using Dapper;
using ECommerce.Application.Abstractions.Data;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Application.Abstractions.Outbox;
using ECommerce.Application.Abstractions.Paging;
using ECommerce.Application.Catalog;
using ECommerce.Application.Notifications;
using ECommerce.Domain.Abstractions;
using ECommerce.Domain.Identity;
using ECommerce.Domain.Inventory;
using ECommerce.Domain.Ordering;
using FluentValidation;

namespace ECommerce.Application.Ordering;

// RF-041 (meus pedidos), RF-042 (detalhe), RF-043 (cancelar), RF-046 (48 h e lembrete de 24 h) e o valor da embalagem.

/// <summary><see cref="ItemCount"/>: peças diferentes no pedido, para "Anel Liso e mais 2".</summary>
public sealed record OrderSummaryResponse(
    Guid Id,
    string Number,
    string Status,
    DateTime PlacedAt,
    DateTime PaymentDueAt,
    decimal Total,
    int ItemCount,
    string FirstProductName,
    string? FirstImageUrl);

public sealed record OrderItemResponse(
    Guid VariantId,
    Guid ProductId,
    string Sku,
    string ProductName,
    string? ColorName,
    string? SizeName,
    string? ImageUrl,
    decimal UnitPrice,
    int Quantity,
    decimal LineTotal);

public sealed record OrderAddressResponse(
    string Recipient, string PostalCode, string Street, string Number, string? Complement, string District, string City, string State);

public sealed record OrderShippingResponse(string ServiceCode, string Name, decimal Price, int? DeliveryDays, bool IsFallback, string? TrackingCode);

public sealed record OrderGiftResponse(bool IsGift, bool Wrap, string? Message, decimal WrapPrice);

public sealed record OrderHistoryResponse(string Status, DateTime ChangedAt);

/// <summary><see cref="CanCancel"/>: o site oferece o botão (RF-043 CA1); a regra que vale é a do servidor.</summary>
public sealed record OrderResponse(
    Guid Id,
    string Number,
    string Status,
    string? CancellationReason,
    DateTime PlacedAt,
    DateTime PaymentDueAt,
    IReadOnlyList<OrderItemResponse> Items,
    decimal Subtotal,
    decimal ShippingFee,
    decimal GiftWrapPrice,
    decimal Total,
    OrderShippingResponse Shipping,
    OrderAddressResponse Address,
    OrderGiftResponse Gift,
    string? Notes,
    IReadOnlyList<OrderHistoryResponse> History,
    bool CanCancel);

public sealed record OrderingSettingsResponse(decimal GiftWrapPrice);

public sealed record ListMyOrdersQuery(UserId CustomerId, int Page = 1, int PageSize = 10) : IQuery<PagedResponse<OrderSummaryResponse>>;

public sealed record GetMyOrderQuery(UserId CustomerId, Guid OrderId) : IQuery<OrderResponse>;

public sealed record CancelMyOrderCommand(UserId CustomerId, Guid OrderId) : ICommand;

/// <summary>Uma rodada do job de 48 h: devolve quantos pedidos cancelou.</summary>
public sealed record CancelOverdueOrdersCommand(int BatchSize = 50) : ICommand<int>;

/// <summary>Uma rodada do lembrete de 24 h: devolve quantos lembretes gravou.</summary>
public sealed record SendPaymentRemindersCommand(int BatchSize = 50) : ICommand<int>;

/// <summary>Valor público da embalagem: o checkout mostra antes de a cliente escolher (RF-047 CA2).</summary>
public sealed record GetOrderingSettingsQuery : IQuery<OrderingSettingsResponse>;

public sealed record SaveOrderingSettingsCommand(decimal GiftWrapPrice) : ICommand;

internal sealed class ListMyOrdersValidator : AbstractValidator<ListMyOrdersQuery>
{
    public ListMyOrdersValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, 50);
    }
}

internal sealed class SaveOrderingSettingsValidator : AbstractValidator<SaveOrderingSettingsCommand>
{
    public SaveOrderingSettingsValidator()
    {
        RuleFor(x => x.GiftWrapPrice)
            .InclusiveBetween(0m, 1000m)
            .Must(price => decimal.Round(price, 2) == price)
            .WithMessage(OrderErrors.InvalidGiftWrapPrice.Message);
    }
}

internal sealed class ListMyOrdersHandler(IDbConnectionFactory connectionFactory)
    : IQueryHandler<ListMyOrdersQuery, PagedResponse<OrderSummaryResponse>>
{
    private const string Sql = """
        SELECT count(*) FROM ordering.orders WHERE customer_id = @CustomerId;

        SELECT o.id, o.number, o.status, o.placed_at, o.payment_due_at, o.total,
               (SELECT count(*) FROM ordering.order_items i WHERE i.order_id = o.id) AS item_count,
               first_item.product_name AS first_product_name, first_item.image_url AS first_image_url
          FROM ordering.orders o
          LEFT JOIN LATERAL (
                SELECT i.product_name, i.image_url FROM ordering.order_items i
                 WHERE i.order_id = o.id ORDER BY i.position LIMIT 1) first_item ON true
         WHERE o.customer_id = @CustomerId
         ORDER BY o.placed_at DESC, o.id DESC
         LIMIT @PageSize OFFSET @Offset;
        """;

    public async Task<Result<PagedResponse<OrderSummaryResponse>>> Handle(ListMyOrdersQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        using var grid = await connection.QueryMultipleAsync(new CommandDefinition(
            Sql,
            new { CustomerId = query.CustomerId.Value, query.PageSize, Offset = (query.Page - 1) * query.PageSize },
            cancellationToken: cancellationToken));

        var total = await grid.ReadSingleAsync<int>();
        var items = (await grid.ReadAsync<Row>())
            .Select(row => new OrderSummaryResponse(
                row.Id,
                Order.FormatNumber(row.Number),
                row.Status,
                Utc(row.PlacedAt),
                Utc(row.PaymentDueAt),
                row.Total,
                row.ItemCount,
                row.FirstProductName ?? string.Empty,
                row.FirstImageUrl))
            .ToList();

        return PagedResponse.Create<OrderSummaryResponse>(items, query.Page, query.PageSize, total);
    }

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private sealed class Row
    {
        public Guid Id { get; init; }
        public long Number { get; init; }
        public string Status { get; init; } = string.Empty;
        public DateTime PlacedAt { get; init; }
        public DateTime PaymentDueAt { get; init; }
        public decimal Total { get; init; }
        public int ItemCount { get; init; }
        public string? FirstProductName { get; init; }
        public string? FirstImageUrl { get; init; }
    }
}

internal sealed class GetMyOrderHandler(IDbConnectionFactory connectionFactory) : IQueryHandler<GetMyOrderQuery, OrderResponse>
{
    private const string Sql = """
        SELECT id, number, status, cancellation_reason, placed_at, payment_due_at, subtotal, shipping_fee, gift_wrap_price, total,
               shipping_service, shipping_service_name, shipping_days, shipping_is_fallback, tracking_code,
               ship_recipient, ship_postal_code, ship_street, ship_number, ship_complement, ship_district, ship_city, ship_state,
               is_gift, gift_wrap, gift_message, notes
          FROM ordering.orders
         WHERE id = @OrderId AND customer_id = @CustomerId;

        SELECT variant_id, product_id, sku, product_name, color_name, size_name, image_url, unit_price, quantity
          FROM ordering.order_items
         WHERE order_id = @OrderId
         ORDER BY position;

        SELECT to_status, changed_at
          FROM ordering.order_status_history
         WHERE order_id = @OrderId
         ORDER BY changed_at, id;
        """;

    public async Task<Result<OrderResponse>> Handle(GetMyOrderQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        using var grid = await connection.QueryMultipleAsync(new CommandDefinition(
            Sql, new { query.OrderId, CustomerId = query.CustomerId.Value }, cancellationToken: cancellationToken));

        var order = await grid.ReadSingleOrDefaultAsync<OrderRow>();
        if (order is null)
        {
            // Pedido de outro cliente responde igual a pedido que não existe (RF-042).
            return OrderErrors.OrderNotFound;
        }

        var items = (await grid.ReadAsync<ItemRow>())
            .Select(item => new OrderItemResponse(
                item.VariantId, item.ProductId, item.Sku, item.ProductName, item.ColorName, item.SizeName, item.ImageUrl,
                item.UnitPrice, item.Quantity, item.UnitPrice * item.Quantity))
            .ToList();
        var history = (await grid.ReadAsync<HistoryRow>())
            .Select(change => new OrderHistoryResponse(change.ToStatus, DateTime.SpecifyKind(change.ChangedAt, DateTimeKind.Utc)))
            .ToList();

        return new OrderResponse(
            order.Id,
            Order.FormatNumber(order.Number),
            order.Status,
            order.CancellationReason,
            DateTime.SpecifyKind(order.PlacedAt, DateTimeKind.Utc),
            DateTime.SpecifyKind(order.PaymentDueAt, DateTimeKind.Utc),
            items,
            order.Subtotal,
            order.ShippingFee,
            order.GiftWrapPrice,
            order.Total,
            new OrderShippingResponse(order.ShippingService, order.ShippingServiceName, order.ShippingFee, order.ShippingDays, order.ShippingIsFallback, order.TrackingCode),
            new OrderAddressResponse(
                order.ShipRecipient, order.ShipPostalCode, order.ShipStreet, order.ShipNumber, order.ShipComplement, order.ShipDistrict, order.ShipCity, order.ShipState),
            new OrderGiftResponse(order.IsGift, order.GiftWrap, order.GiftMessage, order.GiftWrapPrice),
            order.Notes,
            history,
            CanCancel: order.Status == nameof(OrderStatus.PendingPayment));
    }

    private sealed class OrderRow
    {
        public Guid Id { get; init; }
        public long Number { get; init; }
        public string Status { get; init; } = string.Empty;
        public string? CancellationReason { get; init; }
        public DateTime PlacedAt { get; init; }
        public DateTime PaymentDueAt { get; init; }
        public decimal Subtotal { get; init; }
        public decimal ShippingFee { get; init; }
        public decimal GiftWrapPrice { get; init; }
        public decimal Total { get; init; }
        public string ShippingService { get; init; } = string.Empty;
        public string ShippingServiceName { get; init; } = string.Empty;
        public short? ShippingDays { get; init; }
        public bool ShippingIsFallback { get; init; }
        public string? TrackingCode { get; init; }
        public string ShipRecipient { get; init; } = string.Empty;
        public string ShipPostalCode { get; init; } = string.Empty;
        public string ShipStreet { get; init; } = string.Empty;
        public string ShipNumber { get; init; } = string.Empty;
        public string? ShipComplement { get; init; }
        public string ShipDistrict { get; init; } = string.Empty;
        public string ShipCity { get; init; } = string.Empty;
        public string ShipState { get; init; } = string.Empty;
        public bool IsGift { get; init; }
        public bool GiftWrap { get; init; }
        public string? GiftMessage { get; init; }
        public string? Notes { get; init; }
    }

    private sealed class ItemRow
    {
        public Guid VariantId { get; init; }
        public Guid ProductId { get; init; }
        public string Sku { get; init; } = string.Empty;
        public string ProductName { get; init; } = string.Empty;
        public string? ColorName { get; init; }
        public string? SizeName { get; init; }
        public string? ImageUrl { get; init; }
        public decimal UnitPrice { get; init; }
        public int Quantity { get; init; }
    }

    private sealed class HistoryRow
    {
        public string ToStatus { get; init; } = string.Empty;
        public DateTime ChangedAt { get; init; }
    }
}

/// <summary>Cancelar devolve o estoque e avisa a cliente (EM-10); usado pela cliente e pelo job de 48 h.</summary>
internal static class OrderCancellation
{
    public static async Task<Result> CancelAsync(
        Order order,
        CancellationReason reason,
        UserId? by,
        IOrderRepository orders,
        IStockItemRepository stock,
        IUnitOfWork unitOfWork,
        IOutbox outbox,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var cancelled = order.Cancel(reason, by, nowUtc);
        if (cancelled.IsFailure)
        {
            return cancelled;
        }

        if (!await orders.UpdateAsync(order, cancellationToken))
        {
            return Result.Failure(OrderErrors.InvalidTransition);
        }

        var availabilityChanged = false;
        foreach (var line in order.Items.OrderBy(item => item.VariantId.Value))
        {
            var item = await stock.GetForUpdateAsync(line.VariantId, cancellationToken);
            if (item is null)
            {
                continue;
            }

            var returned = item.ReturnFromOrder(line.Quantity, order.Id, $"Pedido {order.Code} cancelado", by, nowUtc);
            if (returned.IsFailure)
            {
                return returned;
            }

            await stock.SaveAsync(item, cancellationToken);
            availabilityChanged |= item.AvailabilityChanged;
        }

        if (availabilityChanged)
        {
            await CatalogChanged.AvailabilityAsync(outbox, cancellationToken);
        }

        await OrderEmails.EnqueueAsync(unitOfWork, outbox, EmailTemplates.OrderCancelled, order, cancellationToken);
        return Result.Success();
    }
}

internal sealed class CancelMyOrderHandler(
    IOrderRepository orders, IStockItemRepository stock, IUnitOfWork unitOfWork, IOutbox outbox, TimeProvider time)
    : ICommandHandler<CancelMyOrderCommand>
{
    public async Task<Result> Handle(CancelMyOrderCommand command, CancellationToken cancellationToken)
    {
        var order = await orders.GetForUpdateAsync(command.OrderId, command.CustomerId, cancellationToken);
        if (order is null)
        {
            return Result.Failure(OrderErrors.OrderNotFound);
        }

        // Pedido pago precisa de estorno, que chega na fatia 4.5 (entrega 4 §3, P14).
        if (order.Status != OrderStatus.PendingPayment)
        {
            return Result.Failure(OrderErrors.CannotCancel);
        }

        return await OrderCancellation.CancelAsync(
            order, CancellationReason.CustomerRequest, command.CustomerId, orders, stock, unitOfWork, outbox, time.GetUtcNow().UtcDateTime, cancellationToken);
    }
}

/// <summary>RF-046 CA1: pedido sem pagamento em 48 h é cancelado pelo sistema e o estoque volta.</summary>
internal sealed class CancelOverdueOrdersHandler(
    IOrderRepository orders, IStockItemRepository stock, IUnitOfWork unitOfWork, IOutbox outbox, TimeProvider time)
    : ICommandHandler<CancelOverdueOrdersCommand, int>
{
    public async Task<Result<int>> Handle(CancelOverdueOrdersCommand command, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var overdue = await orders.LockOverdueAsync(now, command.BatchSize, cancellationToken);
        var cancelled = 0;
        foreach (var order in overdue)
        {
            var result = await OrderCancellation.CancelAsync(
                order, CancellationReason.PaymentTimeout, null, orders, stock, unitOfWork, outbox, now, cancellationToken);
            if (result.IsFailure)
            {
                return result.Error;
            }

            cancelled++;
        }

        return cancelled;
    }
}

/// <summary>RF-046 CA2: lembrete com 24 h sem pagamento, uma vez por pedido.</summary>
internal sealed class SendPaymentRemindersHandler(IOrderRepository orders, IUnitOfWork unitOfWork, IOutbox outbox, TimeProvider time)
    : ICommandHandler<SendPaymentRemindersCommand, int>
{
    public async Task<Result<int>> Handle(SendPaymentRemindersCommand command, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var due = await orders.LockDueForReminderAsync(now, command.BatchSize, cancellationToken);
        var sent = 0;
        foreach (var order in due)
        {
            if (!order.MarkReminderSent(now) || !await orders.UpdateAsync(order, cancellationToken))
            {
                continue;
            }

            await OrderEmails.EnqueueAsync(unitOfWork, outbox, EmailTemplates.PaymentReminder, order, cancellationToken);
            sent++;
        }

        return sent;
    }
}

internal sealed class GetOrderingSettingsHandler(IDbConnectionFactory connectionFactory) : IQueryHandler<GetOrderingSettingsQuery, OrderingSettingsResponse>
{
    public async Task<Result<OrderingSettingsResponse>> Handle(GetOrderingSettingsQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var price = await connection.ExecuteScalarAsync<decimal>(new CommandDefinition(
            "SELECT coalesce((SELECT gift_wrap_price FROM ordering.settings WHERE id = 1), 0)", cancellationToken: cancellationToken));
        return new OrderingSettingsResponse(price);
    }
}

internal sealed class SaveOrderingSettingsHandler(IOrderRepository orders, TimeProvider time) : ICommandHandler<SaveOrderingSettingsCommand>
{
    public async Task<Result> Handle(SaveOrderingSettingsCommand command, CancellationToken cancellationToken)
    {
        await orders.SaveGiftWrapPriceAsync(command.GiftWrapPrice, time.GetUtcNow().UtcDateTime, cancellationToken);
        return Result.Success();
    }
}
