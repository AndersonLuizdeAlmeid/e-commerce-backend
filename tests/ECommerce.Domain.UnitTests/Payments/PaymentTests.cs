using ECommerce.Domain.Payments;

namespace ECommerce.Domain.UnitTests.Payments;

public sealed class PaymentTests
{
    private static readonly DateTime Now = new(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);

    private static Payment Pending() => Payment.Start(Guid.CreateVersion7(), "Fake", PaymentMethod.Pix, 108.86m, null, Now.AddHours(48), Now);

    private static PaymentSnapshot Snapshot(PaymentStatus status) =>
        new("123", status, null, 108.86m, null, "pix-code", "base64", Now.AddHours(48), status == PaymentStatus.Approved ? Now.AddMinutes(5) : null);

    [Fact, Trait("Spec", "RF-045")]
    public void Pending_BecomesApproved_WithTheApprovalTime()
    {
        var payment = Pending();

        payment.Apply(Snapshot(PaymentStatus.Approved), Now.AddMinutes(6)).ShouldBeTrue();

        payment.Status.ShouldBe(PaymentStatus.Approved);
        payment.ApprovedAtUtc.ShouldBe(Now.AddMinutes(5));
        payment.ProviderPaymentId.ShouldBe("123");
    }

    [Fact, Trait("Spec", "21-§8")]
    public void OutOfOrderNotification_DoesNotGoBack()
    {
        var payment = Pending();
        payment.Apply(Snapshot(PaymentStatus.Approved), Now);

        payment.Apply(Snapshot(PaymentStatus.Pending), Now.AddMinutes(1)).ShouldBeFalse();
        payment.Apply(Snapshot(PaymentStatus.Rejected), Now.AddMinutes(1)).ShouldBeFalse();

        payment.Status.ShouldBe(PaymentStatus.Approved);
    }

    [Fact, Trait("Spec", "21-§8")]
    public void RepeatedNotification_ChangesNothing()
    {
        var payment = Pending();
        payment.Apply(Snapshot(PaymentStatus.Approved), Now).ShouldBeTrue();

        payment.Apply(Snapshot(PaymentStatus.Approved), Now.AddMinutes(1)).ShouldBeFalse();
    }

    [Fact, Trait("Spec", "RF-044-CA3")]
    public void Rejected_IsFinal()
    {
        var payment = Pending();
        payment.Apply(Snapshot(PaymentStatus.Rejected), Now);

        payment.Apply(Snapshot(PaymentStatus.Approved), Now.AddMinutes(1)).ShouldBeFalse();
        payment.IsFinal.ShouldBeTrue();
    }

    [Fact, Trait("Spec", "RF-043-CA2")]
    public void Approved_CanBeRefunded_AndAFailedRefundCanBeRetried()
    {
        var payment = Pending();
        payment.Apply(Snapshot(PaymentStatus.Approved), Now);

        payment.MarkRefundFailed(Now.AddHours(1)).ShouldBeTrue();
        payment.Status.ShouldBe(PaymentStatus.RefundFailed);
        payment.Apply(Snapshot(PaymentStatus.Refunded), Now.AddHours(2)).ShouldBeTrue();
        payment.Status.ShouldBe(PaymentStatus.Refunded);
        payment.MarkRefunded(Now.AddHours(3)).ShouldBeFalse();
    }

    [Fact, Trait("Spec", "RF-053-CA1")]
    public void Manual_IsBornApproved_WithTheReason()
    {
        var payment = Payment.RecordManual(Guid.CreateVersion7(), 111.30m, "  PIX direto na conta da loja ", Now);

        payment.Method.ShouldBe(PaymentMethod.Manual);
        payment.Provider.ShouldBe(Payment.ManualProvider);
        payment.IsManual.ShouldBeTrue();
        payment.Status.ShouldBe(PaymentStatus.Approved);
        payment.ApprovedAtUtc.ShouldBe(Now);
        payment.ProviderPaymentId.ShouldBeNull();
        payment.StatusDetail.ShouldBe("PIX direto na conta da loja");
    }

    [Fact, Trait("Spec", "RF-053-CA1")]
    public void OnlyManual_IsMarkedRefundedByTheStore()
    {
        var manual = Payment.RecordManual(Guid.CreateVersion7(), 111.30m, "PIX direto", Now);
        manual.MarkRefundedManually("Devolvido por PIX", Now.AddDays(1)).ShouldBeTrue();
        manual.Status.ShouldBe(PaymentStatus.Refunded);
        manual.StatusDetail.ShouldBe("PIX direto · Devolvido: Devolvido por PIX");
        manual.MarkRefundedManually("De novo", Now.AddDays(2)).ShouldBeFalse();

        var provider = Pending();
        provider.Apply(Snapshot(PaymentStatus.Approved), Now);
        provider.MarkRefundedManually("Devolvido por PIX", Now).ShouldBeFalse();
        provider.Status.ShouldBe(PaymentStatus.Approved);
    }
}
