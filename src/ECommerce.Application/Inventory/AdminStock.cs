using Dapper;
using ECommerce.Application.Abstractions.Data;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Application.Abstractions.Outbox;
using ECommerce.Application.Abstractions.Paging;
using ECommerce.Application.Catalog;
using ECommerce.Application.Catalog.Admin;
using ECommerce.Domain.Abstractions;
using ECommerce.Domain.Catalog;
using ECommerce.Domain.Identity;
using ECommerce.Domain.Inventory;
using FluentValidation;

namespace ECommerce.Application.Inventory;

// RF-052 (saldo e movimentações) e RF-059 (entrada por leitura de código de barras).

public sealed record StockItemResponse(
    Guid VariantId,
    Guid ProductId,
    string ProductName,
    string? ImageUrl,
    string? ColorName,
    string? SizeName,
    string Sku,
    string? Barcode,
    int QuantityOnHand,
    int LowStockThreshold,
    bool IsActive);

public sealed record StockMovementResponse(Guid Id, string Type, int Quantity, string? Reason, string? PerformedBy, DateTime CreatedAt);

public sealed record StockLevelResponse(Guid VariantId, int QuantityOnHand);

public sealed record StockScanResponse(
    Guid VariantId,
    Guid ProductId,
    string ProductName,
    string? ColorName,
    string? SizeName,
    string Sku,
    int QuantityOnHand);

/// <summary><see cref="LowStock"/> traz só o que está à venda: peça fora da loja não precisa de reposição.</summary>
public sealed record ListAdminStockQuery(int Page, int PageSize, string? Search, bool LowStock) : IQuery<PagedResponse<StockItemResponse>>
{
    public const int DefaultPageSize = 30;
    public const int MaxPageSize = 100;
}

public sealed record ListStockMovementsQuery(Guid VariantId, int Page, int PageSize) : IQuery<PagedResponse<StockMovementResponse>>
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;
}

/// <summary><see cref="Quantity"/> é o que entra ou sai; em <c>Adjustment</c>, o novo saldo.</summary>
public sealed record MoveStockCommand(Guid VariantId, string Type, int Quantity, string Reason, UserId PerformedBy) : ICommand<StockLevelResponse>;

public sealed record ScanStockCommand(string Barcode, int Quantity, string? Reason, UserId PerformedBy) : ICommand<StockScanResponse>
{
    public const string DefaultReason = "Entrada por leitura";
    public const int MaxQuantity = 9_999;
}

internal sealed class ListAdminStockValidator : AbstractValidator<ListAdminStockQuery>
{
    public ListAdminStockValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, ListAdminStockQuery.MaxPageSize);
        RuleFor(x => x.Search).MaximumLength(100);
    }
}

internal sealed class ListStockMovementsValidator : AbstractValidator<ListStockMovementsQuery>
{
    public ListStockMovementsValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, ListStockMovementsQuery.MaxPageSize);
    }
}

internal sealed class MoveStockValidator : AbstractValidator<MoveStockCommand>
{
    public MoveStockValidator()
    {
        RuleFor(x => x.Type)
            .Must(type => Enum.TryParse<StockMovementType>(type, ignoreCase: false, out var parsed) && StockMovement.ManualTypes.Contains(parsed) && type == parsed.ToString())
            .WithMessage("Escolha entrada, saída ou ajuste.");
        RuleFor(x => x.Quantity)
            .GreaterThan(0).When(x => x.Type != nameof(StockMovementType.Adjustment)).WithMessage("Informe uma quantidade maior que zero.");
        RuleFor(x => x.Quantity)
            .GreaterThanOrEqualTo(0).When(x => x.Type == nameof(StockMovementType.Adjustment)).WithMessage("O novo saldo não pode ser negativo.");
        RuleFor(x => x.Quantity).LessThanOrEqualTo(StockItem.MaxQuantityOnHand).WithMessage($"O saldo vai até {StockItem.MaxQuantityOnHand}.");
        RuleFor(x => x.Reason)
            .Must(reason => !string.IsNullOrWhiteSpace(reason)).WithMessage("Informe o motivo.")
            .MaximumLength(StockMovement.ReasonMaxLength);
    }
}

internal sealed class ScanStockValidator : AbstractValidator<ScanStockCommand>
{
    public ScanStockValidator()
    {
        RuleFor(x => x.Quantity).InclusiveBetween(1, ScanStockCommand.MaxQuantity);
        RuleFor(x => x.Reason).MaximumLength(StockMovement.ReasonMaxLength);
    }
}

internal sealed class ListAdminStockHandler(IDbConnectionFactory connectionFactory)
    : IQueryHandler<ListAdminStockQuery, PagedResponse<StockItemResponse>>
{
    private const string From = """
          FROM inventory.stock_items st
          JOIN catalog.product_variants v ON v.id = st.variant_id
          JOIN catalog.products p ON p.id = v.product_id
          LEFT JOIN catalog.colors co ON co.id = v.color_id
          LEFT JOIN catalog.sizes s ON s.id = v.size_id
         WHERE (NOT @LowStock OR (st.quantity_on_hand <= st.low_stock_threshold AND v.is_active AND p.is_active))
           AND (@Search::text IS NULL
                OR public.immutable_unaccent(lower(p.name)) LIKE '%' || public.immutable_unaccent(lower(@Search)) || '%'
                OR v.sku ILIKE '%' || @Search || '%'
                OR v.barcode = @Search)
        """;

    private const string Sql = $"""
        SELECT count(*)
        {From};

        SELECT st.variant_id, v.product_id, p.name AS product_name, co.name AS color_name, s.name AS size_name, v.sku, v.barcode,
               st.quantity_on_hand, st.low_stock_threshold, v.is_active AND p.is_active AS is_active,
               (SELECT i.url FROM catalog.product_images i
                 WHERE i.product_id = p.id
                 ORDER BY i.color_id IS NOT DISTINCT FROM v.color_id DESC, i.position LIMIT 1) AS image_url
        {From}
         ORDER BY p.name, p.id, co.name NULLS FIRST, s.sort_order NULLS FIRST, v.id
         LIMIT @PageSize OFFSET @Offset;
        """;

    public async Task<Result<PagedResponse<StockItemResponse>>> Handle(ListAdminStockQuery query, CancellationToken cancellationToken)
    {
        var search = string.IsNullOrWhiteSpace(query.Search)
            ? null
            : query.Search.Trim().Replace(@"\", @"\\", StringComparison.Ordinal).Replace("%", @"\%", StringComparison.Ordinal).Replace("_", @"\_", StringComparison.Ordinal);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        using var grid = await connection.QueryMultipleAsync(new CommandDefinition(
            Sql,
            new { query.LowStock, Search = search, query.PageSize, Offset = (query.Page - 1) * query.PageSize },
            cancellationToken: cancellationToken));

        var total = await grid.ReadSingleAsync<int>();
        var items = (await grid.ReadAsync<Row>())
            .Select(r => new StockItemResponse(
                r.VariantId, r.ProductId, r.ProductName, r.ImageUrl, r.ColorName, r.SizeName, r.Sku, r.Barcode, r.QuantityOnHand, r.LowStockThreshold, r.IsActive))
            .ToList();

        return PagedResponse.Create<StockItemResponse>(items, query.Page, query.PageSize, total);
    }

    private sealed class Row
    {
        public Guid VariantId { get; init; }
        public Guid ProductId { get; init; }
        public string ProductName { get; init; } = string.Empty;
        public string? ImageUrl { get; init; }
        public string? ColorName { get; init; }
        public string? SizeName { get; init; }
        public string Sku { get; init; } = string.Empty;
        public string? Barcode { get; init; }
        public int QuantityOnHand { get; init; }
        public int LowStockThreshold { get; init; }
        public bool IsActive { get; init; }
    }
}

internal sealed class ListStockMovementsHandler(IDbConnectionFactory connectionFactory)
    : IQueryHandler<ListStockMovementsQuery, PagedResponse<StockMovementResponse>>
{
    private const string Sql = """
        SELECT EXISTS (SELECT 1 FROM inventory.stock_items WHERE variant_id = @VariantId);

        SELECT count(*) FROM inventory.stock_movements WHERE variant_id = @VariantId;

        SELECT m.id, m.type, m.quantity, m.reason, u.email AS performed_by, m.created_at
          FROM inventory.stock_movements m
          LEFT JOIN identity.users u ON u.id = m.performed_by
         WHERE m.variant_id = @VariantId
         ORDER BY m.created_at DESC, m.id DESC
         LIMIT @PageSize OFFSET @Offset;
        """;

    public async Task<Result<PagedResponse<StockMovementResponse>>> Handle(ListStockMovementsQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        using var grid = await connection.QueryMultipleAsync(new CommandDefinition(
            Sql,
            new { query.VariantId, query.PageSize, Offset = (query.Page - 1) * query.PageSize },
            cancellationToken: cancellationToken));

        if (!await grid.ReadSingleAsync<bool>())
        {
            return CatalogDomainErrors.VariantNotFound;
        }

        var total = await grid.ReadSingleAsync<int>();
        var items = (await grid.ReadAsync<Row>())
            .Select(r => new StockMovementResponse(r.Id, r.Type, r.Quantity, r.Reason, r.PerformedBy, DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc)))
            .ToList();

        return PagedResponse.Create<StockMovementResponse>(items, query.Page, query.PageSize, total);
    }

    private sealed class Row
    {
        public Guid Id { get; init; }
        public string Type { get; init; } = string.Empty;
        public int Quantity { get; init; }
        public string? Reason { get; init; }
        public string? PerformedBy { get; init; }
        public DateTime CreatedAt { get; init; }
    }
}

internal sealed class MoveStockHandler(IStockItemRepository stock, IOutbox outbox, TimeProvider time)
    : ICommandHandler<MoveStockCommand, StockLevelResponse>
{
    public async Task<Result<StockLevelResponse>> Handle(MoveStockCommand command, CancellationToken cancellationToken)
    {
        var item = await stock.GetForUpdateAsync(new VariantId(command.VariantId), cancellationToken);
        if (item is null)
        {
            return CatalogDomainErrors.VariantNotFound;
        }

        var now = time.GetUtcNow().UtcDateTime;
        var moved = Enum.Parse<StockMovementType>(command.Type) switch
        {
            StockMovementType.In => item.Increase(command.Quantity, command.Reason, command.PerformedBy, now),
            StockMovementType.Out => item.Decrease(command.Quantity, command.Reason, command.PerformedBy, now),
            _ => item.AdjustTo(command.Quantity, command.Reason, command.PerformedBy, now),
        };
        if (moved.IsFailure)
        {
            return moved.Error;
        }

        await StockChanges.SaveAsync(item, stock, outbox, cancellationToken);
        return new StockLevelResponse(command.VariantId, item.QuantityOnHand);
    }
}

internal sealed class ScanStockHandler(
    IQueryHandler<FindVariantByBarcodeQuery, VariantByBarcodeResponse> findVariant, IStockItemRepository stock, IOutbox outbox, TimeProvider time)
    : ICommandHandler<ScanStockCommand, StockScanResponse>
{
    public async Task<Result<StockScanResponse>> Handle(ScanStockCommand command, CancellationToken cancellationToken)
    {
        var variant = await findVariant.Handle(new FindVariantByBarcodeQuery(command.Barcode), cancellationToken);
        if (variant.IsFailure)
        {
            return variant.Error;
        }

        var item = await stock.GetForUpdateAsync(new VariantId(variant.Value.VariantId), cancellationToken);
        if (item is null)
        {
            return CatalogDomainErrors.VariantNotFound;
        }

        var reason = string.IsNullOrWhiteSpace(command.Reason) ? ScanStockCommand.DefaultReason : command.Reason;
        var moved = item.Increase(command.Quantity, reason, command.PerformedBy, time.GetUtcNow().UtcDateTime);
        if (moved.IsFailure)
        {
            return moved.Error;
        }

        await StockChanges.SaveAsync(item, stock, outbox, cancellationToken);
        return new StockScanResponse(
            variant.Value.VariantId, variant.Value.ProductId, variant.Value.ProductName, variant.Value.ColorName, variant.Value.SizeName, variant.Value.Sku, item.QuantityOnHand);
    }
}

internal static class StockChanges
{
    /// <summary>A loja só muda quando a variação esgota ou volta; uma sequência de leituras não revalida páginas a cada unidade.</summary>
    public static async Task SaveAsync(StockItem item, IStockItemRepository stock, IOutbox outbox, CancellationToken cancellationToken)
    {
        await stock.SaveAsync(item, cancellationToken);
        if (item.AvailabilityChanged)
        {
            await CatalogChanged.AvailabilityAsync(outbox, cancellationToken);
        }
    }
}
