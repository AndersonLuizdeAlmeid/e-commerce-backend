using ECommerce.Domain.Catalog;
using ECommerce.Domain.Identity;
using ECommerce.Domain.Ordering;
using ECommerce.Domain.Shipping;

namespace ECommerce.Domain.UnitTests.Ordering;

/// <summary>Fase 6.3: devolução de pedido entregue (entrega 6 §3, P11).</summary>
public sealed class OrderReturnTests
{
    private static readonly DateTime Now = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
    private static readonly UserId Customer = new(Guid.CreateVersion7());
    private static readonly UserId Admin = new(Guid.CreateVersion7());

    private static Order Place(OrderGift? gift = null) =>
        Order.Place(
            Customer,
            [new OrderItem(new VariantId(Guid.CreateVersion7()), new ProductId(Guid.CreateVersion7()), "ANEL-01", "Anel Liso", "Dourado", "16", null, 89.90m, 1)],
            new ShippingAddress("Maria", PostalCode.Rehydrate("01305000"), "Rua Augusta", "100", null, "Consolação", "São Paulo", "SP"),
            new ShippingSelection("PAC", "PAC", 21.40m, 7, false),
            gift ?? OrderGift.None,
            0m,
            null,
            Now).Value;

    private static Order Delivered(OrderGift? gift = null)
    {
        var order = Place(gift);
        order.ConfirmPayment(null, Now).IsSuccess.ShouldBeTrue();
        order.StartPreparation(Admin, Now).IsSuccess.ShouldBeTrue();
        order.Ship("AA123456789BR", Admin, Now).IsSuccess.ShouldBeTrue();
        order.Deliver(Admin, Now).IsSuccess.ShouldBeTrue();
        return order;
    }

    [Fact, Trait("Spec", "ENT6-P11")]
    public void Delivered_IsReturned_WithTheNoteAndWhoDidIt()
    {
        var order = Delivered();

        order.MarkReturned("  Arrependimento em 5 dias, peça conferida  ", Admin, Now.AddDays(5)).IsSuccess.ShouldBeTrue();

        order.Status.ShouldBe(OrderStatus.Returned);
        var change = order.NewHistory[^1];
        change.From.ShouldBe(OrderStatus.Delivered);
        change.To.ShouldBe(OrderStatus.Returned);
        change.ChangedBy.ShouldBe(Admin);
        change.Note.ShouldBe("Arrependimento em 5 dias, peça conferida");
        order.UpdatedAtUtc.ShouldBe(Now.AddDays(5));
    }

    [Fact, Trait("Spec", "ENT6-P11")]
    public void OnlyDelivered_CanBeReturned()
    {
        Place().MarkReturned("Motivo", Admin, Now).Error.ShouldBe(OrderErrors.InvalidTransition);

        var shipped = Place();
        shipped.ConfirmPayment(null, Now);
        shipped.StartPreparation(Admin, Now);
        shipped.Ship("AA123456789BR", Admin, Now);
        shipped.MarkReturned("Motivo", Admin, Now).Error.ShouldBe(OrderErrors.InvalidTransition);

        var cancelled = Place();
        cancelled.Cancel(CancellationReason.CustomerRequest, Customer, Now);
        cancelled.MarkReturned("Motivo", Admin, Now).Error.ShouldBe(OrderErrors.InvalidTransition);
    }

    [Fact, Trait("Spec", "ENT6-P11")]
    public void Returned_IsFinal()
    {
        var order = Delivered();
        order.MarkReturned("Troca de tamanho", Admin, Now);

        order.MarkReturned("De novo", Admin, Now).Error.ShouldBe(OrderErrors.InvalidTransition);
        order.Deliver(Admin, Now).Error.ShouldBe(OrderErrors.InvalidTransition);
        order.Cancel(CancellationReason.AdminRequest, Admin, Now, "x").Error.ShouldBe(OrderErrors.InvalidTransition);
        order.UpdateTrackingCode("BB123456789BR", Admin, Now).Error.ShouldBe(OrderErrors.InvalidTransition);
    }

    [Theory, Trait("Spec", "ENT6-P11")]
    [InlineData(null)]
    [InlineData("   ")]
    public void Note_IsRequired(string? note) =>
        Delivered().MarkReturned(note, Admin, Now).Error.ShouldBe(OrderErrors.ReturnNoteRequired);

    [Fact, Trait("Spec", "ENT6-P11")]
    public void Note_HasALimit()
    {
        Delivered().MarkReturned(new string('a', Order.ReturnNoteMaxLength), Admin, Now).IsSuccess.ShouldBeTrue();
        Delivered().MarkReturned(new string('a', Order.ReturnNoteMaxLength + 1), Admin, Now).Error.ShouldBe(OrderErrors.ReturnNoteRequired);
    }

    [Fact, Trait("Spec", "ENT6-P11")]
    public void GiftMessage_StaysLocked_AfterTheReturn()
    {
        var order = Delivered(OrderGift.Create(true, false, "Feliz aniversário").Value);
        order.MarkReturned("Arrependimento", Admin, Now);

        order.CorrectGiftMessage("Outra", Admin, Now).Error.ShouldBe(OrderErrors.GiftMessageLocked);
    }
}
