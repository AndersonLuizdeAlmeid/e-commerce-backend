using Dapper;
using ECommerce.Application.Abstractions.Data;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Application.Catalog;
using ECommerce.Domain.Abstractions;
using ECommerce.Domain.Carts;
using ECommerce.Domain.Catalog;
using ECommerce.Domain.Identity;
using FluentValidation;

namespace ECommerce.Application.Carts;

// RF-030 (sacola), RF-031 (mescla) e a leitura da sacola do visitante, que só guarda variação e quantidade no navegador.

/// <summary><c>Available</c>, <c>InsufficientStock</c>, <c>OutOfStock</c> ou <c>Unavailable</c> (saiu da loja).</summary>
public static class CartItemStatus
{
    public const string Available = "Available";
    public const string InsufficientStock = "InsufficientStock";
    public const string OutOfStock = "OutOfStock";
    public const string Unavailable = "Unavailable";
}

public sealed record CartItemResponse(
    Guid VariantId,
    Guid ProductId,
    string ProductName,
    string ProductSlug,
    string? ColorName,
    string? SizeName,
    VariantLabelsResponse VariantLabels,
    string? ImageUrl,
    string? ImageAlt,
    decimal UnitPrice,
    int Quantity,
    decimal LineTotal,
    string Status,
    int? AvailableQuantity);

/// <summary><see cref="Subtotal"/> só conta o que pode ser comprado; o preço é sempre o atual (RF-030 CA3).</summary>
public sealed record CartResponse(IReadOnlyList<CartItemResponse> Items, decimal Subtotal);

public sealed record MergeCartResponse(CartResponse Cart, IReadOnlyList<Guid> SkippedVariantIds);

public sealed record CartLine(Guid VariantId, int Quantity);

public sealed record GetCartQuery(UserId CustomerId) : IQuery<CartResponse>;

/// <summary>A sacola do visitante vive no navegador; preço, foto e disponibilidade vêm daqui (arquivo 16 §6).</summary>
public sealed record LookupCartQuery(IReadOnlyList<CartLine> Items) : IQuery<CartResponse>;

public sealed record AddCartItemCommand(UserId CustomerId, Guid VariantId, int Quantity) : ICommand;

public sealed record SetCartItemQuantityCommand(UserId CustomerId, Guid VariantId, int Quantity) : ICommand;

public sealed record RemoveCartItemCommand(UserId CustomerId, Guid VariantId) : ICommand;

public sealed record ClearCartCommand(UserId CustomerId) : ICommand;

/// <summary>Devolve as variações que não entraram: fora da loja ou além do limite de peças diferentes.</summary>
public sealed record MergeCartCommand(UserId CustomerId, IReadOnlyList<CartLine> Items) : ICommand<IReadOnlyList<Guid>>;

internal sealed class CartLineValidator : AbstractValidator<CartLine>
{
    public CartLineValidator()
    {
        RuleFor(x => x.VariantId).NotEmpty();
        RuleFor(x => x.Quantity).InclusiveBetween(1, Cart.MaxQuantityPerItem).WithMessage($"A quantidade por peça vai de 1 a {Cart.MaxQuantityPerItem}.");
    }
}

internal sealed class LookupCartValidator : AbstractValidator<LookupCartQuery>
{
    public LookupCartValidator()
    {
        RuleFor(x => x.Items).Must(items => items.Count <= Cart.MaxItems).WithMessage($"A sacola tem até {Cart.MaxItems} peças diferentes.");
        RuleForEach(x => x.Items).SetValidator(new CartLineValidator());
    }
}

internal sealed class MergeCartValidator : AbstractValidator<MergeCartCommand>
{
    public MergeCartValidator()
    {
        RuleFor(x => x.Items).Must(items => items.Count <= Cart.MaxItems).WithMessage($"A sacola tem até {Cart.MaxItems} peças diferentes.");
        RuleForEach(x => x.Items).SetValidator(new CartLineValidator());
    }
}

internal sealed class AddCartItemValidator : AbstractValidator<AddCartItemCommand>
{
    public AddCartItemValidator()
    {
        RuleFor(x => x.VariantId).NotEmpty();
        RuleFor(x => x.Quantity).InclusiveBetween(1, Cart.MaxQuantityPerItem).WithMessage($"A quantidade por peça vai de 1 a {Cart.MaxQuantityPerItem}.");
    }
}

internal sealed class SetCartItemQuantityValidator : AbstractValidator<SetCartItemQuantityCommand>
{
    public SetCartItemQuantityValidator()
    {
        RuleFor(x => x.Quantity).InclusiveBetween(1, Cart.MaxQuantityPerItem).WithMessage($"A quantidade por peça vai de 1 a {Cart.MaxQuantityPerItem}.");
    }
}

internal static class CartView
{
    // Fora do cache de propósito: preço e saldo da sacola são sempre os de agora (arquivo 12 §2).
    private const string Sql = $"""
        SELECT v.id AS variant_id, p.id AS product_id, p.name AS product_name, p.slug AS product_slug,
               co.name AS color_name, s.name AS size_name, {VariantLabelsResponse.SqlColumns},
               coalesce(v.price, p.base_price) AS unit_price,
               coalesce(st.quantity_on_hand, 0) AS quantity_on_hand,
               v.is_active AND p.is_active AND c.is_active AS is_purchasable,
               image.url AS image_url, image.alt AS image_alt
          FROM catalog.product_variants v
          JOIN catalog.products p ON p.id = v.product_id
          JOIN catalog.categories c ON c.id = p.category_id
          {VariantLabelsResponse.SqlParentJoin}
          LEFT JOIN catalog.colors co ON co.id = v.color_id
          LEFT JOIN catalog.sizes s ON s.id = v.size_id
          LEFT JOIN inventory.stock_items st ON st.variant_id = v.id
          LEFT JOIN LATERAL (
                SELECT i.url, i.alt FROM catalog.product_images i
                 WHERE i.product_id = p.id
                 ORDER BY i.color_id IS NOT DISTINCT FROM v.color_id DESC, i.position LIMIT 1) image ON true
         WHERE v.id = ANY(@Ids)
        """;

    /// <summary>Mantém a ordem pedida. Variação que não existe mais fica de fora: quem chamou a tira da sacola.</summary>
    public static async Task<CartResponse> BuildAsync(IDbConnectionFactory connectionFactory, IReadOnlyList<CartLine> lines, CancellationToken cancellationToken)
    {
        if (lines.Count == 0)
        {
            return new CartResponse([], 0m);
        }

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = (await connection.QueryAsync<Row>(new CommandDefinition(
            Sql, new { Ids = lines.Select(line => line.VariantId).Distinct().ToArray() }, cancellationToken: cancellationToken)))
            .ToDictionary(row => row.VariantId);

        var items = lines
            .Where(line => rows.ContainsKey(line.VariantId))
            .Select(line => ToItem(rows[line.VariantId], line.Quantity))
            .ToList();

        var subtotal = items
            .Where(item => item.Status is CartItemStatus.Available or CartItemStatus.InsufficientStock)
            .Sum(item => item.LineTotal);

        return new CartResponse(items, subtotal);
    }

    private static CartItemResponse ToItem(Row row, int quantity)
    {
        var status = !row.IsPurchasable ? CartItemStatus.Unavailable
            : row.QuantityOnHand == 0 ? CartItemStatus.OutOfStock
            : row.QuantityOnHand < quantity ? CartItemStatus.InsufficientStock
            : CartItemStatus.Available;

        return new CartItemResponse(
            row.VariantId, row.ProductId, row.ProductName, row.ProductSlug, row.ColorName, row.SizeName,
            new VariantLabelsResponse(row.ColorLabel, row.SizeLabel), row.ImageUrl, row.ImageAlt,
            row.UnitPrice, quantity, row.UnitPrice * quantity, status,
            status == CartItemStatus.InsufficientStock ? row.QuantityOnHand : null);
    }

    private sealed class Row
    {
        public Guid VariantId { get; init; }
        public Guid ProductId { get; init; }
        public string ProductName { get; init; } = string.Empty;
        public string ProductSlug { get; init; } = string.Empty;
        public string? ColorName { get; init; }
        public string? SizeName { get; init; }
        public string ColorLabel { get; init; } = string.Empty;
        public string SizeLabel { get; init; } = string.Empty;
        public decimal UnitPrice { get; init; }
        public int QuantityOnHand { get; init; }
        public bool IsPurchasable { get; init; }
        public string? ImageUrl { get; init; }
        public string? ImageAlt { get; init; }
    }
}

internal sealed class GetCartHandler(IDbConnectionFactory connectionFactory) : IQueryHandler<GetCartQuery, CartResponse>
{
    private const string Sql = """
        SELECT i.variant_id, i.quantity
          FROM carts.cart_items i
          JOIN carts.carts c ON c.id = i.cart_id
         WHERE c.customer_id = @CustomerId
         ORDER BY i.added_at, i.variant_id
        """;

    public async Task<Result<CartResponse>> Handle(GetCartQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var lines = (await connection.QueryAsync<LineRow>(new CommandDefinition(Sql, new { CustomerId = query.CustomerId.Value }, cancellationToken: cancellationToken)))
            .Select(row => new CartLine(row.VariantId, row.Quantity))
            .ToList();

        return await CartView.BuildAsync(connectionFactory, lines, cancellationToken);
    }

    private sealed class LineRow
    {
        public Guid VariantId { get; init; }
        public int Quantity { get; init; }
    }
}

internal sealed class LookupCartHandler(IDbConnectionFactory connectionFactory) : IQueryHandler<LookupCartQuery, CartResponse>
{
    public async Task<Result<CartResponse>> Handle(LookupCartQuery query, CancellationToken cancellationToken) =>
        await CartView.BuildAsync(connectionFactory, query.Items, cancellationToken);
}

internal sealed class AddCartItemHandler(ICartRepository carts, TimeProvider time) : ICommandHandler<AddCartItemCommand>
{
    public async Task<Result> Handle(AddCartItemCommand command, CancellationToken cancellationToken)
    {
        var variantId = new VariantId(command.VariantId);
        var purchasable = await carts.FindPurchasableAsync([variantId], cancellationToken);
        if (!purchasable.Contains(variantId))
        {
            return Result.Failure(CartErrors.VariantUnavailable);
        }

        var now = time.GetUtcNow().UtcDateTime;
        var cart = await carts.GetOrCreateForUpdateAsync(command.CustomerId, now, cancellationToken);
        var added = cart.Add(variantId, command.Quantity, now);
        if (added.IsFailure)
        {
            return added;
        }

        await carts.SaveAsync(cart, cancellationToken);
        return Result.Success();
    }
}

internal sealed class SetCartItemQuantityHandler(ICartRepository carts, TimeProvider time) : ICommandHandler<SetCartItemQuantityCommand>
{
    public async Task<Result> Handle(SetCartItemQuantityCommand command, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var cart = await carts.GetOrCreateForUpdateAsync(command.CustomerId, now, cancellationToken);
        var changed = cart.SetQuantity(new VariantId(command.VariantId), command.Quantity, now);
        if (changed.IsFailure)
        {
            return changed;
        }

        await carts.SaveAsync(cart, cancellationToken);
        return Result.Success();
    }
}

internal sealed class RemoveCartItemHandler(ICartRepository carts, TimeProvider time) : ICommandHandler<RemoveCartItemCommand>
{
    public async Task<Result> Handle(RemoveCartItemCommand command, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var cart = await carts.GetOrCreateForUpdateAsync(command.CustomerId, now, cancellationToken);
        cart.Remove(new VariantId(command.VariantId), now);
        await carts.SaveAsync(cart, cancellationToken);
        return Result.Success();
    }
}

internal sealed class ClearCartHandler(ICartRepository carts, TimeProvider time) : ICommandHandler<ClearCartCommand>
{
    public async Task<Result> Handle(ClearCartCommand command, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var cart = await carts.GetOrCreateForUpdateAsync(command.CustomerId, now, cancellationToken);
        cart.Clear(now);
        await carts.SaveAsync(cart, cancellationToken);
        return Result.Success();
    }
}

internal sealed class MergeCartHandler(ICartRepository carts, TimeProvider time) : ICommandHandler<MergeCartCommand, IReadOnlyList<Guid>>
{
    public async Task<Result<IReadOnlyList<Guid>>> Handle(MergeCartCommand command, CancellationToken cancellationToken)
    {
        var lines = command.Items
            .GroupBy(item => new VariantId(item.VariantId))
            .Select(group => (VariantId: group.Key, Quantity: group.Sum(item => item.Quantity)))
            .ToList();

        var purchasable = await carts.FindPurchasableAsync(lines.Select(line => line.VariantId).ToList(), cancellationToken);
        var now = time.GetUtcNow().UtcDateTime;
        var cart = await carts.GetOrCreateForUpdateAsync(command.CustomerId, now, cancellationToken);

        var outOfStore = lines.Where(line => !purchasable.Contains(line.VariantId)).Select(line => line.VariantId);
        var skipped = cart.Merge(lines.Where(line => purchasable.Contains(line.VariantId)), now);

        await carts.SaveAsync(cart, cancellationToken);
        return outOfStore.Concat(skipped).Select(id => id.Value).ToList();
    }
}
