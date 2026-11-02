using ECommerce.Domain.Abstractions;
using ECommerce.Domain.Catalog;
using ECommerce.Domain.Identity;

namespace ECommerce.Domain.Inventory;

public static class InventoryErrors
{
    public static readonly Error InsufficientStock =
        Error.BusinessRule("Inventory.InsufficientStock", "Estoque insuficiente: o saldo não pode ficar negativo.");

    public static readonly Error InvalidQuantity =
        Error.Validation("Inventory.InvalidQuantity", $"Informe uma quantidade válida; o saldo vai até {StockItem.MaxQuantityOnHand}.");

    public static readonly Error InvalidReason =
        Error.Validation("Inventory.InvalidReason", $"Informe o motivo em até {StockMovement.ReasonMaxLength} caracteres.");
}

public enum StockMovementType
{
    In,
    Out,
    Adjustment,
}

/// <summary>Registro de uma mudança de saldo (RF-052). <see cref="Quantity"/> é o delta com sinal.</summary>
public sealed record StockMovement(
    Guid Id,
    VariantId VariantId,
    StockMovementType Type,
    int Quantity,
    string Reason,
    UserId PerformedBy,
    DateTime CreatedAtUtc)
{
    public const int ReasonMaxLength = 200;
}

/// <summary>Saldo de uma variação (arquivo 04 §4.5). Toda mudança gera uma movimentação; o saldo nunca fica negativo (RF-052 CA1).</summary>
public sealed class StockItem
{
    public const int MaxQuantityOnHand = 1_000_000;

    private readonly int _initialQuantity;
    private readonly List<StockMovement> _movements = [];

    private StockItem(VariantId variantId, int quantityOnHand, DateTime updatedAtUtc)
    {
        VariantId = variantId;
        QuantityOnHand = quantityOnHand;
        UpdatedAtUtc = updatedAtUtc;
        _initialQuantity = quantityOnHand;
    }

    public VariantId VariantId { get; }

    public int QuantityOnHand { get; private set; }

    public DateTime UpdatedAtUtc { get; private set; }

    /// <summary>Movimentações ainda não gravadas.</summary>
    public IReadOnlyList<StockMovement> NewMovements => _movements;

    /// <summary>A variação esgotou ou voltou a ter saldo: só nesses momentos a loja muda.</summary>
    public bool AvailabilityChanged => (_initialQuantity == 0) != (QuantityOnHand == 0);

    /// <summary>Uso exclusivo da Infrastructure.</summary>
    public static StockItem Rehydrate(VariantId variantId, int quantityOnHand, DateTime updatedAtUtc) =>
        new(variantId, quantityOnHand, updatedAtUtc);

    public Result Increase(int quantity, string reason, UserId performedBy, DateTime nowUtc) =>
        quantity <= 0
            ? Result.Failure(InventoryErrors.InvalidQuantity)
            : Apply(StockMovementType.In, quantity, reason, performedBy, nowUtc);

    public Result Decrease(int quantity, string reason, UserId performedBy, DateTime nowUtc) =>
        quantity <= 0
            ? Result.Failure(InventoryErrors.InvalidQuantity)
            : Apply(StockMovementType.Out, -quantity, reason, performedBy, nowUtc);

    /// <summary>Quem conta a prateleira sabe o total; o delta sai do saldo atual. Total igual ao saldo não registra nada.</summary>
    public Result AdjustTo(int newQuantity, string reason, UserId performedBy, DateTime nowUtc) =>
        newQuantity < 0
            ? Result.Failure(InventoryErrors.InvalidQuantity)
            : Apply(StockMovementType.Adjustment, newQuantity - QuantityOnHand, reason, performedBy, nowUtc);

    private Result Apply(StockMovementType type, int delta, string reason, UserId performedBy, DateTime nowUtc)
    {
        var trimmedReason = reason?.Trim() ?? string.Empty;
        if (trimmedReason.Length is 0 or > StockMovement.ReasonMaxLength)
        {
            return Result.Failure(InventoryErrors.InvalidReason);
        }

        var quantity = (long)QuantityOnHand + delta;
        if (quantity < 0)
        {
            return Result.Failure(InventoryErrors.InsufficientStock);
        }

        if (quantity > MaxQuantityOnHand)
        {
            return Result.Failure(InventoryErrors.InvalidQuantity);
        }

        if (delta == 0)
        {
            return Result.Success();
        }

        QuantityOnHand = (int)quantity;
        UpdatedAtUtc = nowUtc;
        _movements.Add(new StockMovement(Guid.CreateVersion7(), VariantId, type, delta, trimmedReason, performedBy, nowUtc));
        return Result.Success();
    }
}

public interface IStockItemRepository
{
    /// <summary>Trava a linha até o fim da transação, para o saldo não mudar entre a leitura e a gravação.</summary>
    Task<StockItem?> GetForUpdateAsync(VariantId variantId, CancellationToken cancellationToken);

    Task SaveAsync(StockItem item, CancellationToken cancellationToken);
}
