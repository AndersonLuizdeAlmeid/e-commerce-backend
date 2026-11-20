using System.Net;
using System.Text.Json;
using ECommerce.Api.FunctionalTests.Infrastructure;
using ECommerce.Api.FunctionalTests.Ordering;
using ECommerce.Application.Abstractions.Authentication;
using ECommerce.Domain.Identity;
using ECommerce.Infrastructure.Payments;
using Microsoft.Extensions.DependencyInjection;

namespace ECommerce.Api.FunctionalTests.Identity;

/// <summary>Fase 6.3: exportar dados, excluir conta (entrega 6 §3, P7 e P8) e devolução de pedido entregue (P11).</summary>
public sealed class PrivacyTests(ApiFactory factory) : ShopperTest(factory)
{
    private static object ApprovedCard() => new { method = "CreditCard", cardToken = FakePaymentGateway.ApprovedCardToken, paymentMethodId = "visa" };

    private static object Gift() => new { isGift = true, wrap = false, message = "Para a Ana, com carinho" };

    /// <summary>Cliente com um pedido feito (com dedicatória), pago no cartão quando <paramref name="paid"/>.</summary>
    private async Task<(AuthClient Admin, AuthClient Customer, Guid Order, Guid Ring)> OrderAsync(string email, bool paid = true)
    {
        var admin = await StoreAsync();
        var ring = await PieceAsync(admin, $"Anel {email.Split('@')[0]}", 5);
        var (customer, address) = await ShopperAsync(email, (ring, 1));
        var order = (await JsonAsync(await PlaceAsync(customer, address, gift: Gift()), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        if (paid)
        {
            (await JsonAsync(await customer.PostAsync($"/api/v1/orders/{order}/payments", ApprovedCard()))).GetProperty("status").GetString().ShouldBe("Approved");
        }

        return (admin, customer, order, ring);
    }

    private static async Task<JsonElement> DeliverAsync(AuthClient admin, Guid order)
    {
        (await admin.PostAsync($"/api/v1/admin/orders/{order}/start-preparation", null)).EnsureSuccessStatusCode();
        (await admin.PostAsync($"/api/v1/admin/orders/{order}/ship", new { trackingCode = "AA123456789BR" })).EnsureSuccessStatusCode();
        return await JsonAsync(await admin.PostAsync($"/api/v1/admin/orders/{order}/deliver", null));
    }

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

    [Fact, Trait("Spec", "ENT6-C7")]
    public async Task Export_HasMyProfileAddressesAndOrders_AndNothingFromOthers()
    {
        var (admin, customer, order, _) = await OrderAsync("exporta@loja.teste");
        using var _admin = admin;
        using var _customer = customer;
        var ring = await PieceAsync(admin, "Anel da outra", 3);
        var (other, otherAddress) = await ShopperAsync("outra@loja.teste", (ring, 1));
        using var _other = other;
        await JsonAsync(await PlaceAsync(other, otherAddress), HttpStatusCode.Created);

        var response = await customer.GetAsync("/api/v1/me/data-export");

        var export = await JsonAsync(response);
        response.Content.Headers.ContentDisposition?.DispositionType.ShouldBe("attachment");
        var json = export.GetRawText();
        json.ShouldNotContain("outra@loja.teste");
        export.GetProperty("account").GetProperty("email").GetString().ShouldBe("exporta@loja.teste");
        export.GetProperty("account").GetProperty("fullName").GetString().ShouldBe("Maria da Silva");
        export.GetProperty("addresses").EnumerateArray().Single().GetProperty("street").GetString().ShouldBe("Rua da Assembleia");
        var mine = export.GetProperty("orders").EnumerateArray().Single();
        mine.GetProperty("status").GetString().ShouldBe("PaymentConfirmed");
        mine.GetProperty("giftMessage").GetString().ShouldBe("Para a Ana, com carinho");
        mine.GetProperty("items").EnumerateArray().Single().GetProperty("productName").GetString().ShouldBe("Anel exporta");
        mine.GetProperty("history").EnumerateArray().Select(h => h.GetProperty("status").GetString()).ShouldBe(["PendingPayment", "PaymentConfirmed"]);
        mine.GetProperty("payments").EnumerateArray().Single().GetProperty("status").GetString().ShouldBe("Approved");
        export.GetProperty("notes").GetArrayLength().ShouldBeGreaterThan(0);
        (await JsonAsync(await customer.GetAsync($"/api/v1/orders/{order}"))).GetProperty("id").GetGuid().ShouldBe(order);
    }

    [Fact, Trait("Spec", "ENT6-P8")]
    public async Task Export_IsLimitedToThreeADay_PerAccount()
    {
        using var customer = await AuthClient.RegisteredAsync(Factory, "tresvezes@loja.teste");
        using var other = await AuthClient.RegisteredAsync(Factory, "outravez@loja.teste");

        for (var i = 0; i < 3; i++)
        {
            (await customer.GetAsync("/api/v1/me/data-export")).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        (await customer.GetAsync("/api/v1/me/data-export")).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await other.GetAsync("/api/v1/me/data-export")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await new AuthClient(Factory).GetAsync("/api/v1/me/data-export")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact, Trait("Spec", "ENT6-C8")]
    public async Task Delete_WithAnOrderAwaitingPayment_Is422_AndAfterTheCancellationRemovesEverythingPersonal()
    {
        var (admin, customer, order, _) = await OrderAsync("exclui@loja.teste", paid: false);
        using var _admin = admin;
        using var _customer = customer;
        var userId = await ScalarAsync<Guid>("SELECT id FROM identity.users WHERE email = 'exclui@loja.teste'", new { });

        var blocked = await customer.DeleteAsync("/api/v1/me", new { password = AuthClient.ValidPassword });
        blocked.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeAsync(blocked)).ShouldBe("Identity.AccountHasOpenOrders");

        (await customer.PostAsync($"/api/v1/orders/{order}/cancel", null)).EnsureSuccessStatusCode();
        (await customer.DeleteAsync("/api/v1/me", new { password = "" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var wrong = await customer.DeleteAsync("/api/v1/me", new { password = "Errada@123" });
        (await CodeAsync(wrong)).ShouldBe("Identity.WrongCurrentPassword");

        var deleted = await customer.DeleteAsync("/api/v1/me", new { password = AuthClient.ValidPassword });

        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        deleted.Headers.GetValues("Set-Cookie").ShouldContain(cookie => cookie.StartsWith(AuthClient.CookieName + "=;", StringComparison.Ordinal));
        (await new AuthClient(Factory).LoginAsync("exclui@loja.teste")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        // O access token antigo para já no próximo request, sem esperar o minuto do cache do stamp.
        (await customer.GetAsync("/api/v1/me/profile")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await customer.RefreshAsync()).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // Banco: nada que identifique a pessoa; o pedido continua, com valores e peças.
        (await ScalarAsync<string>("SELECT email::text FROM identity.users WHERE id = @Id", new { Id = userId })).ShouldBe(User.AnonymizedEmailFor(new UserId(userId)));
        (await ScalarAsync<bool>("SELECT anonymized_at IS NOT NULL AND NOT is_active FROM identity.users WHERE id = @Id", new { Id = userId })).ShouldBeTrue();
        (await ScalarAsync<string>("SELECT full_name || '|' || coalesce(phone, '') || '|' || coalesce(cpf, '') FROM customers.customers WHERE id = @Id", new { Id = userId }))
            .ShouldBe("Cliente removida||");
        (await ScalarAsync<int>("SELECT count(*)::int FROM customers.addresses WHERE customer_id = @Id", new { Id = userId })).ShouldBe(0);
        (await ScalarAsync<int>("SELECT count(*)::int FROM identity.refresh_tokens WHERE user_id = @Id AND revoked_at IS NULL", new { Id = userId })).ShouldBe(0);
        var kept = await ScalarAsync<string>(
            "SELECT ship_recipient || '|' || ship_street || '|' || ship_postal_code || '|' || coalesce(gift_message, '-') || '|' || ship_city || '|' || total::text FROM ordering.orders WHERE id = @Id",
            new { Id = order });
        kept.ShouldStartWith("Cliente removida|Removido|00000000|-|Rio de Janeiro|");
        (await ScalarAsync<int>("SELECT count(*)::int FROM ordering.order_items WHERE order_id = @Id", new { Id = order })).ShouldBe(1);

        // Painel: "Cliente removida", sem como reativar; o e-mail fica livre para um cadastro novo.
        var detail = await JsonAsync(await admin.GetAsync($"/api/v1/admin/orders/{order}"));
        detail.GetProperty("customer").GetProperty("name").GetString().ShouldBe("Cliente removida");
        detail.GetProperty("customer").GetProperty("removed").GetBoolean().ShouldBeTrue();
        var reactivate = await admin.PatchAsync($"/api/v1/admin/users/{userId}/status", new { isActive = true });
        (await CodeAsync(reactivate)).ShouldBe("Identity.AccountRemoved");
        var listed = (await JsonAsync(await admin.GetAsync("/api/v1/admin/users?search=removida"))).GetProperty("items").EnumerateArray().Single();
        listed.GetProperty("id").GetGuid().ShouldBe(userId);
        listed.GetProperty("name").GetString().ShouldBe("Cliente removida");
        listed.GetProperty("removed").GetBoolean().ShouldBeTrue();
        using var again = new AuthClient(Factory);
        (await again.RegisterAsync("exclui@loja.teste")).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact, Trait("Spec", "ENT6-P7")]
    public async Task Delete_WithARefundStillPending_Is422()
    {
        var (admin, customer, order, _) = await OrderAsync("estornopendente@loja.teste");
        using var _admin = admin;
        using var _customer = customer;
        Factory.Payments.RejectRefunds = true;
        var cancelled = await JsonAsync(await admin.PostAsync($"/api/v1/admin/orders/{order}/cancel", new { reason = "Sem a peça" }));
        (await WaitForPaymentStatusAsync(cancelled.GetProperty("payments")[0].GetProperty("id").GetGuid(), "RefundFailed")).ShouldBe("RefundFailed");

        var response = await customer.DeleteAsync("/api/v1/me", new { password = AuthClient.ValidPassword });

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeAsync(response)).ShouldBe("Identity.AccountHasPendingRefund");
        (await customer.GetAsync("/api/v1/me/profile")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact, Trait("Spec", "ENT6-P7")]
    public void RemovedPasswordHash_NeverMatches_AndDoesNotThrow()
    {
        using var scope = Factory.Services.CreateScope();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        hasher.Verify(User.RemovedPasswordHash, AuthClient.ValidPassword).ShouldBe(PasswordCheck.Failed);
        hasher.Verify(User.RemovedPasswordHash, string.Empty).ShouldBe(PasswordCheck.Failed);
    }

    [Fact, Trait("Spec", "ENT6-C9")]
    public async Task Return_OfADeliveredOrder_RefundsByTheOutbox_SendsEm13_AndLeavesTheStock()
    {
        var (admin, customer, order, ring) = await OrderAsync("devolve@loja.teste");
        using var _admin = admin;
        using var _customer = customer;
        var delivered = await DeliverAsync(admin, order);
        delivered.GetProperty("allowedActions").EnumerateArray().Select(a => a.GetString()).ShouldBe(["Return"]);
        var stock = await StockAsync(ring);

        (await admin.PostAsync($"/api/v1/admin/orders/{order}/return", new { note = " " })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var returned = await JsonAsync(await admin.PostAsync($"/api/v1/admin/orders/{order}/return", new { note = "Arrependimento, peça conferida sem uso" }));

        returned.GetProperty("status").GetString().ShouldBe("Returned");
        returned.GetProperty("allowedActions").GetArrayLength().ShouldBe(0);
        var last = returned.GetProperty("history").EnumerateArray().Last();
        last.GetProperty("toStatus").GetString().ShouldBe("Returned");
        last.GetProperty("note").GetString().ShouldBe("Arrependimento, peça conferida sem uso");
        var payment = returned.GetProperty("payments")[0].GetProperty("id").GetGuid();
        (await WaitForPaymentStatusAsync(payment, "Refunded")).ShouldBe("Refunded");
        await Factory.Emails.WaitForAsync("devolve@loja.teste", "Estorno do pedido");
        (await StockAsync(ring)).ShouldBe(stock);
        (await JsonAsync(await customer.GetAsync($"/api/v1/orders/{order}"))).GetProperty("status").GetString().ShouldBe("Returned");

        var again = await admin.PostAsync($"/api/v1/admin/orders/{order}/return", new { note = "De novo" });
        again.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeAsync(again)).ShouldBe("Ordering.InvalidTransition");
    }

    [Fact, Trait("Spec", "ENT6-P11")]
    public async Task Return_BeforeDelivery_Is422()
    {
        var (admin, customer, order, _) = await OrderAsync("cedo@loja.teste");
        using var _admin = admin;
        using var _customer = customer;

        var response = await admin.PostAsync($"/api/v1/admin/orders/{order}/return", new { note = "Ainda não chegou" });

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeAsync(response)).ShouldBe("Ordering.InvalidTransition");
    }

    [Fact, Trait("Spec", "ENT6-P11")]
    public async Task Return_OfAManualPayment_WaitsInThePendingRefunds()
    {
        var (admin, customer, order, _) = await OrderAsync("devolvemanual@loja.teste", paid: false);
        using var _admin = admin;
        using var _customer = customer;
        (await admin.PostAsync($"/api/v1/admin/orders/{order}/confirm-payment", new { reason = "Dinheiro na loja" })).EnsureSuccessStatusCode();
        await DeliverAsync(admin, order);

        await JsonAsync(await admin.PostAsync($"/api/v1/admin/orders/{order}/return", new { note = "Troca de tamanho sem peça no estoque" }));

        var row = (await JsonAsync(await admin.GetAsync("/api/v1/admin/payments/refunds/pending"))).EnumerateArray().Single();
        row.GetProperty("kind").GetString().ShouldBe("ManualPayment");
        row.GetProperty("orderStatus").GetString().ShouldBe("Returned");
        var payment = row.GetProperty("paymentId").GetGuid();
        (await admin.PostAsync($"/api/v1/admin/payments/{payment}/mark-refunded", new { note = "Devolvido por PIX" })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await Factory.Emails.WaitForAsync("devolvemanual@loja.teste", "Estorno do pedido");
    }
}
