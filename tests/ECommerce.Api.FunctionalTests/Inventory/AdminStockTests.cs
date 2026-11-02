using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ECommerce.Api.FunctionalTests.Catalog;
using ECommerce.Api.FunctionalTests.Infrastructure;

namespace ECommerce.Api.FunctionalTests.Inventory;

public sealed class AdminStockTests(ApiFactory factory) : AdminCatalogTest(factory)
{
    private const string ValidBarcode = "7891234567895";

    private static async Task<Guid> VariantAsync(AuthClient admin, Guid product) =>
        (await GetProductAsync(admin, product)).GetProperty("variants")[0].GetProperty("id").GetGuid();

    private static Task<HttpResponseMessage> MoveAsync(AuthClient admin, Guid variant, string type, int quantity, string reason = "Reposição") =>
        admin.PostAsync($"/api/v1/admin/stock/{variant}/movements", new { type, quantity, reason });

    private static async Task<int> BalanceAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken)).GetProperty("quantityOnHand").GetInt32();
    }

    private Task<int> CatalogChangesAsync() =>
        Factory.QuerySingleAsync<int>("SELECT count(*)::int FROM messaging.outbox_messages WHERE type = 'catalog.changed'");

    private async Task<Guid> VariantWithBarcodeAsync(AuthClient admin, string name = "Anel Liso")
    {
        var product = await ProductInStoreAsync(admin, name);
        var variant = await VariantAsync(admin, product);
        await Factory.ExecuteSqlAsync("UPDATE catalog.product_variants SET barcode = @ValidBarcode WHERE id = @variant", new { ValidBarcode, variant });
        return variant;
    }

    [Fact, Trait("Spec", "RF-052")]
    public async Task Movements_ChangeTheBalance_AndKeepTheHistoryWithAuthor()
    {
        using var admin = await AdminAsync();
        var variant = await VariantAsync(admin, await DraftWithVariantAsync(admin));

        (await BalanceAsync(await MoveAsync(admin, variant, "In", 5))).ShouldBe(5);
        (await BalanceAsync(await MoveAsync(admin, variant, "Out", 2, "Avaria"))).ShouldBe(3);
        (await BalanceAsync(await MoveAsync(admin, variant, "Adjustment", 7, "Contagem"))).ShouldBe(7);

        var history = await (await admin.GetAsync($"/api/v1/admin/stock/{variant}/movements")).Content.ReadFromJsonAsync<JsonElement>(CancellationToken);
        var items = history.GetProperty("items");
        history.GetProperty("totalItems").GetInt32().ShouldBe(3);
        items[0].GetProperty("type").GetString().ShouldBe("Adjustment");
        items[0].GetProperty("quantity").GetInt32().ShouldBe(4);
        items[0].GetProperty("reason").GetString().ShouldBe("Contagem");
        items[0].GetProperty("performedBy").GetString().ShouldBe("admin@loja.teste");
        items[1].GetProperty("quantity").GetInt32().ShouldBe(-2);
        items[2].GetProperty("quantity").GetInt32().ShouldBe(5);
    }

    [Fact, Trait("Spec", "RF-052-CA1")]
    public async Task Out_BeyondTheBalance_IsRefused_AndChangesNothing()
    {
        using var admin = await AdminAsync();
        var variant = await VariantAsync(admin, await DraftWithVariantAsync(admin));
        await MoveAsync(admin, variant, "In", 2);

        var response = await MoveAsync(admin, variant, "Out", 3, "Avaria");

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadAsStringAsync(CancellationToken)).ShouldContain("Inventory.InsufficientStock");
        (await Factory.QuerySingleAsync<int>("SELECT quantity_on_hand FROM inventory.stock_items WHERE variant_id = @variant", new { variant })).ShouldBe(2);
        (await Factory.QuerySingleAsync<int>("SELECT count(*)::int FROM inventory.stock_movements WHERE variant_id = @variant", new { variant })).ShouldBe(1);
    }

    [Fact, Trait("Spec", "RF-052-CA1")]
    public async Task SimultaneousOuts_OfTheLastUnit_OnlyOnePasses()
    {
        using var admin = await AdminAsync();
        var variant = await VariantAsync(admin, await DraftWithVariantAsync(admin));
        await MoveAsync(admin, variant, "In", 1);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => MoveAsync(admin, variant, "Out", 1, "Avaria")));

        responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.UnprocessableEntity).ShouldBe(7);
        (await Factory.QuerySingleAsync<int>("SELECT quantity_on_hand FROM inventory.stock_items WHERE variant_id = @variant", new { variant })).ShouldBe(0);
    }

    [Fact, Trait("Spec", "RF-052-CA1")]
    public async Task SimultaneousIns_AllCount()
    {
        using var admin = await AdminAsync();
        var variant = await VariantAsync(admin, await DraftWithVariantAsync(admin));

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => MoveAsync(admin, variant, "In", 1)));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK);
        (await Factory.QuerySingleAsync<int>("SELECT quantity_on_hand FROM inventory.stock_items WHERE variant_id = @variant", new { variant })).ShouldBe(8);
    }

    [Theory, Trait("Spec", "RF-052")]
    [InlineData("Teleporte", 1, "Motivo")]
    [InlineData("in", 1, "Motivo")]
    [InlineData("OrderPlaced", 1, "Motivo")]
    [InlineData("In", 0, "Motivo")]
    [InlineData("Out", -1, "Motivo")]
    [InlineData("Adjustment", -1, "Motivo")]
    [InlineData("In", 1, "   ")]
    public async Task InvalidMovement_Returns400(string type, int quantity, string reason)
    {
        using var admin = await AdminAsync();
        var variant = await VariantAsync(admin, await DraftWithVariantAsync(admin));

        (await MoveAsync(admin, variant, type, quantity, reason)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact, Trait("Spec", "RF-052")]
    public async Task UnknownVariant_Returns404()
    {
        using var admin = await AdminAsync();
        var unknown = Guid.CreateVersion7();

        (await MoveAsync(admin, unknown, "In", 1)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await admin.GetAsync($"/api/v1/admin/stock/{unknown}/movements")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact, Trait("Spec", "RF-052")]
    public async Task Store_OnlyRevalidates_WhenAvailabilityChanges()
    {
        using var admin = await AdminAsync();
        var variant = await VariantAsync(admin, await DraftWithVariantAsync(admin));
        var before = await CatalogChangesAsync();

        await MoveAsync(admin, variant, "In", 3);
        (await CatalogChangesAsync()).ShouldBe(before + 1);

        await MoveAsync(admin, variant, "In", 2);
        await MoveAsync(admin, variant, "Out", 4, "Avaria");
        (await CatalogChangesAsync()).ShouldBe(before + 1);

        await MoveAsync(admin, variant, "Out", 1, "Avaria");
        (await CatalogChangesAsync()).ShouldBe(before + 2);
    }

    [Fact, Trait("Spec", "RF-052")]
    public async Task EntryOnProductInStore_MakesItAvailableInTheCatalog()
    {
        using var admin = await AdminAsync();
        var product = await ProductInStoreAsync(admin, "Anel Liso");
        var variant = await VariantAsync(admin, product);

        await MoveAsync(admin, variant, "In", 1);

        var details = await Client.GetFromJsonAsync<JsonElement>("/api/v1/catalog/products/anel-liso", CancellationToken);
        details.GetProperty("variants")[0].GetProperty("available").GetBoolean().ShouldBeTrue();
    }

    [Fact, Trait("Spec", "RF-059")]
    public async Task Scan_AddsOnePerRead_WithTheDefaultReason()
    {
        using var admin = await AdminAsync();
        var variant = await VariantWithBarcodeAsync(admin);

        var first = await admin.PostAsync("/api/v1/admin/stock/scan", new { barcode = ValidBarcode });
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await first.Content.ReadFromJsonAsync<JsonElement>(CancellationToken);
        body.GetProperty("variantId").GetGuid().ShouldBe(variant);
        body.GetProperty("productName").GetString().ShouldBe("Anel Liso");
        body.GetProperty("quantityOnHand").GetInt32().ShouldBe(1);

        (await BalanceAsync(await admin.PostAsync("/api/v1/admin/stock/scan", new { barcode = ValidBarcode, quantity = 4 }))).ShouldBe(5);

        (await Factory.QuerySingleAsync<string>("SELECT reason FROM inventory.stock_movements WHERE variant_id = @variant LIMIT 1", new { variant }))
            .ShouldBe("Entrada por leitura");
    }

    [Fact, Trait("Spec", "RF-059-CA1")]
    public async Task Scan_WithoutOwner_Returns404_ForThePanelToOfferLinking()
    {
        using var admin = await AdminAsync();

        var response = await admin.PostAsync("/api/v1/admin/stock/scan", new { barcode = ValidBarcode });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync(CancellationToken)).ShouldContain("Catalog.BarcodeNotFound");
    }

    [Fact, Trait("Spec", "RF-059-CA2")]
    public async Task Scan_WithWrongCheckDigit_Returns400_AndChangesNothing()
    {
        using var admin = await AdminAsync();
        var variant = await VariantWithBarcodeAsync(admin);

        (await admin.PostAsync("/api/v1/admin/stock/scan", new { barcode = "7891234567890" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await Factory.QuerySingleAsync<int>("SELECT quantity_on_hand FROM inventory.stock_items WHERE variant_id = @variant", new { variant })).ShouldBe(0);
    }

    [Fact, Trait("Spec", "RF-052")]
    public async Task List_SearchesAndFiltersLowStock()
    {
        using var admin = await AdminAsync();
        var ring = await VariantAsync(admin, await ProductInStoreAsync(admin, "Anel Coração"));
        var bag = await VariantAsync(admin, await ProductInStoreAsync(admin, "Bolsa Shopper"));
        var hidden = await VariantAsync(admin, await DraftWithVariantAsync(admin, "Colar Rascunho"));
        await MoveAsync(admin, bag, "In", 50);

        var search = await ListAsync(admin, "search=coracao");
        search.GetProperty("totalItems").GetInt32().ShouldBe(1);
        search.GetProperty("items")[0].GetProperty("variantId").GetGuid().ShouldBe(ring);

        var low = (await ListAsync(admin, "lowStock=true")).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("variantId").GetGuid()).ToList();
        low.ShouldContain(ring);
        low.ShouldNotContain(bag);
        low.ShouldNotContain(hidden);

        var all = await ListAsync(admin, "");
        all.GetProperty("totalItems").GetInt32().ShouldBe(3);
    }

    [Fact, Trait("Spec", "RF-052")]
    public async Task StockRoutes_AreOnlyForAdmins()
    {
        var variant = Guid.CreateVersion7();
        (await Client.GetAsync("/api/v1/admin/stock", CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Client.PostAsJsonAsync($"/api/v1/admin/stock/{variant}/movements", new { type = "In", quantity = 1, reason = "x" }, CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var customer = await AuthClient.RegisteredAsync(Factory, "cliente@loja.teste");

        (await customer.GetAsync("/api/v1/admin/stock")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await customer.PostAsync("/api/v1/admin/stock/scan", new { barcode = ValidBarcode })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private static async Task<Guid> DraftWithVariantAsync(AuthClient admin, string name = "Anel Rascunho")
    {
        var product = await DraftProductAsync(admin, name);
        await GenerateAsync(admin, product, [], []);
        return product;
    }

    private static async Task<JsonElement> ListAsync(AuthClient admin, string query) =>
        await (await admin.GetAsync($"/api/v1/admin/stock?{query}")).Content.ReadFromJsonAsync<JsonElement>(CancellationToken);
}
