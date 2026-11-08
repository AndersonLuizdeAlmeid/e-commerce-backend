using Dapper;
using ECommerce.Application.Abstractions.Data;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Application.Abstractions.Outbox;
using ECommerce.Application.Catalog;
using ECommerce.Application.Notifications;
using ECommerce.Application.Shipping;
using ECommerce.Domain.Abstractions;
using ECommerce.Domain.Carts;
using ECommerce.Domain.Catalog;
using ECommerce.Domain.Customers;
using ECommerce.Domain.Identity;
using ECommerce.Domain.Inventory;
using ECommerce.Domain.Ordering;
using FluentValidation;

namespace ECommerce.Application.Ordering;

// RF-040 (checkout) e RF-047 (presente). O pedido nasce da sacola guardada na conta, nunca de preços vindos do navegador.

public sealed record PlaceOrderCommand(
    UserId CustomerId,
    Guid AddressId,
    string ShippingServiceCode,
    bool IsGift,
    bool GiftWrap,
    string? GiftMessage,
    string? Notes,
    string? IdempotencyKey) : ICommand<PlacedOrderResponse>;

/// <summary><see cref="Replayed"/>: a mesma chave de idempotência já tinha criado este pedido (RF-040 CA7).</summary>
public sealed record PlacedOrderResponse(Guid Id, string Number, decimal Total, DateTime PaymentDueAt, bool Replayed);

internal sealed class PlaceOrderValidator : AbstractValidator<PlaceOrderCommand>
{
    public const int IdempotencyKeyMaxLength = 100;

    public PlaceOrderValidator()
    {
        RuleFor(x => x.AddressId).NotEmpty().WithMessage("Escolha o endereço de entrega.");
        RuleFor(x => x.ShippingServiceCode).NotEmpty().WithMessage("Escolha o frete.").MaximumLength(40);
        RuleFor(x => x.GiftMessage)
            .Must(message => OrderGift.Create(true, false, message).IsSuccess)
            .WithMessage(OrderErrors.InvalidGiftMessage.Message)
            .When(x => x.IsGift);
        RuleFor(x => x.Notes)
            .MaximumLength(Order.NotesMaxLength)
            .WithMessage(OrderErrors.InvalidNotes.Message);
        RuleFor(x => x.IdempotencyKey)
            .MaximumLength(IdempotencyKeyMaxLength)
            .WithMessage($"O cabeçalho Idempotency-Key tem até {IdempotencyKeyMaxLength} caracteres.");
    }
}

/// <summary>
/// Tudo numa transação (RF-040 CA4): trava a sacola (duas abas do mesmo cliente fazem fila), recalcula o frete (CA3),
/// trava os saldos em ordem de variação (sem deadlock entre clientes), cria o pedido, baixa o estoque, esvazia a sacola
/// e grava o EM-04 no Outbox. Qualquer falha devolve o erro e o decorator desfaz tudo (CA2).
/// </summary>
internal sealed class PlaceOrderHandler(
    IUnitOfWork unitOfWork,
    ICartRepository carts,
    IAddressBookRepository addresses,
    IStockItemRepository stock,
    IOrderRepository orders,
    IQueryHandler<GetShippingQuotesQuery, ShippingQuotesResponse> shippingQuotes,
    IOutbox outbox,
    TimeProvider time) : ICommandHandler<PlaceOrderCommand, PlacedOrderResponse>
{
    private const string ItemsSql = $"""
        SELECT v.id AS variant_id, p.id AS product_id, v.sku, p.name AS product_name,
               co.name AS color_name, s.name AS size_name,
               coalesce(v.price, p.base_price) AS unit_price,
               v.is_active AND p.is_active AND c.is_active AS is_purchasable,
               image.url AS image_url
          FROM catalog.product_variants v
          JOIN catalog.products p ON p.id = v.product_id
          JOIN catalog.categories c ON c.id = p.category_id
          LEFT JOIN catalog.colors co ON co.id = v.color_id
          LEFT JOIN catalog.sizes s ON s.id = v.size_id
          LEFT JOIN LATERAL (
                SELECT i.url FROM catalog.product_images i
                 WHERE i.product_id = p.id
                 ORDER BY i.color_id IS NOT DISTINCT FROM v.color_id DESC, i.position LIMIT 1) image ON true
         WHERE v.id = ANY(@Ids)
        """;

    public async Task<Result<PlacedOrderResponse>> Handle(PlaceOrderCommand command, CancellationToken cancellationToken)
    {
        if (!await EmailConfirmedAsync(command.CustomerId, cancellationToken))
        {
            return OrderErrors.EmailNotConfirmed;
        }

        var now = time.GetUtcNow().UtcDateTime;
        var cart = await carts.GetOrCreateForUpdateAsync(command.CustomerId, now, cancellationToken);

        // Depois da trava da sacola: a segunda requisição com a mesma chave espera a primeira e devolve o pedido dela.
        if (!string.IsNullOrWhiteSpace(command.IdempotencyKey)
            && await orders.FindByIdempotencyKeyAsync(command.CustomerId, command.IdempotencyKey, cancellationToken) is { } existingId)
        {
            var existing = await orders.GetForUpdateAsync(existingId, command.CustomerId, cancellationToken);
            return new PlacedOrderResponse(existing!.Id, existing.Code, existing.Total, existing.PaymentDueAtUtc, Replayed: true);
        }

        if (cart.Items.Count == 0)
        {
            return OrderErrors.EmptyCart;
        }

        var book = await addresses.GetForUpdateAsync(command.CustomerId, cancellationToken);
        var address = book?.Addresses.FirstOrDefault(candidate => candidate.Id == command.AddressId);
        if (address is null)
        {
            return OrderErrors.AddressNotFound;
        }

        var gift = OrderGift.Create(command.IsGift, command.GiftWrap, command.GiftMessage);
        if (gift.IsFailure)
        {
            return gift.Error;
        }

        var rows = (await unitOfWork.Connection.QueryAsync<ItemRow>(new CommandDefinition(
                ItemsSql,
                new { Ids = cart.Items.Select(item => item.VariantId.Value).ToArray() },
                unitOfWork.Transaction,
                cancellationToken: cancellationToken)))
            .ToDictionary(row => row.VariantId);

        // Peça que saiu da loja entra como esgotada: o cliente volta à sacola, que já mostra o aviso.
        var unavailable = cart.Items
            .Where(item => !rows.TryGetValue(item.VariantId.Value, out var row) || !row.IsPurchasable)
            .Select(item => new OutOfStockItem(item.VariantId.Value, rows.GetValueOrDefault(item.VariantId.Value)?.ProductName ?? "Peça fora da loja", item.Quantity, 0))
            .ToList();
        if (unavailable.Count > 0)
        {
            return new OutOfStockError(unavailable);
        }

        var shipping = await QuoteAsync(command, address.Content.PostalCode.Value, cart, cancellationToken);
        if (shipping.IsFailure)
        {
            return shipping.Error;
        }

        var lockedStock = new List<(CartItem Line, StockItem Stock)>();
        foreach (var line in cart.Items.OrderBy(item => item.VariantId.Value))
        {
            var item = await stock.GetForUpdateAsync(line.VariantId, cancellationToken);
            lockedStock.Add((line, item ?? StockItem.Rehydrate(line.VariantId, 0, now)));
        }

        var outOfStock = lockedStock
            .Where(pair => pair.Stock.QuantityOnHand < pair.Line.Quantity)
            .Select(pair => new OutOfStockItem(pair.Line.VariantId.Value, rows[pair.Line.VariantId.Value].ProductName, pair.Line.Quantity, pair.Stock.QuantityOnHand))
            .ToList();
        if (outOfStock.Count > 0)
        {
            return new OutOfStockError(outOfStock);
        }

        var items = cart.Items
            .Select(line => rows[line.VariantId.Value])
            .Select(row => new OrderItem(
                new VariantId(row.VariantId),
                new ProductId(row.ProductId),
                row.Sku,
                row.ProductName,
                row.ColorName,
                row.SizeName,
                row.ImageUrl,
                row.UnitPrice,
                cart.Items.First(line => line.VariantId.Value == row.VariantId).Quantity))
            .ToList();

        var content = address.Content;
        var placed = Order.Place(
            command.CustomerId,
            items,
            new ShippingAddress(content.Recipient, content.PostalCode, content.Street, content.Number, content.Complement, content.District, content.City, content.State),
            shipping.Value,
            gift.Value,
            gift.Value.Wrap ? await orders.GetGiftWrapPriceAsync(cancellationToken) : 0m,
            command.Notes,
            now);
        if (placed.IsFailure)
        {
            return placed.Error;
        }

        var order = placed.Value;
        await orders.AddAsync(order, string.IsNullOrWhiteSpace(command.IdempotencyKey) ? null : command.IdempotencyKey, cancellationToken);

        foreach (var (line, item) in lockedStock)
        {
            var reserved = item.ReserveForOrder(line.Quantity, order.Id, $"Pedido {order.Code}", command.CustomerId, now);
            if (reserved.IsFailure)
            {
                return reserved.Error;
            }

            await stock.SaveAsync(item, cancellationToken);
        }

        // Uma revalidação só, mesmo que várias peças tenham esgotado com a compra (entrega 3 §3, P12).
        if (lockedStock.Any(pair => pair.Stock.AvailabilityChanged))
        {
            await CatalogChanged.AvailabilityAsync(outbox, cancellationToken);
        }

        cart.Clear(now);
        await carts.SaveAsync(cart, cancellationToken);

        await OrderEmails.EnqueueAsync(unitOfWork, outbox, EmailTemplates.OrderPlaced, order, cancellationToken);

        return new PlacedOrderResponse(order.Id, order.Code, order.Total, order.PaymentDueAtUtc, Replayed: false);
    }

    private Task<bool> EmailConfirmedAsync(UserId customerId, CancellationToken cancellationToken) =>
        unitOfWork.Connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT coalesce((SELECT email_confirmed_at IS NOT NULL FROM identity.users WHERE id = @Id), false)",
            new { Id = customerId.Value },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));

    /// <summary>
    /// Mesmo cálculo da tela (RF-040 CA3): provedor, regras e contingência. O serviço escolhido precisa continuar na
    /// cotação; o preço é o de agora, não o que a tela mostrou.
    /// </summary>
    private async Task<Result<ShippingSelection>> QuoteAsync(PlaceOrderCommand command, string postalCode, Cart cart, CancellationToken cancellationToken)
    {
        var quotes = await shippingQuotes.Handle(
            new GetShippingQuotesQuery(postalCode, cart.Items.Select(item => new ShippingQuoteItem(item.VariantId.Value, item.Quantity)).ToList()),
            cancellationToken);
        if (quotes.IsFailure)
        {
            return quotes.Error;
        }

        var option = quotes.Value.Options.FirstOrDefault(candidate =>
            string.Equals(candidate.ServiceCode, command.ShippingServiceCode, StringComparison.OrdinalIgnoreCase));
        return option is null
            ? OrderErrors.ShippingServiceUnavailable
            : new ShippingSelection(option.ServiceCode, option.Name, option.Price, option.DeliveryDays, quotes.Value.IsFallback);
    }

    private sealed class ItemRow
    {
        public Guid VariantId { get; init; }
        public Guid ProductId { get; init; }
        public string Sku { get; init; } = string.Empty;
        public string ProductName { get; init; } = string.Empty;
        public string? ColorName { get; init; }
        public string? SizeName { get; init; }
        public decimal UnitPrice { get; init; }
        public bool IsPurchasable { get; init; }
        public string? ImageUrl { get; init; }
    }
}
