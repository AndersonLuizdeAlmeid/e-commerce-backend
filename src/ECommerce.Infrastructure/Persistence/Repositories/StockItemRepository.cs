using Dapper;
using ECommerce.Application.Abstractions.Data;
using ECommerce.Domain.Catalog;
using ECommerce.Domain.Inventory;

namespace ECommerce.Infrastructure.Persistence.Repositories;

internal sealed class StockItemRepository(IUnitOfWork unitOfWork) : IStockItemRepository
{
    public async Task<StockItem?> GetForUpdateAsync(VariantId variantId, CancellationToken cancellationToken)
    {
        var row = await unitOfWork.Connection.QuerySingleOrDefaultAsync<Row>(Command(
            "SELECT variant_id, quantity_on_hand, updated_at FROM inventory.stock_items WHERE variant_id = @Id FOR UPDATE",
            new { Id = variantId.Value },
            cancellationToken));

        return row is null ? null : StockItem.Rehydrate(new VariantId(row.VariantId), row.QuantityOnHand, row.UpdatedAt);
    }

    public async Task SaveAsync(StockItem item, CancellationToken cancellationToken)
    {
        if (item.NewMovements.Count == 0)
        {
            return;
        }

        await unitOfWork.Connection.ExecuteAsync(Command(
            """
            UPDATE inventory.stock_items
               SET quantity_on_hand = @QuantityOnHand, version = version + 1, updated_at = @UpdatedAt
             WHERE variant_id = @VariantId
            """,
            new { VariantId = item.VariantId.Value, item.QuantityOnHand, UpdatedAt = item.UpdatedAtUtc },
            cancellationToken));

        await unitOfWork.Connection.ExecuteAsync(Command(
            """
            INSERT INTO inventory.stock_movements (id, variant_id, type, quantity, reason, order_id, performed_by, created_at)
            VALUES (@Id, @VariantId, @Type, @Quantity, @Reason, @OrderId, @PerformedBy, @CreatedAt)
            """,
            item.NewMovements.Select(movement => new
            {
                movement.Id,
                VariantId = movement.VariantId.Value,
                Type = movement.Type.ToString(),
                movement.Quantity,
                movement.Reason,
                movement.OrderId,
                PerformedBy = movement.PerformedBy?.Value,
                CreatedAt = movement.CreatedAtUtc,
            }).ToList(),
            cancellationToken));
    }

    private CommandDefinition Command(string sql, object parameters, CancellationToken cancellationToken) =>
        new(sql, parameters, unitOfWork.Transaction, cancellationToken: cancellationToken);

    private sealed class Row
    {
        public Guid VariantId { get; init; }
        public int QuantityOnHand { get; init; }
        public DateTime UpdatedAt { get; init; }
    }
}
