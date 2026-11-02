using ECommerce.Domain.Catalog;
using ECommerce.Domain.Identity;
using ECommerce.Domain.Inventory;

namespace ECommerce.Domain.UnitTests.Inventory;

public sealed class StockItemTests
{
    private static readonly DateTime Now = new(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);
    private static readonly UserId Admin = new(Guid.CreateVersion7());

    private static StockItem Item(int quantity) => StockItem.Rehydrate(new VariantId(Guid.CreateVersion7()), quantity, Now.AddDays(-1));

    [Fact, Trait("Spec", "RF-052")]
    public void Increase_AddsAndRecordsThePositiveDelta()
    {
        var item = Item(2);

        item.Increase(3, " Reposição ", Admin, Now).IsSuccess.ShouldBeTrue();

        item.QuantityOnHand.ShouldBe(5);
        item.UpdatedAtUtc.ShouldBe(Now);
        var movement = item.NewMovements.ShouldHaveSingleItem();
        movement.Type.ShouldBe(StockMovementType.In);
        movement.Quantity.ShouldBe(3);
        movement.Reason.ShouldBe("Reposição");
        movement.PerformedBy.ShouldBe(Admin);
    }

    [Fact, Trait("Spec", "RF-052")]
    public void Decrease_SubtractsAndRecordsTheNegativeDelta()
    {
        var item = Item(5);

        item.Decrease(2, "Avaria", Admin, Now).IsSuccess.ShouldBeTrue();

        item.QuantityOnHand.ShouldBe(3);
        item.NewMovements.ShouldHaveSingleItem().Quantity.ShouldBe(-2);
    }

    [Fact, Trait("Spec", "RF-052-CA1")]
    public void Decrease_BeyondTheBalance_IsRefusedAndChangesNothing()
    {
        var item = Item(2);

        var result = item.Decrease(3, "Avaria", Admin, Now);

        result.Error.ShouldBe(InventoryErrors.InsufficientStock);
        item.QuantityOnHand.ShouldBe(2);
        item.NewMovements.ShouldBeEmpty();
    }

    [Theory, Trait("Spec", "RF-052")]
    [InlineData(7, 3, -4)]
    [InlineData(3, 7, 4)]
    [InlineData(4, 0, -4)]
    public void AdjustTo_RecordsTheDifferenceFromTheCurrentBalance(int current, int counted, int delta)
    {
        var item = Item(current);

        item.AdjustTo(counted, "Contagem", Admin, Now).IsSuccess.ShouldBeTrue();

        item.QuantityOnHand.ShouldBe(counted);
        var movement = item.NewMovements.ShouldHaveSingleItem();
        movement.Type.ShouldBe(StockMovementType.Adjustment);
        movement.Quantity.ShouldBe(delta);
    }

    [Fact, Trait("Spec", "RF-052")]
    public void AdjustTo_TheSameBalance_RecordsNothing()
    {
        var item = Item(4);

        item.AdjustTo(4, "Contagem", Admin, Now).IsSuccess.ShouldBeTrue();

        item.NewMovements.ShouldBeEmpty();
        item.UpdatedAtUtc.ShouldBe(Now.AddDays(-1));
    }

    [Theory, Trait("Spec", "RF-052")]
    [InlineData("")]
    [InlineData("   ")]
    public void Movement_WithoutReason_IsRefused(string reason)
    {
        Item(1).Increase(1, reason, Admin, Now).Error.ShouldBe(InventoryErrors.InvalidReason);
    }

    [Fact, Trait("Spec", "RF-052")]
    public void Movement_WithTooLongReason_IsRefused()
    {
        Item(1).Increase(1, new string('x', StockMovement.ReasonMaxLength + 1), Admin, Now).Error.ShouldBe(InventoryErrors.InvalidReason);
    }

    [Fact, Trait("Spec", "RF-052")]
    public void Quantity_ZeroOrNegative_IsRefused()
    {
        Item(1).Increase(0, "x", Admin, Now).Error.ShouldBe(InventoryErrors.InvalidQuantity);
        Item(1).Decrease(-1, "x", Admin, Now).Error.ShouldBe(InventoryErrors.InvalidQuantity);
        Item(1).AdjustTo(-1, "x", Admin, Now).Error.ShouldBe(InventoryErrors.InvalidQuantity);
    }

    [Fact, Trait("Spec", "RF-052")]
    public void Increase_BeyondTheMaximum_IsRefused()
    {
        var item = Item(StockItem.MaxQuantityOnHand);

        item.Increase(int.MaxValue, "x", Admin, Now).Error.ShouldBe(InventoryErrors.InvalidQuantity);

        item.QuantityOnHand.ShouldBe(StockItem.MaxQuantityOnHand);
    }

    [Theory, Trait("Spec", "RF-052")]
    [InlineData(0, 3, true)]
    [InlineData(3, 0, true)]
    [InlineData(3, 1, false)]
    [InlineData(0, 0, false)]
    public void AvailabilityChanged_OnlyWhenCrossingZero(int before, int after, bool changed)
    {
        var item = Item(before);

        item.AdjustTo(after, "Contagem", Admin, Now);

        item.AvailabilityChanged.ShouldBe(changed);
    }
}
