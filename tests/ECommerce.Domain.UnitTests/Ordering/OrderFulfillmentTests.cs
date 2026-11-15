using ECommerce.Domain.Catalog;
using ECommerce.Domain.Identity;
using ECommerce.Domain.Ordering;
using ECommerce.Domain.Shipping;

namespace ECommerce.Domain.UnitTests.Ordering;

/// <summary>Fase 5: o caminho do pedido pago até a entrega (arquivo 04 §4.8, RF-053, RF-047 CA3).</summary>
public sealed class OrderFulfillmentTests
{
    private static readonly DateTime Now = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly UserId Customer = new(Guid.CreateVersion7());
    private static readonly UserId Admin = new(Guid.CreateVersion7());

    private static Order Place(OrderGift? gift = null)
    {
        var order = Order.Place(
            Customer,
            [new OrderItem(new VariantId(Guid.CreateVersion7()), new ProductId(Guid.CreateVersion7()), "ANEL-01", "Anel Liso", "Dourado", "16", null, 89.90m, 1)],
            new ShippingAddress("Maria", PostalCode.Rehydrate("01305000"), "Rua Augusta", "100", null, "Consolação", "São Paulo", "SP"),
            new ShippingSelection("PAC", "PAC", 21.40m, 7, false),
            gift ?? OrderGift.None,
            0m,
            null,
            Now).Value;
        return order;
    }

    private static Order Paid(OrderGift? gift = null)
    {
        var order = Place(gift);
        order.ConfirmPayment(null, Now).IsSuccess.ShouldBeTrue();
        return order;
    }

    [Fact, Trait("Spec", "RF-053")]
    public void HappyPath_PaidToDelivered_RecordsWhoAndWhen()
    {
        var order = Paid();

        order.StartPreparation(Admin, Now.AddHours(1)).IsSuccess.ShouldBeTrue();
        order.Ship("aa123456789br", Admin, Now.AddHours(2)).IsSuccess.ShouldBeTrue();
        order.Deliver(Admin, Now.AddDays(3)).IsSuccess.ShouldBeTrue();

        order.Status.ShouldBe(OrderStatus.Delivered);
        order.TrackingCode.ShouldBe("AA123456789BR");
        var changes = order.NewHistory.Skip(2).ToList();
        changes.Select(change => change.To).ShouldBe([OrderStatus.Preparing, OrderStatus.Shipped, OrderStatus.Delivered]);
        changes.ShouldAllBe(change => change.ChangedBy == Admin);
        changes[1].Note.ShouldBe("Rastreio AA123456789BR");
    }

    [Theory, Trait("Spec", "RF-053-CA2")]
    [InlineData(OrderStatus.PendingPayment)]
    [InlineData(OrderStatus.Preparing)]
    public void StartPreparation_OnlyFromPaid(OrderStatus from)
    {
        var order = from == OrderStatus.PendingPayment ? Place() : Paid();
        if (from == OrderStatus.Preparing)
        {
            order.StartPreparation(Admin, Now);
        }

        order.StartPreparation(Admin, Now).Error.ShouldBe(OrderErrors.InvalidTransition);
    }

    [Fact, Trait("Spec", "RF-053-CA2")]
    public void SkippingSteps_IsRejected()
    {
        var paid = Paid();
        paid.Ship("AA123456789BR", Admin, Now).Error.ShouldBe(OrderErrors.InvalidTransition);
        paid.Deliver(Admin, Now).Error.ShouldBe(OrderErrors.InvalidTransition);
        paid.Status.ShouldBe(OrderStatus.PaymentConfirmed);
    }

    [Theory, Trait("Spec", "RF-053-CA3")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("AA 123456789 BR")]
    [InlineData("1234567")]
    [InlineData("AA123-456BR")]
    public void Ship_RequiresAValidTrackingCode(string? code)
    {
        var order = Paid();
        order.StartPreparation(Admin, Now);

        order.Ship(code, Admin, Now).Error.ShouldBe(OrderErrors.InvalidTrackingCode);
        order.Status.ShouldBe(OrderStatus.Preparing);
        order.TrackingCode.ShouldBeNull();
    }

    [Fact, Trait("Spec", "RF-053-CA3")]
    public void TrackingCode_AcceptsOtherCarriers()
    {
        TrackingCode.Normalize("  12345678901234 ").Value.ShouldBe("12345678901234");
        TrackingCode.Normalize(new string('A', TrackingCode.MaxLength + 1)).IsFailure.ShouldBeTrue();
    }

    [Fact, Trait("Spec", "RF-053")]
    public void UpdateTracking_OnlyWhileShipped_AndOnlyWhenItChanges()
    {
        var order = Paid();
        order.StartPreparation(Admin, Now);
        order.UpdateTrackingCode("AA123456789BR", Admin, Now).Error.ShouldBe(OrderErrors.InvalidTransition);

        order.Ship("AA123456789BR", Admin, Now);
        var before = order.NewHistory.Count;
        order.UpdateTrackingCode("aa123456789br", Admin, Now).IsSuccess.ShouldBeTrue();
        order.NewHistory.Count.ShouldBe(before);

        order.UpdateTrackingCode("BB987654321BR", Admin, Now).IsSuccess.ShouldBeTrue();
        order.TrackingCode.ShouldBe("BB987654321BR");
        order.Status.ShouldBe(OrderStatus.Shipped);
        order.NewHistory[^1].Note.ShouldBe("Rastreio corrigido: AA123456789BR → BB987654321BR");
    }

    [Fact, Trait("Spec", "RF-043")]
    public void AdminCancel_WorksUntilPreparing_WithTheReasonInTheHistory()
    {
        var order = Paid();
        order.StartPreparation(Admin, Now);

        order.Cancel(CancellationReason.AdminRequest, Admin, Now, "Peça com defeito").IsSuccess.ShouldBeTrue();

        order.Status.ShouldBe(OrderStatus.Cancelled);
        order.NewHistory[^1].Note.ShouldBe("Peça com defeito");
    }

    [Fact, Trait("Spec", "RF-053")]
    public void Shipped_CannotBeCancelled()
    {
        var order = Paid();
        order.StartPreparation(Admin, Now);
        order.Ship("AA123456789BR", Admin, Now);

        order.Cancel(CancellationReason.AdminRequest, Admin, Now, "Mudou de ideia").Error.ShouldBe(OrderErrors.InvalidTransition);
    }

    [Fact, Trait("Spec", "RF-047-CA3")]
    public void GiftMessage_CanBeCorrectedUntilShipped_WithoutTheTextInTheHistory()
    {
        var order = Paid(OrderGift.Create(true, true, "Feliz aniverssário").Value);
        order.StartPreparation(Admin, Now);

        order.CorrectGiftMessage("Feliz aniversário", Admin, Now).IsSuccess.ShouldBeTrue();
        order.Gift.Message.ShouldBe("Feliz aniversário");
        order.Gift.Wrap.ShouldBeTrue();
        order.NewHistory[^1].Note.ShouldBe("Dedicatória corrigida");

        order.Ship("AA123456789BR", Admin, Now);
        order.CorrectGiftMessage("Outra", Admin, Now).Error.ShouldBe(OrderErrors.GiftMessageLocked);
    }

    [Fact, Trait("Spec", "RF-047-CA3")]
    public void GiftMessage_RespectsTheSameLimits_AndNeedsAGift()
    {
        Paid(OrderGift.Create(true, false, "Oi").Value)
            .CorrectGiftMessage(new string('a', OrderGift.MessageMaxLength + 1), Admin, Now).Error.ShouldBe(OrderErrors.InvalidGiftMessage);
        Paid().CorrectGiftMessage("Oi", Admin, Now).Error.ShouldBe(OrderErrors.NotAGift);
    }
}
