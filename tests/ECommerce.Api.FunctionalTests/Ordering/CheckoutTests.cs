using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using ECommerce.Api.FunctionalTests.Catalog;
using ECommerce.Api.FunctionalTests.Infrastructure;
using ECommerce.Application.Abstractions.Caching;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Application.Ordering;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace ECommerce.Api.FunctionalTests.Ordering;

public sealed class CheckoutTests(ApiFactory factory) : ShopperTest(factory)
{
    [Fact, Trait("Spec", "RF-040-CA4")]
    public async Task Checkout_CreatesTheOrder_ReservesStock_EmptiesTheCart_AndEmailsTheCustomer()
    {
        using var admin = await StoreAsync();
        var ring = await PieceAsync(admin, "Anel Liso", stock: 5);
        var (customer, address) = await ShopperAsync("compra@loja.teste", (ring, 2));
        using var shopper = customer;

        var placed = await JsonAsync(await PlaceAsync(customer, address), HttpStatusCode.Created);

        placed.GetProperty("number").GetString().ShouldStartWith("PED-");
        placed.GetProperty("total").GetDecimal().ShouldBe(89.90m * 2 + 21.40m);
        (await StockAsync(ring)).ShouldBe(3);
        (await ScalarAsync<string>(
            "SELECT type FROM inventory.stock_movements WHERE variant_id = @Id AND order_id IS NOT NULL", new { Id = ring })).ShouldBe("OrderPlaced");
        (await JsonAsync(await customer.GetAsync("/api/v1/cart"))).GetProperty("items").GetArrayLength().ShouldBe(0);

        var id = placed.GetProperty("id").GetGuid();
        var order = await JsonAsync(await customer.GetAsync($"/api/v1/orders/{id}"));
        order.GetProperty("status").GetString().ShouldBe("PendingPayment");
        order.GetProperty("canCancel").GetBoolean().ShouldBeTrue();
        order.GetProperty("shipping").GetProperty("serviceCode").GetString().ShouldBe("PAC");
        order.GetProperty("address").GetProperty("city").GetString().ShouldBe("Rio de Janeiro");
        order.GetProperty("items")[0].GetProperty("productName").GetString().ShouldBe("Anel Liso");
        order.GetProperty("items")[0].GetProperty("quantity").GetInt32().ShouldBe(2);

        var email = await Factory.Emails.WaitForAsync("compra@loja.teste", "Recebemos seu pedido");
        email.TextBody.ShouldContain("Anel Liso");
        email.TextBody.ShouldContain($"/conta/pedidos/{id}");

        var list = await JsonAsync(await customer.GetAsync("/api/v1/orders"));
        list.GetProperty("items")[0].GetProperty("id").GetGuid().ShouldBe(id);
        list.GetProperty("items")[0].GetProperty("itemCount").GetInt32().ShouldBe(1);
    }

    [Fact, Trait("Spec", "D05")]
    public async Task Checkout_WithoutConfirmedEmail_IsRefused()
    {
        using var admin = await StoreAsync();
        var ring = await PieceAsync(admin, "Anel Liso", stock: 5);
        var (customer, address) = await ShopperAsync("naoconfirmou@loja.teste", (ring, 1));
        using var shopper = customer;
        await ExecuteAsync("UPDATE identity.users SET email_confirmed_at = NULL WHERE email = @Email", new { Email = "naoconfirmou@loja.teste" });

        var response = await PlaceAsync(customer, address);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeAsync(response)).ShouldBe("Identity.EmailNotConfirmed");
    }

    [Fact, Trait("Spec", "RF-040-CA1")]
    public async Task Checkout_WithEmptyCart_IsRefused()
    {
        using var admin = await StoreAsync();
        var (customer, address) = await ShopperAsync("vazia@loja.teste");
        using var shopper = customer;

        var response = await PlaceAsync(customer, address);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeAsync(response)).ShouldBe("Ordering.EmptyCart");
    }

    [Fact, Trait("Spec", "RF-007-CA1")]
    public async Task Checkout_WithAnotherCustomersAddress_IsRefused()
    {
        using var admin = await StoreAsync();
        var ring = await PieceAsync(admin, "Anel Liso", stock: 5);
        var (owner, ownerAddress) = await ShopperAsync("dona@loja.teste");
        var (customer, _) = await ShopperAsync("outra@loja.teste", (ring, 1));
        using var o = owner;
        using var c = customer;

        var response = await PlaceAsync(customer, ownerAddress);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeAsync(response)).ShouldBe("Ordering.AddressNotFound");
    }

    [Fact, Trait("Spec", "RF-040-CA2")]
    public async Task Checkout_WithoutEnoughStock_ListsThePieces_AndChangesNothing()
    {
        using var admin = await StoreAsync();
        var ring = await PieceAsync(admin, "Anel Liso", stock: 1);
        var bag = await PieceAsync(admin, "Bolsa Shopper", stock: 3);
        var (customer, address) = await ShopperAsync("muito@loja.teste", (ring, 2), (bag, 1));
        using var shopper = customer;

        var response = await PlaceAsync(customer, address);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken);
        problem.GetProperty("code").GetString().ShouldBe("Ordering.OutOfStock");
        var item = problem.GetProperty("items").EnumerateArray().Single();
        item.GetProperty("variantId").GetGuid().ShouldBe(ring);
        item.GetProperty("available").GetInt32().ShouldBe(1);

        (await StockAsync(ring)).ShouldBe(1);
        (await StockAsync(bag)).ShouldBe(3);
        (await ScalarAsync<long>("SELECT count(*) FROM ordering.orders", new { })).ShouldBe(0);
        (await JsonAsync(await customer.GetAsync("/api/v1/cart"))).GetProperty("items").GetArrayLength().ShouldBe(2);
    }

    [Fact, Trait("Spec", "RF-040-CA2")]
    public async Task TwoCustomersBuyingTheLastUnit_OnlyOneGetsIt()
    {
        using var admin = await StoreAsync();
        var ring = await PieceAsync(admin, "Anel Liso", stock: 1);
        var (first, firstAddress) = await ShopperAsync("primeira@loja.teste", (ring, 1));
        var (second, secondAddress) = await ShopperAsync("segunda@loja.teste", (ring, 1));
        using var f = first;
        using var s = second;

        var responses = await Task.WhenAll(PlaceAsync(first, firstAddress), PlaceAsync(second, secondAddress));

        responses.Count(response => response.StatusCode == HttpStatusCode.Created).ShouldBe(1);
        responses.Count(response => response.StatusCode == HttpStatusCode.Conflict).ShouldBe(1);
        (await StockAsync(ring)).ShouldBe(0);
        (await ScalarAsync<long>("SELECT count(*) FROM ordering.orders", new { })).ShouldBe(1);
    }

    [Fact, Trait("Spec", "RF-040-CA7")]
    public async Task SameIdempotencyKey_ReturnsTheSameOrder()
    {
        using var admin = await StoreAsync();
        var ring = await PieceAsync(admin, "Anel Liso", stock: 5);
        var (customer, address) = await ShopperAsync("duplo@loja.teste", (ring, 1));
        using var shopper = customer;
        var key = Guid.NewGuid().ToString();

        var responses = await Task.WhenAll(PlaceAsync(customer, address, key: key), PlaceAsync(customer, address, key: key));

        responses.ShouldContain(response => response.StatusCode == HttpStatusCode.Created);
        responses.ShouldContain(response => response.StatusCode == HttpStatusCode.OK);
        var ids = await Task.WhenAll(responses.Select(async response =>
            (await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken)).GetProperty("id").GetGuid()));
        ids.Distinct().Count().ShouldBe(1);
        (await StockAsync(ring)).ShouldBe(4);
    }

    [Fact, Trait("Spec", "RF-047")]
    public async Task Gift_AddsTheWrapPriceOfTheMoment_AndCarriesTheMessage()
    {
        using var admin = await StoreAsync();
        (await admin.PutAsync("/api/v1/admin/ordering/settings", new { giftWrapPrice = 12m })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var ring = await PieceAsync(admin, "Anel Liso", stock: 5);
        var (customer, address) = await ShopperAsync("presente@loja.teste", (ring, 1));
        using var shopper = customer;

        var placed = await JsonAsync(
            await PlaceAsync(customer, address, gift: new { wrap = true, message = "Feliz aniversário,\nMãe!" }), HttpStatusCode.Created);
        (await admin.PutAsync("/api/v1/admin/ordering/settings", new { giftWrapPrice = 20m })).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        placed.GetProperty("total").GetDecimal().ShouldBe(89.90m + 21.40m + 12m);
        var order = await JsonAsync(await customer.GetAsync($"/api/v1/orders/{placed.GetProperty("id").GetGuid()}"));
        order.GetProperty("giftWrapPrice").GetDecimal().ShouldBe(12m);
        order.GetProperty("gift").GetProperty("message").GetString().ShouldBe("Feliz aniversário,\nMãe!");
        var email = await Factory.Emails.WaitForAsync("presente@loja.teste", "Recebemos seu pedido");
        email.TextBody.ShouldContain("Dedicatória: Feliz aniversário, Mãe!");
        // O ICU do Linux separa "R$" do valor com espaço rígido; o do Windows, com espaço comum.
        email.TextBody.Replace('\u00A0', ' ').ShouldContain("Embalagem para presente: R$ 12,00");
        email.HtmlBody.ShouldContain(",<br>M");
    }

    [Fact, Trait("Spec", "RF-047-CA1")]
    public async Task GiftMessage_TooLong_IsRefusedByField()
    {
        using var admin = await StoreAsync();
        var ring = await PieceAsync(admin, "Anel Liso", stock: 5);
        var (customer, address) = await ShopperAsync("longa@loja.teste", (ring, 1));
        using var shopper = customer;

        var response = await PlaceAsync(customer, address, gift: new { wrap = false, message = new string('a', 201) });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken)).GetProperty("errors").TryGetProperty("giftMessage", out _).ShouldBeTrue();
    }

    [Fact, Trait("Spec", "RF-040-CA3")]
    public async Task ShippingServiceThatIsNotQuoted_IsRefused()
    {
        using var admin = await StoreAsync();
        var ring = await PieceAsync(admin, "Anel Liso", stock: 5);
        var (customer, address) = await ShopperAsync("frete@loja.teste", (ring, 1));
        using var shopper = customer;

        var response = await PlaceAsync(customer, address, service: "JADLOG");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeAsync(response)).ShouldBe("Ordering.ShippingServiceUnavailable");
        (await StockAsync(ring)).ShouldBe(5);
    }

    [Fact, Trait("Spec", "RF-043")]
    public async Task Cancel_ReturnsTheStock_AndEmailsTheCustomer()
    {
        using var admin = await StoreAsync();
        var ring = await PieceAsync(admin, "Anel Liso", stock: 1);
        var (customer, address) = await ShopperAsync("cancela@loja.teste", (ring, 1));
        using var shopper = customer;
        var id = (await JsonAsync(await PlaceAsync(customer, address), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        (await StockAsync(ring)).ShouldBe(0);

        (await customer.PostAsync($"/api/v1/orders/{id}/cancel", null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await StockAsync(ring)).ShouldBe(1);
        var order = await JsonAsync(await customer.GetAsync($"/api/v1/orders/{id}"));
        order.GetProperty("status").GetString().ShouldBe("Cancelled");
        order.GetProperty("cancellationReason").GetString().ShouldBe("CustomerRequest");
        order.GetProperty("canCancel").GetBoolean().ShouldBeFalse();
        order.GetProperty("history").GetArrayLength().ShouldBe(2);
        (await Factory.Emails.WaitForAsync("cancela@loja.teste", "cancelado")).TextBody.ShouldContain("a seu pedido");

        var again = await customer.PostAsync($"/api/v1/orders/{id}/cancel", null);
        again.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact, Trait("Spec", "RF-042")]
    public async Task AnotherCustomersOrder_IsNotFound()
    {
        using var admin = await StoreAsync();
        var ring = await PieceAsync(admin, "Anel Liso", stock: 5);
        var (owner, address) = await ShopperAsync("dona@loja.teste", (ring, 1));
        using var o = owner;
        var id = (await JsonAsync(await PlaceAsync(owner, address), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        using var other = await AuthClient.RegisteredAsync(Factory, "curiosa@loja.teste");

        (await other.GetAsync($"/api/v1/orders/{id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await other.PostAsync($"/api/v1/orders/{id}/cancel", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await JsonAsync(await other.GetAsync("/api/v1/orders"))).GetProperty("items").GetArrayLength().ShouldBe(0);
        (await admin.GetAsync("/api/v1/orders")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact, Trait("Spec", "RF-046-CA1")]
    public async Task OrderWithout48HoursOfPayment_IsCancelledByTheJob()
    {
        using var admin = await StoreAsync();
        var ring = await PieceAsync(admin, "Anel Liso", stock: 1);
        var (customer, address) = await ShopperAsync("atrasada@loja.teste", (ring, 1));
        using var shopper = customer;
        var id = (await JsonAsync(await PlaceAsync(customer, address), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await ExecuteAsync("UPDATE ordering.orders SET payment_due_at = now() - interval '1 minute' WHERE id = @Id", new { Id = id });

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var job = scope.ServiceProvider.GetRequiredService<ICommandHandler<CancelOverdueOrdersCommand, int>>();
            (await job.Handle(new CancelOverdueOrdersCommand(), CancellationToken)).Value.ShouldBe(1);
            (await job.Handle(new CancelOverdueOrdersCommand(), CancellationToken)).Value.ShouldBe(0);
        }

        (await StockAsync(ring)).ShouldBe(1);
        var order = await JsonAsync(await customer.GetAsync($"/api/v1/orders/{id}"));
        order.GetProperty("cancellationReason").GetString().ShouldBe("PaymentTimeout");
        (await Factory.Emails.WaitForAsync("atrasada@loja.teste", "cancelado")).TextBody.ShouldContain("48 horas");
    }

    [Fact, Trait("Spec", "RF-046-CA2")]
    public async Task Reminder_IsSentOnceAfter24Hours()
    {
        using var admin = await StoreAsync();
        var ring = await PieceAsync(admin, "Anel Liso", stock: 5);
        var (customer, address) = await ShopperAsync("lembrete@loja.teste", (ring, 1));
        using var shopper = customer;
        var id = (await JsonAsync(await PlaceAsync(customer, address), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await ExecuteAsync("UPDATE ordering.orders SET placed_at = now() - interval '25 hours' WHERE id = @Id", new { Id = id });

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var job = scope.ServiceProvider.GetRequiredService<ICommandHandler<SendPaymentRemindersCommand, int>>();
            (await job.Handle(new SendPaymentRemindersCommand(), CancellationToken)).Value.ShouldBe(1);
            (await job.Handle(new SendPaymentRemindersCommand(), CancellationToken)).Value.ShouldBe(0);
        }

        await Factory.Emails.WaitForAsync("lembrete@loja.teste", "ainda espera o pagamento");
    }
}
