using System.Net.Http.Json;
using System.Text.Json;
using ECommerce.Api.FunctionalTests.Infrastructure;

namespace ECommerce.Api.FunctionalTests.Catalog;

public sealed class CatalogFiltersAndSitemapTests(ApiFactory factory) : FunctionalTest(factory)
{
    [Fact, Trait("Spec", "RF-023")]
    public async Task GetFilters_ReturnsColorsSizesAndPriceRangeOfVisibleVariantsInCategory()
    {
        var shirts = await Catalog.CategoryAsync("Camisetas", "camisetas");
        var pants = await Catalog.CategoryAsync("Calças", "calcas");
        var blue = await Catalog.ColorAsync("Azul", "azul");
        var red = await Catalog.ColorAsync("Vermelho", "vermelho");
        var green = await Catalog.ColorAsync("Verde", "verde");
        var large = await Catalog.SizeAsync("G", 40);
        var small = await Catalog.SizeAsync("P", 20);
        var size40 = await Catalog.SizeAsync("40", 140);

        var shirt = await Catalog.ProductAsync(shirts, "Camiseta", "camiseta", basePrice: 50m);
        await Catalog.VariantAsync(shirt, "CAM-AZUL-G", blue, large);
        await Catalog.VariantAsync(shirt, "CAM-AZUL-P", blue, small, price: 45m);
        await Catalog.VariantAsync(shirt, "CAM-VERDE-P", green, small, isActive: false);

        var pant = await Catalog.ProductAsync(pants, "Calça", "calca", basePrice: 150m);
        await Catalog.VariantAsync(pant, "CAL-VERMELHO-40", red, size40);

        var filters = await Client.GetFromJsonAsync<JsonElement>("/api/v1/catalog/filters?category=camisetas", CancellationToken);

        filters.GetProperty("colors").EnumerateArray().Select(c => c.GetProperty("slug").GetString()).ShouldBe(["azul"]);
        filters.GetProperty("sizes").EnumerateArray().Select(s => s.GetProperty("name").GetString()).ShouldBe(["P", "G"]);
        filters.GetProperty("minPrice").GetDecimal().ShouldBe(45m);
        filters.GetProperty("maxPrice").GetDecimal().ShouldBe(50m);
    }

    [Fact, Trait("Spec", "RF-023")]
    public async Task GetFilters_WithoutProducts_ReturnsEmptyListsAndNullPrices()
    {
        var filters = await Client.GetFromJsonAsync<JsonElement>("/api/v1/catalog/filters", CancellationToken);

        filters.GetProperty("colors").GetArrayLength().ShouldBe(0);
        filters.GetProperty("sizes").GetArrayLength().ShouldBe(0);
        filters.GetProperty("minPrice").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact, Trait("Spec", "SEO-06")]
    public async Task GetSitemap_ListsOnlyVisibleCategoriesAndProducts()
    {
        var shirts = await Catalog.CategoryAsync("Camisetas", "camisetas");
        var old = await Catalog.CategoryAsync("Coleção Antiga", "colecao-antiga", isActive: false);
        await Catalog.SimpleProductAsync(shirts, "Camiseta", "camiseta");
        await Catalog.ProductAsync(shirts, "Camiseta Inativa", "camiseta-inativa", isActive: false);
        await Catalog.SimpleProductAsync(old, "Camiseta Antiga", "camiseta-antiga");

        var sitemap = await Client.GetFromJsonAsync<JsonElement>("/api/v1/catalog/sitemap", CancellationToken);

        sitemap.GetProperty("categories").EnumerateArray().Select(c => c.GetProperty("slug").GetString()).ShouldBe(["camisetas"]);
        sitemap.GetProperty("products").EnumerateArray().Select(p => p.GetProperty("slug").GetString()).ShouldBe(["camiseta"]);
        sitemap.GetProperty("products")[0].GetProperty("updatedAt").GetDateTime().Kind.ShouldBe(DateTimeKind.Utc);
    }
}
