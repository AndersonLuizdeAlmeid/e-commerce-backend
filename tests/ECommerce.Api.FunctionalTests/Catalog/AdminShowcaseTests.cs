using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ECommerce.Api.FunctionalTests.Infrastructure;
using ECommerce.Application.Abstractions.Caching;
using ECommerce.Application.Catalog;
using Microsoft.Extensions.DependencyInjection;

namespace ECommerce.Api.FunctionalTests.Catalog;

public sealed class AdminShowcaseTests(ApiFactory factory) : AdminCatalogTest(factory)
{
    private static string Day(int fromToday) =>
        DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-3)).AddDays(fromToday).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static object Occasion(int startsIn, int endsIn, Guid[] productIds, string name = "Dia dos Pais", bool isActive = true) =>
        new { name, text = "Presentes para ele.", startsOn = Day(startsIn), endsOn = Day(endsIn), isActive, productIds };

    private Task GiveStockAsync(string productSlug) =>
        Factory.ExecuteSqlAsync(
            $"""
            INSERT INTO inventory.stock_items (variant_id, quantity_on_hand, updated_at)
            SELECT v.id, 5, now()
              FROM catalog.product_variants v
              JOIN catalog.products p ON p.id = v.product_id
             WHERE p.slug = '{productSlug}'
            ON CONFLICT (variant_id) DO UPDATE SET quantity_on_hand = 5
            """);

    private async Task<JsonElement> ShowcaseAsync()
    {
        await Factory.Services.GetRequiredService<ICacheService>().RemoveByTagAsync(CatalogCache.ShowcaseTag, CancellationToken);
        return await Client.GetFromJsonAsync<JsonElement>("/api/v1/catalog/showcase", CancellationToken);
    }

    private static string? FeaturedSlug(JsonElement showcase) =>
        showcase.GetProperty("featured").GetProperty("product").GetProperty("slug").GetString();

    [Fact, Trait("Spec", "RF-025")]
    public async Task Featured_WithStock_IsReturnedAsChosen()
    {
        using var admin = await AdminAsync();
        var ring = await ProductInStoreAsync(admin, "Anel Liso");
        await ProductInStoreAsync(admin, "Bolsa Shopper");
        await GiveStockAsync("anel-liso");
        await GiveStockAsync("bolsa-shopper");

        var set = await admin.PutAsync("/api/v1/admin/showcase/featured", new { productId = ring });

        set.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var showcase = await ShowcaseAsync();
        FeaturedSlug(showcase).ShouldBe("anel-liso");
        showcase.GetProperty("featured").GetProperty("isFallback").GetBoolean().ShouldBeFalse();
    }

    [Fact, Trait("Spec", "RF-025-CA1")]
    public async Task Featured_WithoutStock_FallsBackToNewestWithStock()
    {
        using var admin = await AdminAsync();
        await ProductInStoreAsync(admin, "Colar Antigo");
        await ProductInStoreAsync(admin, "Bolsa Shopper");
        var ring = await ProductInStoreAsync(admin, "Anel Esgotado");
        await GiveStockAsync("colar-antigo");
        await GiveStockAsync("bolsa-shopper");
        await admin.PutAsync("/api/v1/admin/showcase/featured", new { productId = ring });

        var showcase = await ShowcaseAsync();

        FeaturedSlug(showcase).ShouldBe("bolsa-shopper");
        showcase.GetProperty("featured").GetProperty("isFallback").GetBoolean().ShouldBeTrue();
    }

    [Fact, Trait("Spec", "RF-025-CA1")]
    public async Task Featured_Inactive_FallsBack_AndPanelIsWarned()
    {
        using var admin = await AdminAsync();
        var ring = await ProductInStoreAsync(admin, "Anel Liso");
        await ProductInStoreAsync(admin, "Bolsa Shopper");
        await GiveStockAsync("anel-liso");
        await GiveStockAsync("bolsa-shopper");
        await admin.PutAsync("/api/v1/admin/showcase/featured", new { productId = ring });
        (await admin.PatchAsync($"/api/v1/admin/products/{ring}/status", new { isActive = false })).EnsureSuccessStatusCode();

        FeaturedSlug(await ShowcaseAsync()).ShouldBe("bolsa-shopper");

        var panel = await admin.GetAsync("/api/v1/admin/showcase/featured");
        (await panel.Content.ReadFromJsonAsync<JsonElement>(CancellationToken)).GetProperty("isAvailable").GetBoolean().ShouldBeFalse();
    }

    [Fact, Trait("Spec", "RF-025-CA1")]
    public async Task Showcase_EmptyStore_HasNothingToFeature()
    {
        var showcase = await ShowcaseAsync();

        showcase.GetProperty("featured").ValueKind.ShouldBe(JsonValueKind.Null);
        showcase.GetProperty("occasion").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact, Trait("Spec", "RF-025")]
    public async Task Featured_WithColorThatProductDoesNotHave_IsRefused()
    {
        using var admin = await AdminAsync();
        var ring = await ProductInStoreAsync(admin, "Anel Liso");
        var color = await CreateColorAsync(admin, "Ródio");

        var response = await admin.PutAsync("/api/v1/admin/showcase/featured", new { productId = ring, colorId = color });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact, Trait("Spec", "RF-025")]
    public async Task Featured_Cleared_PanelHasNothingChosen()
    {
        using var admin = await AdminAsync();
        var ring = await ProductInStoreAsync(admin, "Anel Liso");
        await admin.PutAsync("/api/v1/admin/showcase/featured", new { productId = ring });

        await admin.PutAsync("/api/v1/admin/showcase/featured", new { productId = (Guid?)null });

        (await admin.GetAsync("/api/v1/admin/showcase/featured")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact, Trait("Spec", "RF-025")]
    public async Task Occasion_InPeriod_ReturnsProductsInChosenOrder()
    {
        using var admin = await AdminAsync();
        var ring = await ProductInStoreAsync(admin, "Anel Liso");
        var bag = await ProductInStoreAsync(admin, "Bolsa Shopper");

        var created = await admin.PostAsync("/api/v1/admin/occasions", Occasion(0, 0, [bag, ring]));

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var occasion = (await ShowcaseAsync()).GetProperty("occasion");
        occasion.GetProperty("name").GetString().ShouldBe("Dia dos Pais");
        occasion.GetProperty("products").EnumerateArray().Select(p => p.GetProperty("slug").GetString()).ShouldBe(["bolsa-shopper", "anel-liso"]);
    }

    [Theory, Trait("Spec", "RF-025-CA3")]
    [InlineData(-5, -2)]
    [InlineData(2, 5)]
    public async Task Occasion_OutOfPeriod_IsNotReturned(int startsIn, int endsIn)
    {
        using var admin = await AdminAsync();
        var ring = await ProductInStoreAsync(admin, "Anel Liso");
        await admin.PostAsync("/api/v1/admin/occasions", Occasion(startsIn, endsIn, [ring]));

        (await ShowcaseAsync()).GetProperty("occasion").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact, Trait("Spec", "RF-025-CA2")]
    public async Task ActiveOccasions_WithOverlappingPeriods_ReturnConflict()
    {
        using var admin = await AdminAsync();
        await admin.PostAsync("/api/v1/admin/occasions", Occasion(0, 10, []));

        var overlap = await admin.PostAsync("/api/v1/admin/occasions", Occasion(10, 20, [], "Natal"));
        var after = await admin.PostAsync("/api/v1/admin/occasions", Occasion(11, 20, [], "Natal"));

        overlap.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        after.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact, Trait("Spec", "RF-025-CA2")]
    public async Task InactiveOccasion_DoesNotBlockThePeriod()
    {
        using var admin = await AdminAsync();
        await admin.PostAsync("/api/v1/admin/occasions", Occasion(0, 10, [], isActive: false));

        var response = await admin.PostAsync("/api/v1/admin/occasions", Occasion(0, 10, [], "Natal"));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact, Trait("Spec", "RF-025-CA2")]
    public async Task EditingOccasion_DoesNotConflictWithItself()
    {
        using var admin = await AdminAsync();
        var created = await admin.PostAsync("/api/v1/admin/occasions", Occasion(0, 10, []));
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(CancellationToken)).GetProperty("id").GetGuid();

        var update = await admin.PutAsync($"/api/v1/admin/occasions/{id}", new { version = 0, occasion = Occasion(0, 12, []) });

        update.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact, Trait("Spec", "RF-025-CA4")]
    public async Task Occasion_WithoutActiveProducts_IsNotReturned()
    {
        using var admin = await AdminAsync();
        var ring = await ProductInStoreAsync(admin, "Anel Liso");
        await admin.PostAsync("/api/v1/admin/occasions", Occasion(0, 5, [ring]));
        (await admin.PatchAsync($"/api/v1/admin/products/{ring}/status", new { isActive = false })).EnsureSuccessStatusCode();

        (await ShowcaseAsync()).GetProperty("occasion").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact, Trait("Spec", "RF-025")]
    public async Task Occasion_WithUnknownProduct_Returns404()
    {
        using var admin = await AdminAsync();

        var response = await admin.PostAsync("/api/v1/admin/occasions", Occasion(0, 5, [Guid.CreateVersion7()]));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact, Trait("Spec", "RF-025")]
    public async Task AdminList_BringsProductsInOrder()
    {
        using var admin = await AdminAsync();
        var ring = await ProductInStoreAsync(admin, "Anel Liso");
        var bag = await ProductInStoreAsync(admin, "Bolsa Shopper");
        await admin.PostAsync("/api/v1/admin/occasions", Occasion(0, 5, [bag, ring]));

        var list = await (await admin.GetAsync("/api/v1/admin/occasions")).Content.ReadFromJsonAsync<JsonElement>(CancellationToken);

        var occasion = list.EnumerateArray().Single();
        occasion.GetProperty("startsOn").GetString().ShouldBe(Day(0));
        occasion.GetProperty("products").EnumerateArray().Select(p => p.GetProperty("id").GetGuid()).ShouldBe([bag, ring]);
    }
}
