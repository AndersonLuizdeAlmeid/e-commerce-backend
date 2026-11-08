using System.Net;
using System.Text.Json;
using ECommerce.Api.FunctionalTests.Infrastructure;
using ECommerce.Api.FunctionalTests.Ordering;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Application.Payments;
using ECommerce.Infrastructure.Payments;
using Microsoft.Extensions.DependencyInjection;

namespace ECommerce.Api.FunctionalTests.Payments;

/// <summary>Pagamento com a plataforma falsa (entrega 4 §3, P3): o caminho é o mesmo do Mercado Pago, sem a rede.</summary>
public sealed class PaymentTests(ApiFactory factory) : ShopperTest(factory)
{
    private async Task<(AuthClient Customer, Guid Order, Guid Ring)> PlacedOrderAsync(string email, int stock = 5)
    {
        using var admin = await StoreAsync();
        var ring = await PieceAsync(admin, "Anel Liso", stock);
        var (customer, address) = await ShopperAsync(email, (ring, 1));
        var order = (await JsonAsync(await PlaceAsync(customer, address), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        return (customer, order, ring);
    }

    private static Task<HttpResponseMessage> PayAsync(AuthClient customer, Guid order, object? body = null) =>
        customer.PostAsync($"/api/v1/orders/{order}/payments", body ?? new { method = "Pix" });

    private static async Task<string> OrderStatusAsync(AuthClient customer, Guid order) =>
        (await JsonAsync(await customer.GetAsync($"/api/v1/orders/{order}"))).GetProperty("status").GetString()!;

    /// <summary>A confirmação passa pelo Outbox, que roda a cada 200 ms nos testes.</summary>
    private static async Task<string> WaitForOrderStatusAsync(AuthClient customer, Guid order, string expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        string status;
        do
        {
            status = await OrderStatusAsync(customer, order);
            if (status == expected)
            {
                return status;
            }

            await Task.Delay(100, CancellationToken);
        }
        while (DateTime.UtcNow < deadline);

        return status;
    }

    [Fact, Trait("Spec", "RF-044-CA2")]
    public async Task Pix_ReturnsTheQrCodeAndExpiration_AndTheSameOneWhenAskedAgain()
    {
        var (customer, order, _) = await PlacedOrderAsync("pix@loja.teste");
        using var shopper = customer;

        var first = await JsonAsync(await PayAsync(customer, order));
        var second = await JsonAsync(await PayAsync(customer, order));

        first.GetProperty("status").GetString().ShouldBe("Pending");
        first.GetProperty("pixQrCode").GetString().ShouldNotBeNullOrEmpty();
        first.GetProperty("pixQrCodeBase64").GetString().ShouldNotBeNullOrEmpty();
        first.GetProperty("canSimulate").GetBoolean().ShouldBeTrue();
        var due = (await JsonAsync(await customer.GetAsync($"/api/v1/orders/{order}"))).GetProperty("paymentDueAt").GetDateTime();
        first.GetProperty("expiresAt").GetDateTime().ShouldBe(due, TimeSpan.FromSeconds(1));
        second.GetProperty("id").GetGuid().ShouldBe(first.GetProperty("id").GetGuid());

        var current = await JsonAsync(await customer.GetAsync($"/api/v1/orders/{order}/payments/current"));
        current.GetProperty("id").GetGuid().ShouldBe(first.GetProperty("id").GetGuid());
    }

    [Fact, Trait("Spec", "RF-045")]
    public async Task Approval_ConfirmsTheOrder_AndEmailsTheCustomerAndTheStore()
    {
        var (customer, order, _) = await PlacedOrderAsync("aprovado@loja.teste");
        using var shopper = customer;
        await PayAsync(customer, order);

        (await customer.PostAsync($"/api/v1/orders/{order}/payments/simulate-approval", null)).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        (await WaitForOrderStatusAsync(customer, order, "PaymentConfirmed")).ShouldBe("PaymentConfirmed");
        var payment = await JsonAsync(await customer.GetAsync($"/api/v1/orders/{order}/payments/current"));
        payment.GetProperty("status").GetString().ShouldBe("Approved");
        payment.GetProperty("orderStatus").GetString().ShouldBe("PaymentConfirmed");
        await Factory.Emails.WaitForAsync("aprovado@loja.teste", "Pagamento confirmado");
        (await Factory.Emails.WaitForAsync(ApiFactory.StoreEmail, "Novo pedido pago")).TextBody.ShouldContain("aprovado@loja.teste");

        (await PayAsync(customer, order)).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var detail = await JsonAsync(await customer.GetAsync($"/api/v1/orders/{order}"));
        detail.GetProperty("canCancel").GetBoolean().ShouldBeFalse();
    }

    [Fact, Trait("Spec", "RF-045")]
    public async Task RepeatedSync_ConfirmsOnce()
    {
        var (customer, order, _) = await PlacedOrderAsync("repetido@loja.teste");
        using var shopper = customer;
        await PayAsync(customer, order);
        await customer.PostAsync($"/api/v1/orders/{order}/payments/simulate-approval", null);
        await WaitForOrderStatusAsync(customer, order, "PaymentConfirmed");
        var providerId = await ScalarAsync<string>("SELECT provider_payment_id FROM payments.payments WHERE order_id = @Id", new { Id = order });

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var sync = scope.ServiceProvider.GetRequiredService<ICommandHandler<SyncPaymentCommand>>();
            (await sync.Handle(new SyncPaymentCommand(PaymentSettings.FakeProvider, providerId), CancellationToken)).IsSuccess.ShouldBeTrue();
        }

        (await ScalarAsync<long>(
            "SELECT count(*) FROM ordering.order_status_history WHERE order_id = @Id AND to_status = 'PaymentConfirmed'", new { Id = order })).ShouldBe(1);
    }

    [Fact, Trait("Spec", "RF-044")]
    public async Task Card_ApprovedImmediately_ConfirmsTheOrder()
    {
        var (customer, order, _) = await PlacedOrderAsync("cartao@loja.teste");
        using var shopper = customer;

        var payment = await JsonAsync(await PayAsync(customer, order, new
        {
            method = "CreditCard",
            cardToken = FakePaymentGateway.ApprovedCardToken,
            paymentMethodId = "visa",
            installments = 5,
        }));

        payment.GetProperty("status").GetString().ShouldBe("Approved");
        payment.GetProperty("installments").GetInt32().ShouldBe(3);
        payment.GetProperty("orderStatus").GetString().ShouldBe("PaymentConfirmed");
        (await OrderStatusAsync(customer, order)).ShouldBe("PaymentConfirmed");
    }

    [Fact, Trait("Spec", "RF-044-CA3")]
    public async Task Card_Rejected_AllowsAnotherTry()
    {
        var (customer, order, _) = await PlacedOrderAsync("recusado@loja.teste");
        using var shopper = customer;

        var rejected = await JsonAsync(await PayAsync(customer, order, new
        {
            method = "CreditCard",
            cardToken = FakePaymentGateway.RejectedCardToken,
            paymentMethodId = "visa",
        }));
        rejected.GetProperty("status").GetString().ShouldBe("Rejected");
        rejected.GetProperty("statusDetail").GetString().ShouldBe("cc_rejected_insufficient_amount");

        var pix = await JsonAsync(await PayAsync(customer, order));
        pix.GetProperty("status").GetString().ShouldBe("Pending");
        (await OrderStatusAsync(customer, order)).ShouldBe("PendingPayment");
    }

    [Fact, Trait("Spec", "RF-044-CA1")]
    public async Task OverdueOrAnotherCustomersOrder_CannotBePaid()
    {
        var (customer, order, _) = await PlacedOrderAsync("vencido@loja.teste");
        using var shopper = customer;
        using var other = await AuthClient.RegisteredAsync(Factory, "outra@loja.teste");

        (await PayAsync(other, order)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await other.GetAsync($"/api/v1/orders/{order}/payments/current")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        await ExecuteAsync("UPDATE ordering.orders SET payment_due_at = now() - interval '1 minute' WHERE id = @Id", new { Id = order });
        var response = await PayAsync(customer, order);
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeAsync(response)).ShouldBe("Payments.OrderNotPayable");
    }

    [Fact, Trait("Spec", "RF-044")]
    public async Task ProviderDown_ReturnsUnavailable_AndSavesNothing()
    {
        var (customer, order, _) = await PlacedOrderAsync("fora@loja.teste");
        using var shopper = customer;
        Factory.Payments.FailNext = 1;

        (await PayAsync(customer, order)).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);

        (await ScalarAsync<long>("SELECT count(*) FROM payments.payments", new { })).ShouldBe(0);
    }

    [Fact, Trait("Spec", "RN-101")]
    public async Task ApprovalAfterCancellation_KeepsTheOrderCancelled_AndAlertsTheStore()
    {
        var (customer, order, _) = await PlacedOrderAsync("tarde@loja.teste");
        using var shopper = customer;
        await PayAsync(customer, order);
        var providerId = await ScalarAsync<string>("SELECT provider_payment_id FROM payments.payments WHERE order_id = @Id", new { Id = order });
        (await customer.PostAsync($"/api/v1/orders/{order}/cancel", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await Factory.Payments.SimulateApprovalAsync(providerId, CancellationToken);
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var sync = scope.ServiceProvider.GetRequiredService<ICommandHandler<SyncPaymentCommand>>();
            (await sync.Handle(new SyncPaymentCommand(PaymentSettings.FakeProvider, providerId), CancellationToken)).IsSuccess.ShouldBeTrue();
        }

        (await OrderStatusAsync(customer, order)).ShouldBe("Cancelled");
        (await Factory.Emails.WaitForAsync(ApiFactory.StoreEmail, "Atenção")).TextBody.ShouldContain("já estava cancelado");
    }

    [Fact, Trait("Spec", "25-§2")]
    public async Task CancellingTheOrder_CancelsTheOpenPix()
    {
        var (customer, order, _) = await PlacedOrderAsync("cancelapix@loja.teste");
        using var shopper = customer;
        await PayAsync(customer, order);
        var providerId = await ScalarAsync<string>("SELECT provider_payment_id FROM payments.payments WHERE order_id = @Id", new { Id = order });

        (await customer.PostAsync($"/api/v1/orders/{order}/cancel", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while ((await Factory.Payments.GetPaymentAsync(providerId, CancellationToken)).Value.Status != Domain.Payments.PaymentStatus.Cancelled
               && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, CancellationToken);
        }

        (await Factory.Payments.GetPaymentAsync(providerId, CancellationToken)).Value.Status.ShouldBe(Domain.Payments.PaymentStatus.Cancelled);
    }

    [Fact, Trait("Spec", "21-§5.5")]
    public async Task Reconciliation_PicksUpPendingPaymentsWithoutNotification()
    {
        var (customer, order, _) = await PlacedOrderAsync("silencio@loja.teste");
        using var shopper = customer;
        await PayAsync(customer, order);
        var providerId = await ScalarAsync<string>("SELECT provider_payment_id FROM payments.payments WHERE order_id = @Id", new { Id = order });
        await Factory.Payments.SimulateApprovalAsync(providerId, CancellationToken);
        await ExecuteAsync("UPDATE payments.payments SET updated_at = now() - interval '11 minutes' WHERE order_id = @Id", new { Id = order });

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var reconcile = scope.ServiceProvider.GetRequiredService<ICommandHandler<ReconcilePaymentsCommand, int>>();
            (await reconcile.Handle(new ReconcilePaymentsCommand(), CancellationToken)).Value.ShouldBe(1);
        }

        (await WaitForOrderStatusAsync(customer, order, "PaymentConfirmed")).ShouldBe("PaymentConfirmed");
    }

    [Fact, Trait("Spec", "21-§7")]
    public async Task Webhook_WithoutValidSignature_IsUnauthorized()
    {
        var response = await Client.PostAsync(
            "/api/v1/webhooks/payments/fake",
            new StringContent("""{"id":1,"type":"payment","data":{"id":"1"}}""", System.Text.Encoding.UTF8, "application/json"),
            CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Client.PostAsync("/api/v1/webhooks/payments/outro", new StringContent("{}"), CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
