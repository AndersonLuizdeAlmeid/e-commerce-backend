using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ECommerce.Api.FunctionalTests.Catalog;
using ECommerce.Api.FunctionalTests.Infrastructure;

namespace ECommerce.Api.FunctionalTests.Carts;

public sealed class CartTests(ApiFactory factory) : AdminCatalogTest(factory)
{
    private static async Task<Guid> VariantAsync(AuthClient admin, Guid product) =>
        (await GetProductAsync(admin, product)).GetProperty("variants")[0].GetProperty("id").GetGuid();

    /// <summary>Peça na loja com o saldo pedido, criada pelo painel, como faz a administradora.</summary>
    private static async Task<(Guid Product, Guid Variant)> PieceAsync(AuthClient admin, string name, int stock = 5)
    {
        var product = await ProductInStoreAsync(admin, name);
        var variant = await VariantAsync(admin, product);
        if (stock > 0)
        {
            (await admin.PostAsync($"/api/v1/admin/stock/{variant}/movements", new { type = "In", quantity = stock, reason = "Teste" })).EnsureSuccessStatusCode();
        }

        return (product, variant);
    }

    private Task<AuthClient> CustomerAsync(string email = "cliente@loja.teste") => AuthClient.RegisteredAsync(Factory, email);

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken);
    }

    private static Task<HttpResponseMessage> AddAsync(AuthClient customer, Guid variant, int quantity = 1) =>
        customer.PostAsync("/api/v1/cart/items", new { variantId = variant, quantity });

    [Fact, Trait("Spec", "RF-030")]
    public async Task Cart_AddChangeAndRemove_ReturnsTheUpdatedCart()
    {
        using var admin = await AdminAsync();
        var (_, ring) = await PieceAsync(admin, "Anel Liso");
        var (_, bag) = await PieceAsync(admin, "Bolsa Shopper");
        using var customer = await CustomerAsync();

        (await JsonAsync(await customer.GetAsync("/api/v1/cart"))).GetProperty("items").GetArrayLength().ShouldBe(0);

        await AddAsync(customer, ring, 2);
        var added = await JsonAsync(await AddAsync(customer, ring));
        var item = added.GetProperty("items")[0];
        item.GetProperty("quantity").GetInt32().ShouldBe(3);
        item.GetProperty("productName").GetString().ShouldBe("Anel Liso");
        item.GetProperty("productSlug").GetString().ShouldBe("anel-liso");
        item.GetProperty("imageUrl").GetString().ShouldNotBeNullOrEmpty();
        item.GetProperty("status").GetString().ShouldBe("Available");
        item.GetProperty("unitPrice").GetDecimal().ShouldBe(89.90m);
        added.GetProperty("subtotal").GetDecimal().ShouldBe(269.70m);

        await AddAsync(customer, bag);
        var changed = await JsonAsync(await customer.PutAsync($"/api/v1/cart/items/{ring}", new { quantity = 1 }));
        changed.GetProperty("subtotal").GetDecimal().ShouldBe(179.80m);

        var removed = await JsonAsync(await customer.DeleteAsync($"/api/v1/cart/items/{ring}"));
        removed.GetProperty("items").GetArrayLength().ShouldBe(1);
        removed.GetProperty("items")[0].GetProperty("variantId").GetGuid().ShouldBe(bag);

        (await customer.DeleteAsync("/api/v1/cart")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await JsonAsync(await customer.GetAsync("/api/v1/cart"))).GetProperty("items").GetArrayLength().ShouldBe(0);
    }

    [Fact, Trait("Spec", "RF-030-CA1")]
    public async Task Quantity_OutOfTheRange_IsRefused()
    {
        using var admin = await AdminAsync();
        var (_, ring) = await PieceAsync(admin, "Anel Liso");
        using var customer = await CustomerAsync();
        await AddAsync(customer, ring, 98);

        (await AddAsync(customer, ring, 0)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AddAsync(customer, ring, 100)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AddAsync(customer, ring, 2)).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await customer.PutAsync($"/api/v1/cart/items/{ring}", new { quantity = 100 })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact, Trait("Spec", "RF-030-CA2")]
    public async Task Add_InactiveOrUnknownVariant_Returns422()
    {
        using var admin = await AdminAsync();
        var (product, ring) = await PieceAsync(admin, "Anel Liso");
        using var customer = await CustomerAsync();

        (await AddAsync(customer, Guid.CreateVersion7())).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        (await admin.PatchAsync($"/api/v1/admin/products/{product}/status", new { isActive = false })).EnsureSuccessStatusCode();
        var response = await AddAsync(customer, ring);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadAsStringAsync(CancellationToken)).ShouldContain("Carts.VariantUnavailable");
    }

    [Fact, Trait("Spec", "RF-030-CA3")]
    public async Task Cart_ShowsTheCurrentPrice_AndFlagsStock()
    {
        using var admin = await AdminAsync();
        var (product, ring) = await PieceAsync(admin, "Anel Liso", stock: 2);
        var (_, bag) = await PieceAsync(admin, "Bolsa Shopper", stock: 0);
        using var customer = await CustomerAsync();
        await AddAsync(customer, ring, 3);
        await AddAsync(customer, bag);

        await Factory.ExecuteSqlAsync("UPDATE catalog.products SET base_price = 100 WHERE id = @product", new { product });
        var cart = await JsonAsync(await customer.GetAsync("/api/v1/cart"));

        var items = cart.GetProperty("items");
        items[0].GetProperty("unitPrice").GetDecimal().ShouldBe(100m);
        items[0].GetProperty("status").GetString().ShouldBe("InsufficientStock");
        items[0].GetProperty("availableQuantity").GetInt32().ShouldBe(2);
        items[1].GetProperty("status").GetString().ShouldBe("OutOfStock");
        items[1].GetProperty("availableQuantity").ValueKind.ShouldBe(JsonValueKind.Null);
        cart.GetProperty("subtotal").GetDecimal().ShouldBe(300m);
    }

    [Fact, Trait("Spec", "RF-030-CA3")]
    public async Task Cart_PieceThatLeftTheStore_IsFlagged_AndOutOfTheSubtotal()
    {
        using var admin = await AdminAsync();
        var (product, ring) = await PieceAsync(admin, "Anel Liso");
        using var customer = await CustomerAsync();
        await AddAsync(customer, ring);

        (await admin.PatchAsync($"/api/v1/admin/products/{product}/status", new { isActive = false })).EnsureSuccessStatusCode();
        var cart = await JsonAsync(await customer.GetAsync("/api/v1/cart"));

        cart.GetProperty("items")[0].GetProperty("status").GetString().ShouldBe("Unavailable");
        cart.GetProperty("subtotal").GetDecimal().ShouldBe(0m);
    }

    [Fact, Trait("Spec", "RF-030-CA4")]
    public async Task Cart_DoesNotReserveStock()
    {
        using var admin = await AdminAsync();
        var (_, ring) = await PieceAsync(admin, "Anel Liso", stock: 1);
        using var customer = await CustomerAsync();

        await AddAsync(customer, ring);

        (await Factory.QuerySingleAsync<int>("SELECT quantity_on_hand FROM inventory.stock_items WHERE variant_id = @ring", new { ring })).ShouldBe(1);
    }

    [Fact, Trait("Spec", "RF-030")]
    public async Task SimultaneousAdds_NoneIsLost()
    {
        using var admin = await AdminAsync();
        var (_, ring) = await PieceAsync(admin, "Anel Liso");
        using var customer = await CustomerAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => AddAsync(customer, ring)));

        responses.ShouldAllBe(response => response.StatusCode == HttpStatusCode.OK);
        (await JsonAsync(await customer.GetAsync("/api/v1/cart"))).GetProperty("items")[0].GetProperty("quantity").GetInt32().ShouldBe(6);
    }

    [Fact, Trait("Spec", "RF-031")]
    public async Task Merge_SumsWithTheLimit_AndReturnsWhatLeftTheStore()
    {
        using var admin = await AdminAsync();
        var (_, ring) = await PieceAsync(admin, "Anel Liso");
        var (_, bag) = await PieceAsync(admin, "Bolsa Shopper");
        var (hiddenProduct, hidden) = await PieceAsync(admin, "Colar Antigo");
        (await admin.PatchAsync($"/api/v1/admin/products/{hiddenProduct}/status", new { isActive = false })).EnsureSuccessStatusCode();
        var unknown = Guid.CreateVersion7();
        using var customer = await CustomerAsync();
        await AddAsync(customer, ring, 95);

        var merged = await JsonAsync(await customer.PostAsync("/api/v1/cart/merge", new
        {
            items = new[]
            {
                new { variantId = ring, quantity = 10 },
                new { variantId = bag, quantity = 2 },
                new { variantId = hidden, quantity = 1 },
                new { variantId = unknown, quantity = 1 },
            },
        }));

        var items = merged.GetProperty("cart").GetProperty("items");
        items.GetArrayLength().ShouldBe(2);
        items[0].GetProperty("quantity").GetInt32().ShouldBe(99);
        items[1].GetProperty("quantity").GetInt32().ShouldBe(2);
        merged.GetProperty("skippedVariantIds").EnumerateArray().Select(id => id.GetGuid()).ShouldBe([hidden, unknown], ignoreOrder: true);
    }

    [Fact, Trait("Spec", "RF-031")]
    public async Task Merge_InvalidQuantity_Returns400_AndChangesNothing()
    {
        using var admin = await AdminAsync();
        var (_, ring) = await PieceAsync(admin, "Anel Liso");
        using var customer = await CustomerAsync();

        (await customer.PostAsync("/api/v1/cart/merge", new { items = new[] { new { variantId = ring, quantity = 0 } } })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await JsonAsync(await customer.GetAsync("/api/v1/cart"))).GetProperty("items").GetArrayLength().ShouldBe(0);
    }

    [Fact, Trait("Spec", "16-CARRINHO")]
    public async Task Lookup_IsPublic_AndAnswersLikeTheCart_WithoutUnknownVariants()
    {
        using var admin = await AdminAsync();
        var (_, ring) = await PieceAsync(admin, "Anel Liso", stock: 1);
        var (_, bag) = await PieceAsync(admin, "Bolsa Shopper");

        var response = await Client.PostAsJsonAsync(
            "/api/v1/catalog/variants/lookup",
            new { items = new[] { new { variantId = bag, quantity = 1 }, new { variantId = Guid.CreateVersion7(), quantity = 1 }, new { variantId = ring, quantity = 2 } } },
            CancellationToken);

        var cart = await JsonAsync(response);
        var items = cart.GetProperty("items");
        items.GetArrayLength().ShouldBe(2);
        items[0].GetProperty("variantId").GetGuid().ShouldBe(bag);
        items[1].GetProperty("status").GetString().ShouldBe("InsufficientStock");
        cart.GetProperty("subtotal").GetDecimal().ShouldBe(269.70m);
    }

    [Fact, Trait("Spec", "16-CARRINHO")]
    public async Task Lookup_OverTheLimit_Returns400()
    {
        var items = Enumerable.Range(0, 51).Select(_ => new { variantId = Guid.CreateVersion7(), quantity = 1 }).ToArray();

        var response = await Client.PostAsJsonAsync("/api/v1/catalog/variants/lookup", new { items }, CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact, Trait("Spec", "09-CART")]
    public async Task CartRoutes_AreOnlyForCustomers()
    {
        (await Client.GetAsync("/api/v1/cart", CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var admin = await AdminAsync();
        (await admin.GetAsync("/api/v1/cart")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact, Trait("Spec", "RF-030")]
    public async Task Cart_BelongsToItsOwner()
    {
        using var admin = await AdminAsync();
        var (_, ring) = await PieceAsync(admin, "Anel Liso");
        using var first = await CustomerAsync("primeira@loja.teste");
        using var second = await CustomerAsync("segunda@loja.teste");

        await AddAsync(first, ring);

        (await JsonAsync(await second.GetAsync("/api/v1/cart"))).GetProperty("items").GetArrayLength().ShouldBe(0);
    }
}
