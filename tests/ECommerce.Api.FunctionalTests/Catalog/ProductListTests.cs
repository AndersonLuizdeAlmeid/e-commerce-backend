using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ECommerce.Api.FunctionalTests.Infrastructure;

namespace ECommerce.Api.FunctionalTests.Catalog;

public sealed class ProductListTests(ApiFactory factory) : FunctionalTest(factory)
{
    [Fact, Trait("Spec", "RF-020-CA1")]
    public async Task ListProducts_ReturnsOnlyActiveProductsWithActiveVariantInActiveCategory()
    {
        var category = await Catalog.CategoryAsync("Camisetas", "camisetas");
        var inactiveCategory = await Catalog.CategoryAsync("Coleção Antiga", "colecao-antiga", isActive: false);

        await Catalog.SimpleProductAsync(category, "Camiseta Visível", "camiseta-visivel");

        var inactiveProduct = await Catalog.ProductAsync(category, "Camiseta Inativa", "camiseta-inativa", isActive: false);
        await Catalog.VariantAsync(inactiveProduct, "CAM-INATIVA");

        var withoutActiveVariant = await Catalog.ProductAsync(category, "Camiseta Sem Variação", "camiseta-sem-variacao");
        await Catalog.VariantAsync(withoutActiveVariant, "CAM-SEM-VAR", isActive: false);

        await Catalog.SimpleProductAsync(inactiveCategory, "Camiseta Antiga", "camiseta-antiga");

        var page = await GetPageAsync("/api/v1/catalog/products");

        page.Slugs.ShouldBe(["camiseta-visivel"]);
    }

    [Fact, Trait("Spec", "RF-020-CA2")]
    public async Task ListProducts_ReturnsPaginationMetadata()
    {
        var category = await Catalog.CategoryAsync("Camisetas", "camisetas");
        await Catalog.SimpleProductAsync(category, "Camiseta A", "camiseta-a");
        await Catalog.SimpleProductAsync(category, "Camiseta B", "camiseta-b");
        await Catalog.SimpleProductAsync(category, "Camiseta C", "camiseta-c");

        var page = await GetPageAsync("/api/v1/catalog/products?page=2&pageSize=2&sort=name:asc");

        page.Slugs.ShouldBe(["camiseta-c"]);
        page.Root.GetProperty("page").GetInt32().ShouldBe(2);
        page.Root.GetProperty("pageSize").GetInt32().ShouldBe(2);
        page.Root.GetProperty("totalItems").GetInt32().ShouldBe(3);
        page.Root.GetProperty("totalPages").GetInt32().ShouldBe(2);
    }

    [Fact, Trait("Spec", "RF-020-CA2")]
    public async Task ListProducts_WhenPageIsBeyondTheEnd_ReturnsEmptyItemsWithRealTotal()
    {
        var category = await Catalog.CategoryAsync("Camisetas", "camisetas");
        await Catalog.SimpleProductAsync(category, "Camiseta A", "camiseta-a");

        var page = await GetPageAsync("/api/v1/catalog/products?page=5&pageSize=10");

        page.Slugs.ShouldBeEmpty();
        page.Root.GetProperty("totalItems").GetInt32().ShouldBe(1);
    }

    [Theory, Trait("Spec", "RF-020-CA3")]
    [InlineData("CALCA")]
    [InlineData("calça")]
    [InlineData("jeans")]
    public async Task ListProducts_SearchIgnoresCaseAndAccents(string search)
    {
        var category = await Catalog.CategoryAsync("Calças", "calcas");
        await Catalog.SimpleProductAsync(category, "Calça Jeans Reta", "calca-jeans-reta");
        await Catalog.SimpleProductAsync(category, "Bermuda Sarja", "bermuda-sarja");

        var page = await GetPageAsync($"/api/v1/catalog/products?search={Uri.EscapeDataString(search)}");

        page.Slugs.ShouldBe(["calca-jeans-reta"]);
    }

    [Fact]
    public async Task ListProducts_SearchTreatsLikeWildcardsAsText()
    {
        var category = await Catalog.CategoryAsync("Acessórios", "acessorios");
        await Catalog.SimpleProductAsync(category, "Boné", "bone");

        var page = await GetPageAsync("/api/v1/catalog/products?search=%25");

        page.Slugs.ShouldBeEmpty();
    }

    [Fact, Trait("Spec", "RF-020-CA4")]
    public async Task ListProducts_ShowsLowestActiveVariantPriceAndAvailableColors()
    {
        var category = await Catalog.CategoryAsync("Camisetas", "camisetas");
        var blue = await Catalog.ColorAsync("Azul", "azul", "#1B3A6B");
        var black = await Catalog.ColorAsync("Preto", "preto", "#151515");
        var red = await Catalog.ColorAsync("Vermelho", "vermelho", "#B3261E");
        var product = await Catalog.ProductAsync(category, "Camiseta Básica", "camiseta-basica", basePrice: 59.90m);
        await Catalog.VariantAsync(product, "CAM-AZUL", colorId: blue);
        await Catalog.VariantAsync(product, "CAM-PRETO", colorId: black, price: 49.90m);
        await Catalog.VariantAsync(product, "CAM-VERMELHO", colorId: red, price: 9.90m, isActive: false);
        await Catalog.ImageAsync(product, "https://img.teste/azul.png", "Camiseta azul", position: 1, colorId: blue);

        var page = await GetPageAsync("/api/v1/catalog/products");

        var item = page.Items.Single();
        item.GetProperty("priceFrom").GetDecimal().ShouldBe(49.90m);
        item.GetProperty("categorySlug").GetString().ShouldBe("camisetas");
        item.GetProperty("colors").EnumerateArray().Select(c => c.GetProperty("slug").GetString()).ShouldBe(["azul", "preto"]);
        item.GetProperty("image").GetProperty("url").GetString().ShouldBe("https://img.teste/azul.png");
    }

    [Fact, Trait("Spec", "RF-020")]
    public async Task ListProducts_FilterByColorAndSize_RequiresBothOnTheSameVariant()
    {
        var category = await Catalog.CategoryAsync("Camisetas", "camisetas");
        var blue = await Catalog.ColorAsync("Azul", "azul");
        var black = await Catalog.ColorAsync("Preto", "preto");
        var small = await Catalog.SizeAsync("P", 20);
        var medium = await Catalog.SizeAsync("M", 30);

        var mixed = await Catalog.ProductAsync(category, "Azul P e Preto M", "azul-p-preto-m");
        await Catalog.VariantAsync(mixed, "MIX-AZUL-P", colorId: blue, sizeId: small);
        await Catalog.VariantAsync(mixed, "MIX-PRETO-M", colorId: black, sizeId: medium);

        var blueMedium = await Catalog.ProductAsync(category, "Azul M", "azul-m");
        await Catalog.VariantAsync(blueMedium, "AZUL-M", colorId: blue, sizeId: medium);

        var page = await GetPageAsync("/api/v1/catalog/products?colors=azul&sizes=M");

        page.Slugs.ShouldBe(["azul-m"]);
    }

    [Fact, Trait("Spec", "RF-020")]
    public async Task ListProducts_FilterByCategoryAndPriceRange()
    {
        var shirts = await Catalog.CategoryAsync("Camisetas", "camisetas");
        var pants = await Catalog.CategoryAsync("Calças", "calcas");
        await Catalog.SimpleProductAsync(shirts, "Camiseta Barata", "camiseta-barata", price: 39.90m);
        await Catalog.SimpleProductAsync(shirts, "Camiseta Média", "camiseta-media", price: 79.90m);
        await Catalog.SimpleProductAsync(shirts, "Camiseta Cara", "camiseta-cara", price: 149.90m);
        await Catalog.SimpleProductAsync(pants, "Calça Média", "calca-media", price: 79.90m);

        var page = await GetPageAsync("/api/v1/catalog/products?category=camisetas&minPrice=50&maxPrice=100");

        page.Slugs.ShouldBe(["camiseta-media"]);
    }

    [Theory, Trait("Spec", "RF-020")]
    [InlineData("price:asc", new[] { "barata", "media", "cara" })]
    [InlineData("price:desc", new[] { "cara", "media", "barata" })]
    [InlineData("name:asc", new[] { "barata", "cara", "media" })]
    [InlineData("createdAt:desc", new[] { "media", "cara", "barata" })]
    public async Task ListProducts_SortsByWhitelistedField(string sort, string[] expected)
    {
        var category = await Catalog.CategoryAsync("Camisetas", "camisetas");
        var now = DateTime.UtcNow;
        await Catalog.SimpleProductAsync(category, "Barata", "barata", price: 10m, createdAt: now.AddDays(-3));
        await Catalog.SimpleProductAsync(category, "Cara", "cara", price: 30m, createdAt: now.AddDays(-2));
        await Catalog.SimpleProductAsync(category, "Média", "media", price: 20m, createdAt: now.AddDays(-1));

        var page = await GetPageAsync($"/api/v1/catalog/products?sort={Uri.EscapeDataString(sort)}");

        page.Slugs.ShouldBe(expected);
    }

    [Theory, Trait("Spec", "RF-020")]
    [InlineData("pageSize=101", "pageSize")]
    [InlineData("page=0", "page")]
    [InlineData("sort=stock:desc", "sort")]
    [InlineData("minPrice=100&maxPrice=10", "maxPrice")]
    public async Task ListProducts_WithInvalidQuery_ReturnsValidationProblemPerField(string queryString, string field)
    {
        var response = await Client.GetAsync(new Uri($"/api/v1/catalog/products?{queryString}", UriKind.Relative), CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken);
        problem.GetProperty("code").GetString().ShouldBe("Validation.Failed");
        problem.GetProperty("errors").TryGetProperty(field, out _).ShouldBeTrue();
    }

    private async Task<ProductPage> GetPageAsync(string url)
    {
        var root = await Client.GetFromJsonAsync<JsonElement>(url, CancellationToken);
        return new ProductPage(root);
    }

    private sealed record ProductPage(JsonElement Root)
    {
        public IReadOnlyList<JsonElement> Items => Root.GetProperty("items").EnumerateArray().ToList();
        public IReadOnlyList<string?> Slugs => Items.Select(item => item.GetProperty("slug").GetString()).ToList();
    }
}
