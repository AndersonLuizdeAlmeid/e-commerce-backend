using ECommerce.Domain.Catalog;
using ECommerce.Domain.Identity;
using ECommerce.Domain.Inventory;
using ECommerce.Domain.Ordering;
using ECommerce.Domain.Shipping;

namespace ECommerce.Domain.UnitTests.Ordering;

public sealed class OrderTests
{
    private static readonly DateTime Now = new(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);
    private static readonly UserId Customer = new(Guid.CreateVersion7());

    private static OrderItem Item(decimal price = 89.90m, int quantity = 1) => new(
        new VariantId(Guid.CreateVersion7()), new ProductId(Guid.CreateVersion7()), "ANEL-01", "Anel Liso", "Dourado", "16", null, price, quantity);

    private static readonly ShippingAddress Address = new(
        "Maria da Silva", PostalCode.Rehydrate("01305000"), "Rua Augusta", "100", null, "Consolação", "São Paulo", "SP");

    private static Order Place(OrderGift? gift = null, decimal wrapPrice = 0m, decimal shipping = 21.40m) =>
        Order.Place(
            Customer,
            [Item(89.90m, 2), Item(150m)],
            Address,
            new ShippingSelection("PAC", "PAC", shipping, 7, false),
            gift ?? OrderGift.None,
            wrapPrice,
            null,
            Now).Value;

    [Fact, Trait("Spec", "RF-040-CA5")]
    public void Total_IsItemsPlusShipping()
    {
        var order = Place();

        order.Subtotal.ShouldBe(329.80m);
        order.Total.ShouldBe(351.20m);
        order.Status.ShouldBe(OrderStatus.PendingPayment);
        order.PaymentDueAtUtc.ShouldBe(Now.AddHours(48));
        order.NewHistory.Single().To.ShouldBe(OrderStatus.PendingPayment);
    }

    [Fact, Trait("Spec", "RF-047-CA2")]
    public void GiftWrap_AddsTheCurrentPrice_OnlyWhenAsked()
    {
        Place(OrderGift.Create(true, true, "Feliz aniversário!").Value, wrapPrice: 12m).Total.ShouldBe(363.20m);
        Place(OrderGift.Create(true, false, "Feliz aniversário!").Value, wrapPrice: 12m).Total.ShouldBe(351.20m);
        Place(OrderGift.None, wrapPrice: 12m).GiftWrapPrice.ShouldBe(0m);
    }

    [Fact, Trait("Spec", "RF-040-CA6")]
    public void Number_IsReadable()
    {
        Order.FormatNumber(123).ShouldBe("PED-000123");
        Order.FormatNumber(1_234_567).ShouldBe("PED-1234567");
    }

    [Fact, Trait("Spec", "RF-047-CA1")]
    public void GiftMessage_IsLimitedTo200CharactersAndSixLines()
    {
        OrderGift.Create(true, false, new string('a', 200)).IsSuccess.ShouldBeTrue();
        OrderGift.Create(true, false, new string('a', 201)).Error.ShouldBe(OrderErrors.InvalidGiftMessage);
        OrderGift.Create(true, false, "1\n2\n3\n4\n5\n6").IsSuccess.ShouldBeTrue();
        OrderGift.Create(true, false, "1\r\n2\n3\n4\n5\n6\n7").Error.ShouldBe(OrderErrors.InvalidGiftMessage);
        OrderGift.Create(true, false, "olá\u0007").Error.ShouldBe(OrderErrors.InvalidGiftMessage);
    }

    [Fact, Trait("Spec", "RF-047")]
    public void NotAGift_DropsWrapAndMessage()
    {
        OrderGift.Create(false, true, "Mensagem").Value.ShouldBe(OrderGift.None);
        OrderGift.Create(true, false, "   ").Value.Message.ShouldBeNull();
    }

    [Fact, Trait("Spec", "RF-043")]
    public void Cancel_FromPendingPayment_RecordsReasonAndHistory()
    {
        var order = Place();

        order.Cancel(CancellationReason.CustomerRequest, Customer, Now.AddHours(1)).IsSuccess.ShouldBeTrue();

        order.Status.ShouldBe(OrderStatus.Cancelled);
        order.CancellationReason.ShouldBe(CancellationReason.CustomerRequest);
        order.NewHistory[^1].From.ShouldBe(OrderStatus.PendingPayment);
        order.Cancel(CancellationReason.CustomerRequest, Customer, Now).Error.ShouldBe(OrderErrors.InvalidTransition);
    }

    [Fact, Trait("Spec", "RN-101")]
    public void ConfirmPayment_OnlyFromPendingPayment()
    {
        var order = Place();
        order.Cancel(CancellationReason.PaymentTimeout, null, Now.AddHours(48));

        order.ConfirmPayment(null, Now.AddHours(49)).Error.ShouldBe(OrderErrors.InvalidTransition);
    }

    [Fact, Trait("Spec", "RF-046-CA2")]
    public void Reminder_IsMarkedOnce()
    {
        var order = Place();

        order.MarkReminderSent(Now.AddHours(24)).ShouldBeTrue();
        order.MarkReminderSent(Now.AddHours(25)).ShouldBeFalse();
    }

    [Fact, Trait("Spec", "RF-040-CA4")]
    public void Stock_ReservedForOrder_NeverGoesNegative()
    {
        var stock = StockItem.Rehydrate(new VariantId(Guid.CreateVersion7()), 1, Now);
        var orderId = Guid.CreateVersion7();

        stock.ReserveForOrder(2, orderId, "Pedido PED-000001", Customer, Now).Error.ShouldBe(InventoryErrors.InsufficientStock);
        stock.ReserveForOrder(1, orderId, "Pedido PED-000001", Customer, Now).IsSuccess.ShouldBeTrue();

        stock.QuantityOnHand.ShouldBe(0);
        stock.AvailabilityChanged.ShouldBeTrue();
        var movement = stock.NewMovements.Single();
        movement.Type.ShouldBe(StockMovementType.OrderPlaced);
        movement.Quantity.ShouldBe(-1);
        movement.OrderId.ShouldBe(orderId);
    }
}
