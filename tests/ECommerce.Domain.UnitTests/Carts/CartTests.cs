using ECommerce.Domain.Carts;
using ECommerce.Domain.Catalog;
using ECommerce.Domain.Identity;

namespace ECommerce.Domain.UnitTests.Carts;

public sealed class CartTests
{
    private static readonly DateTime Now = new(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);

    private static Cart Empty() => Cart.Rehydrate(Guid.CreateVersion7(), new UserId(Guid.CreateVersion7()), [], Now.AddDays(-1));

    private static VariantId Variant() => new(Guid.CreateVersion7());

    [Fact, Trait("Spec", "RF-030")]
    public void Add_TheSamePiece_SumsTheQuantity()
    {
        var cart = Empty();
        var ring = Variant();

        cart.Add(ring, 2, Now);
        cart.Add(ring, 3, Now);

        cart.Items.ShouldHaveSingleItem().Quantity.ShouldBe(5);
    }

    [Theory, Trait("Spec", "RF-030-CA1")]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100)]
    public void Add_OutOfTheRange_IsRefused(int quantity)
    {
        Empty().Add(Variant(), quantity, Now).Error.ShouldBe(CartErrors.InvalidQuantity);
    }

    [Fact, Trait("Spec", "RF-030-CA1")]
    public void Add_PassingTheLimitPerPiece_IsRefusedAndKeepsTheQuantity()
    {
        var cart = Empty();
        var ring = Variant();
        cart.Add(ring, 98, Now);

        cart.Add(ring, 2, Now).Error.ShouldBe(CartErrors.InvalidQuantity);

        cart.Items.ShouldHaveSingleItem().Quantity.ShouldBe(98);
    }

    [Fact, Trait("Spec", "RN-040")]
    public void Add_BeyondTheLimitOfPieces_IsRefused_ButAPieceAlreadyThereStillSums()
    {
        var cart = Empty();
        var first = Variant();
        cart.Add(first, 1, Now);
        for (var i = 1; i < Cart.MaxItems; i++)
        {
            cart.Add(Variant(), 1, Now);
        }

        cart.Add(Variant(), 1, Now).Error.ShouldBe(CartErrors.TooManyItems);
        cart.Add(first, 1, Now).IsSuccess.ShouldBeTrue();
        cart.Items.Count.ShouldBe(Cart.MaxItems);
    }

    [Fact, Trait("Spec", "RF-030")]
    public void SetQuantity_ChangesOnlyThatPiece_AndRefusesWhatIsNotInTheCart()
    {
        var cart = Empty();
        var ring = Variant();
        cart.Add(ring, 2, Now);

        cart.SetQuantity(ring, 7, Now).IsSuccess.ShouldBeTrue();
        cart.Items.ShouldHaveSingleItem().Quantity.ShouldBe(7);

        cart.SetQuantity(Variant(), 1, Now).Error.ShouldBe(CartErrors.ItemNotInCart);
        cart.SetQuantity(ring, 0, Now).Error.ShouldBe(CartErrors.InvalidQuantity);
    }

    [Fact, Trait("Spec", "RF-030")]
    public void Remove_And_Clear_LeaveTheCartEmpty()
    {
        var cart = Empty();
        var ring = Variant();
        cart.Add(ring, 1, Now);
        cart.Add(Variant(), 1, Now);

        cart.Remove(ring, Now);
        cart.Remove(ring, Now);
        cart.Items.Count.ShouldBe(1);

        cart.Clear(Now);
        cart.Items.ShouldBeEmpty();
    }

    [Fact, Trait("Spec", "RF-031")]
    public void Merge_SumsWithTheLimit_AndAddsNewPieces()
    {
        var cart = Empty();
        var ring = Variant();
        var bag = Variant();
        cart.Add(ring, 90, Now);

        var skipped = cart.Merge([(ring, 20), (bag, 2)], Now);

        skipped.ShouldBeEmpty();
        cart.Items.Single(item => item.VariantId == ring).Quantity.ShouldBe(Cart.MaxQuantityPerItem);
        cart.Items.Single(item => item.VariantId == bag).Quantity.ShouldBe(2);
    }

    [Fact, Trait("Spec", "RN-040")]
    public void Merge_BeyondTheLimitOfPieces_ReturnsWhatDidNotFit()
    {
        var cart = Empty();
        var ring = Variant();
        cart.Add(ring, 1, Now);
        for (var i = 1; i < Cart.MaxItems; i++)
        {
            cart.Add(Variant(), 1, Now);
        }

        var extra = Variant();
        var skipped = cart.Merge([(ring, 1), (extra, 1)], Now);

        skipped.ShouldBe([extra]);
        cart.Items.Count.ShouldBe(Cart.MaxItems);
        cart.Items.Single(item => item.VariantId == ring).Quantity.ShouldBe(2);
    }
}
