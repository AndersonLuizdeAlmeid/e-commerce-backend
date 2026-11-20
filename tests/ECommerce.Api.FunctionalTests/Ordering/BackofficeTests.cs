using System.Net;
using System.Text.Json;
using ECommerce.Api.FunctionalTests.Infrastructure;
using ECommerce.Domain.Payments;
using ECommerce.Infrastructure.Payments;

namespace ECommerce.Api.FunctionalTests.Ordering;

/// <summary>Fase 5: pedidos no painel (RF-053), estornos pendentes, usuários (RF-054) e dashboard (RF-055).</summary>
public sealed class BackofficeTests(ApiFactory factory) : ShopperTest(factory)
{
    private static object ApprovedCard() => new { method = "CreditCard", cardToken = FakePaymentGateway.ApprovedCardToken, paymentMethodId = "visa" };

    private sealed record Scenario(AuthClient Admin, AuthClient Customer, Guid Order, Guid Ring) : IDisposable
    {
        public void Dispose()
        {
            Admin.Dispose();
            Customer.Dispose();
        }
    }

    /// <summary>Pedido feito pela cliente; pago com o cartão de teste quando <paramref name="paid"/>.</summary>
    private async Task<Scenario> OrderAsync(string email, bool paid = true, object? gift = null, int stock = 5)
    {
        var admin = await StoreAsync();
        var ring = await PieceAsync(admin, $"Anel {email.Split('@')[0]}", stock);
        var (customer, address) = await ShopperAsync(email, (ring, 1));
        var order = (await JsonAsync(await PlaceAsync(customer, address, gift: gift), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        if (paid)
        {
            (await JsonAsync(await customer.PostAsync($"/api/v1/orders/{order}/payments", ApprovedCard()))).GetProperty("status").GetString().ShouldBe("Approved");
        }

        return new Scenario(admin, customer, order, ring);
    }

    private static async Task<JsonElement> ActAsync(AuthClient admin, Guid order, string action, object? body = null, HttpStatusCode expected = HttpStatusCode.OK) =>
        await JsonAsync(await admin.PostAsync($"/api/v1/admin/orders/{order}/{action}", body), expected);

    private async Task<string> WaitForPaymentStatusAsync(Guid paymentId, string expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        string status;
        do
        {
            status = await ScalarAsync<string>("SELECT status FROM payments.payments WHERE id = @Id", new { Id = paymentId });
            if (status == expected)
            {
                return status;
            }

            await Task.Delay(100, CancellationToken);
        }
        while (DateTime.UtcNow < deadline);

        return status;
    }

    [Fact, Trait("Spec", "RF-053")]
    public async Task PaidOrder_IsPreparedShippedAndDelivered_WithEmailsAndWhoDidIt()
    {
        using var s = await OrderAsync("caminho@loja.teste");

        var preparing = await ActAsync(s.Admin, s.Order, "start-preparation");
        preparing.GetProperty("status").GetString().ShouldBe("Preparing");
        preparing.GetProperty("allowedActions").EnumerateArray().Select(a => a.GetString()).ShouldContain("Ship");

        var invalid = await s.Admin.PostAsync($"/api/v1/admin/orders/{s.Order}/ship", new { trackingCode = "123" });
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await invalid.Content.ReadAsStringAsync(CancellationToken)).ShouldContain("trackingCode");

        var shipped = await ActAsync(s.Admin, s.Order, "ship", new { trackingCode = "aa123456789br" });
        shipped.GetProperty("status").GetString().ShouldBe("Shipped");
        shipped.GetProperty("shipping").GetProperty("trackingCode").GetString().ShouldBe("AA123456789BR");
        (await Factory.Emails.WaitForAsync("caminho@loja.teste", "foi enviado")).TextBody.ShouldContain("AA123456789BR");

        var delivered = await ActAsync(s.Admin, s.Order, "deliver");
        delivered.GetProperty("status").GetString().ShouldBe("Delivered");
        // Entregue só aceita a devolução (entrega 6 §3, P11).
        delivered.GetProperty("allowedActions").EnumerateArray().Select(a => a.GetString()).ShouldBe(["Return"]);
        await Factory.Emails.WaitForAsync("caminho@loja.teste", "entregue");

        var history = delivered.GetProperty("history").EnumerateArray().ToList();
        history.Select(h => h.GetProperty("toStatus").GetString()).ShouldBe(["PendingPayment", "PaymentConfirmed", "Preparing", "Shipped", "Delivered"]);
        history[2].GetProperty("changedBy").GetString().ShouldNotBeNullOrEmpty();
        history[1].GetProperty("changedBy").ValueKind.ShouldBe(JsonValueKind.Null); // o webhook/sistema confirmou

        var mine = await JsonAsync(await s.Customer.GetAsync($"/api/v1/orders/{s.Order}"));
        mine.GetProperty("status").GetString().ShouldBe("Delivered");
        mine.GetProperty("shipping").GetProperty("trackingCode").GetString().ShouldBe("AA123456789BR");
    }

    [Fact, Trait("Spec", "RF-053-CA2")]
    public async Task SkippingAStep_Is422_AndNothingChanges()
    {
        using var s = await OrderAsync("pular@loja.teste");

        var response = await s.Admin.PostAsync($"/api/v1/admin/orders/{s.Order}/ship", new { trackingCode = "AA123456789BR" });

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeAsync(response)).ShouldBe("Ordering.InvalidTransition");
        (await JsonAsync(await s.Admin.GetAsync($"/api/v1/admin/orders/{s.Order}"))).GetProperty("status").GetString().ShouldBe("PaymentConfirmed");
    }

    [Fact, Trait("Spec", "RF-053")]
    public async Task StoreCancelsWhilePreparing_StockReturns_RefundGoes_AndTheReasonStaysInside()
    {
        using var s = await OrderAsync("lojacancela@loja.teste", stock: 1);
        await ActAsync(s.Admin, s.Order, "start-preparation");

        (await s.Admin.PostAsync($"/api/v1/admin/orders/{s.Order}/cancel", new { reason = " " })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var cancelled = await ActAsync(s.Admin, s.Order, "cancel", new { reason = "Peça com defeito no fecho" });

        cancelled.GetProperty("status").GetString().ShouldBe("Cancelled");
        cancelled.GetProperty("cancellationReason").GetString().ShouldBe("AdminRequest");
        cancelled.GetProperty("history").EnumerateArray().Last().GetProperty("note").GetString().ShouldBe("Peça com defeito no fecho");
        (await StockAsync(s.Ring)).ShouldBe(1);
        var payment = cancelled.GetProperty("payments")[0].GetProperty("id").GetGuid();
        (await WaitForPaymentStatusAsync(payment, "Refunded")).ShouldBe("Refunded");
        var email = await Factory.Emails.WaitForAsync("lojacancela@loja.teste", "cancelado");
        email.TextBody.ShouldContain("pela loja");
        email.TextBody.ShouldNotContain("defeito");
    }

    [Fact, Trait("Spec", "RF-053-CA1")]
    public async Task ManualConfirmation_NeedsAReason_RecordsAManualPayment_AndCancelsTheOpenPix()
    {
        using var s = await OrderAsync("manual@loja.teste", paid: false);
        (await s.Customer.PostAsync($"/api/v1/orders/{s.Order}/payments", new { method = "Pix" })).EnsureSuccessStatusCode();

        (await s.Admin.PostAsync($"/api/v1/admin/orders/{s.Order}/confirm-payment", new { reason = "" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var confirmed = await ActAsync(s.Admin, s.Order, "confirm-payment", new { reason = "PIX direto na conta da loja, conferido no extrato" });

        confirmed.GetProperty("status").GetString().ShouldBe("PaymentConfirmed");
        var manual = confirmed.GetProperty("payments").EnumerateArray().Single(p => p.GetProperty("method").GetString() == "Manual");
        manual.GetProperty("status").GetString().ShouldBe("Approved");
        manual.GetProperty("amount").GetDecimal().ShouldBe(confirmed.GetProperty("total").GetDecimal());
        await Factory.Emails.WaitForAsync("manual@loja.teste", "Pagamento confirmado");

        // O PIX aberto é cancelado na plataforma pelo Outbox.
        var pix = await ScalarAsync<string>("SELECT provider_payment_id FROM payments.payments WHERE order_id = @Id AND method = 'Pix'", new { Id = s.Order });
        var deadline = DateTime.UtcNow.AddSeconds(15);
        var status = PaymentStatus.Pending;
        while (status != PaymentStatus.Cancelled && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, CancellationToken);
            status = (await Factory.Payments.GetPaymentAsync(pix, CancellationToken)).Value.Status;
        }

        status.ShouldBe(PaymentStatus.Cancelled);
    }

    [Fact, Trait("Spec", "21-§5.4")]
    public async Task ManualPaymentOfACancelledOrder_WaitsInTheQueue_UntilTheStoreMarksItReturned()
    {
        using var s = await OrderAsync("devolvi@loja.teste", paid: false);
        await ActAsync(s.Admin, s.Order, "confirm-payment", new { reason = "Dinheiro na loja" });
        await ActAsync(s.Admin, s.Order, "cancel", new { reason = "Cliente desistiu" });

        var queue = await JsonAsync(await s.Admin.GetAsync("/api/v1/admin/payments/refunds/pending"));
        var row = queue.EnumerateArray().Single();
        row.GetProperty("kind").GetString().ShouldBe("ManualPayment");
        row.GetProperty("actions").EnumerateArray().Select(a => a.GetString()).ShouldBe(["MarkRefunded"]);
        var payment = row.GetProperty("paymentId").GetGuid();

        (await s.Admin.PostAsync($"/api/v1/admin/payments/{payment}/refund", null)).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await s.Admin.PostAsync($"/api/v1/admin/payments/{payment}/mark-refunded", new { note = "" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await s.Admin.PostAsync($"/api/v1/admin/payments/{payment}/mark-refunded", new { note = "Devolvido por PIX" })).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await JsonAsync(await s.Admin.GetAsync("/api/v1/admin/payments/refunds/pending"))).GetArrayLength().ShouldBe(0);
        await Factory.Emails.WaitForAsync("devolvi@loja.teste", "Estorno do pedido");
    }

    [Fact, Trait("Spec", "21-§5.4")]
    public async Task RefusedRefund_IsInTheQueue_AndLeavesItWhenTheRetryWorks()
    {
        using var s = await OrderAsync("recusado@loja.teste");
        Factory.Payments.RejectRefunds = true;
        var cancelled = await ActAsync(s.Admin, s.Order, "cancel", new { reason = "Teste de estorno" });
        var payment = cancelled.GetProperty("payments")[0].GetProperty("id").GetGuid();
        (await WaitForPaymentStatusAsync(payment, "RefundFailed")).ShouldBe("RefundFailed");

        var row = (await JsonAsync(await s.Admin.GetAsync("/api/v1/admin/payments/refunds/pending"))).EnumerateArray().Single();
        row.GetProperty("kind").GetString().ShouldBe("RefundFailed");
        row.GetProperty("customerEmail").GetString().ShouldBe("recusado@loja.teste");
        (await s.Admin.PostAsync($"/api/v1/admin/payments/{payment}/mark-refunded", new { note = "x" })).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        Factory.Payments.RejectRefunds = false;
        (await s.Admin.PostAsync($"/api/v1/admin/payments/{payment}/refund", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await WaitForPaymentStatusAsync(payment, "Refunded")).ShouldBe("Refunded");
        (await JsonAsync(await s.Admin.GetAsync("/api/v1/admin/payments/refunds/pending"))).GetArrayLength().ShouldBe(0);
    }

    [Fact, Trait("Spec", "21-§5.4")]
    public async Task ApprovedPaymentOfAnOldCancelledOrder_ShowsUpAsNotRefunded()
    {
        using var s = await OrderAsync("esquecido@loja.teste");
        // Pedido cancelado há uma hora com o pagamento ainda aprovado: o Outbox desistiu (estado montado direto no banco).
        await ExecuteAsync(
            "UPDATE ordering.orders SET status = 'Cancelled', cancellation_reason = 'AdminRequest', updated_at = now() - interval '1 hour' WHERE id = @Id",
            new { Id = s.Order });

        var row = (await JsonAsync(await s.Admin.GetAsync("/api/v1/admin/payments/refunds/pending"))).EnumerateArray().Single();

        row.GetProperty("kind").GetString().ShouldBe("RefundNotDone");
        row.GetProperty("actions").EnumerateArray().Select(a => a.GetString()).ShouldBe(["Retry", "Sync"]);
    }

    [Fact, Trait("Spec", "RF-047-CA3")]
    public async Task GiftMessage_IsCorrectedUntilShipped()
    {
        using var s = await OrderAsync("presente@loja.teste", gift: new { wrap = true, message = "Feliz aniverssário" });

        var corrected = await JsonAsync(await s.Admin.PutAsync($"/api/v1/admin/orders/{s.Order}/gift-message", new { message = "Feliz aniversário" }));
        corrected.GetProperty("gift").GetProperty("message").GetString().ShouldBe("Feliz aniversário");
        corrected.GetProperty("history").EnumerateArray().Last().GetProperty("note").GetString().ShouldBe("Dedicatória corrigida");

        await ActAsync(s.Admin, s.Order, "start-preparation");
        await ActAsync(s.Admin, s.Order, "ship", new { trackingCode = "AA123456789BR" });
        var locked = await s.Admin.PutAsync($"/api/v1/admin/orders/{s.Order}/gift-message", new { message = "Outra" });
        locked.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeAsync(locked)).ShouldBe("Ordering.GiftMessageLocked");
    }

    [Fact, Trait("Spec", "RF-053")]
    public async Task List_FiltersBySearchStatusAndGift_WithCountsPerStatus()
    {
        using var s = await OrderAsync("lista@loja.teste", gift: new { wrap = false, message = "Oi" });
        var number = (await JsonAsync(await s.Admin.GetAsync($"/api/v1/admin/orders/{s.Order}"))).GetProperty("number").GetString()!;

        var bySearch = await JsonAsync(await s.Admin.GetAsync($"/api/v1/admin/orders?search={number[4..].TrimStart('0')}"));
        bySearch.GetProperty("orders").GetProperty("items")[0].GetProperty("id").GetGuid().ShouldBe(s.Order);
        bySearch.GetProperty("orders").GetProperty("items")[0].GetProperty("paymentMethod").GetString().ShouldBe("CreditCard");
        bySearch.GetProperty("orders").GetProperty("items")[0].GetProperty("isGift").GetBoolean().ShouldBeTrue();

        var byEmail = await JsonAsync(await s.Admin.GetAsync("/api/v1/admin/orders?search=LISTA@loja"));
        byEmail.GetProperty("orders").GetProperty("totalItems").GetInt32().ShouldBe(1);

        var other = await JsonAsync(await s.Admin.GetAsync("/api/v1/admin/orders?status=Shipped"));
        other.GetProperty("orders").GetProperty("totalItems").GetInt32().ShouldBe(0);
        other.GetProperty("statusCounts").EnumerateArray().Single().GetProperty("status").GetString().ShouldBe("PaymentConfirmed");

        (await s.Admin.GetAsync("/api/v1/admin/orders?status=Nada")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await s.Admin.GetAsync("/api/v1/admin/orders?search=%25")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact, Trait("Spec", "09-§2")]
    public async Task Customer_IsForbiddenEverywhereInTheBackoffice()
    {
        using var s = await OrderAsync("curiosa@loja.teste");

        foreach (var path in new[] { "/api/v1/admin/orders", $"/api/v1/admin/orders/{s.Order}", "/api/v1/admin/payments/refunds/pending", "/api/v1/admin/users", "/api/v1/admin/dashboard" })
        {
            (await s.Customer.GetAsync(path)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, path);
        }

        (await s.Customer.PostAsync($"/api/v1/admin/orders/{s.Order}/start-preparation", null)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact, Trait("Spec", "RF-054-CA1")]
    public async Task Admin_CannotChangeHerself()
    {
        using var admin = await AdminAsync();
        var me = (await JsonAsync(await admin.GetAsync("/api/v1/admin/users?role=Admin"))).GetProperty("items").EnumerateArray().Single();
        me.GetProperty("isSelf").GetBoolean().ShouldBeTrue();
        var id = me.GetProperty("id").GetGuid();

        var demote = await admin.PatchAsync($"/api/v1/admin/users/{id}/role", new { role = "Customer" });
        demote.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeAsync(demote)).ShouldBe("Identity.CannotChangeSelf");
        (await admin.PatchAsync($"/api/v1/admin/users/{id}/status", new { isActive = false })).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact, Trait("Spec", "RF-054")]
    public async Task DeactivatedCustomer_LosesAccessOnTheNextRequest()
    {
        using var s = await OrderAsync("desativada@loja.teste", paid: false);
        (await s.Customer.GetAsync("/api/v1/orders")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var id = (await JsonAsync(await s.Admin.GetAsync("/api/v1/admin/users?search=desativada"))).GetProperty("items")[0].GetProperty("id").GetGuid();

        var user = await JsonAsync(await s.Admin.PatchAsync($"/api/v1/admin/users/{id}/status", new { isActive = false }));

        user.GetProperty("isActive").GetBoolean().ShouldBeFalse();
        (await s.Customer.GetAsync("/api/v1/orders")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await s.Customer.RefreshAsync()).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await new AuthClient(Factory).LoginAsync("desativada@loja.teste")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact, Trait("Spec", "RF-054")]
    public async Task PromotedCustomer_UsesThePanelAfterSigningInAgain()
    {
        using var admin = await AdminAsync();
        using var maria = await AuthClient.RegisteredAsync(Factory, "promovida@loja.teste");
        var id = (await JsonAsync(await admin.GetAsync("/api/v1/admin/users?search=promovida"))).GetProperty("items")[0].GetProperty("id").GetGuid();

        (await JsonAsync(await admin.PatchAsync($"/api/v1/admin/users/{id}/role", new { role = "Admin" }))).GetProperty("role").GetString().ShouldBe("Admin");

        (await maria.GetAsync("/api/v1/cart")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using var again = new AuthClient(Factory);
        (await again.LoginAsync("promovida@loja.teste")).EnsureSuccessStatusCode();
        (await again.GetAsync("/api/v1/admin/orders")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact, Trait("Spec", "RF-054-CA2")]
    public async Task TwoAdminsDemotingEachOther_AtTheSameTime_LeaveOneAdmin()
    {
        using var first = await AdminAsync();
        using (var registered = await AuthClient.RegisteredAsync(Factory, "segunda@loja.teste"))
        {
            var newId = (await JsonAsync(await first.GetAsync("/api/v1/admin/users?search=segunda"))).GetProperty("items")[0].GetProperty("id").GetGuid();
            (await first.PatchAsync($"/api/v1/admin/users/{newId}/role", new { role = "Admin" })).EnsureSuccessStatusCode();
        }

        using var second = new AuthClient(Factory);
        (await second.LoginAsync("segunda@loja.teste")).EnsureSuccessStatusCode();
        var admins = (await JsonAsync(await first.GetAsync("/api/v1/admin/users?role=Admin"))).GetProperty("items").EnumerateArray().ToList();
        var firstId = admins.Single(a => a.GetProperty("isSelf").GetBoolean()).GetProperty("id").GetGuid();
        var secondId = admins.Single(a => !a.GetProperty("isSelf").GetBoolean()).GetProperty("id").GetGuid();

        var results = await Task.WhenAll(
            first.PatchAsync($"/api/v1/admin/users/{secondId}/role", new { role = "Customer" }),
            second.PatchAsync($"/api/v1/admin/users/{firstId}/role", new { role = "Customer" }));

        results.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        (await ScalarAsync<long>("SELECT count(*) FROM identity.users WHERE role = 'Admin' AND is_active", new { })).ShouldBe(1);
        (await ScalarAsync<long>("SELECT count(*) FROM customers.customers c JOIN identity.users u ON u.id = c.id WHERE u.role = 'Customer'", new { }))
            .ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact, Trait("Spec", "RF-055")]
    public async Task Dashboard_SumsWhatWasPaid_AndShowsWhatToDo()
    {
        using var paid = await OrderAsync("painel1@loja.teste");
        using var pending = await OrderAsync("painel2@loja.teste", paid: false);
        var total = (await JsonAsync(await paid.Admin.GetAsync($"/api/v1/admin/orders/{paid.Order}"))).GetProperty("total").GetDecimal();
        // Pagamento aprovado fora do período: não entra no faturamento.
        await ExecuteAsync("UPDATE payments.payments SET approved_at = now() - interval '90 days' WHERE order_id = @Id", new { Id = paid.Order });
        using var recent = await OrderAsync("painel3@loja.teste");

        var dashboard = await JsonAsync(await paid.Admin.GetAsync("/api/v1/admin/dashboard"));

        dashboard.GetProperty("revenue").GetDecimal().ShouldBe(total);
        dashboard.GetProperty("paidOrders").GetInt32().ShouldBe(1);
        dashboard.GetProperty("averageTicket").GetDecimal().ShouldBe(total);
        dashboard.GetProperty("ordersPlaced").GetInt32().ShouldBe(3);
        dashboard.GetProperty("toDo").GetProperty("toPrepare").GetInt32().ShouldBe(2);
        dashboard.GetProperty("toDo").GetProperty("awaitingPayment").GetInt32().ShouldBe(1);

        var older = await JsonAsync(await paid.Admin.GetAsync(
            $"/api/v1/admin/dashboard?from={DateTime.UtcNow.AddDays(-120):yyyy-MM-dd}&to={DateTime.UtcNow.AddDays(-60):yyyy-MM-dd}"));
        older.GetProperty("revenue").GetDecimal().ShouldBe(total);
        older.GetProperty("ordersPlaced").GetInt32().ShouldBe(0);

        (await paid.Admin.GetAsync("/api/v1/admin/dashboard?from=2026-11-10&to=2026-11-01")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
