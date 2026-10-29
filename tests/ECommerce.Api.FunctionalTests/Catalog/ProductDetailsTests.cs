using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ECommerce.Api.FunctionalTests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ECommerce.Api.FunctionalTests.Catalog;

public sealed class ProductDetailsTests(ApiFactory factory) : FunctionalTest(factory)
{
    [Fact, Trait("Spec", "RF-021")]
    public async Task GetProduct_ReturnsCategoryGridVariantsAndImages()
    {
        var (productId, colors, sizes) = await SeedShirtAsync();
        await Catalog.ImageAsync(productId, "https://img.teste/azul-1.png", "Camiseta azul, frente", 1, colors.Blue);
        await Catalog.ImageAsync(productId, "https://img.teste/preto-1.png", "Camiseta preta, frente", 1, colors.Black);

        var product = await Client.GetFromJsonAsync<JsonElement>("/api/v1/catalog/products/camiseta-basica", CancellationToken);

        product.GetProperty("name").GetString().ShouldBe("Camiseta Básica");
        product.GetProperty("category").GetProperty("slug").GetString().ShouldBe("camisetas");
        product.GetProperty("priceFrom").GetDecimal().ShouldBe(59.90m);
        product.GetProperty("seo").GetProperty("title").GetString().ShouldBe("Camiseta Básica");
        product.GetProperty("colors").EnumerateArray().Select(c => c.GetProperty("name").GetString()).ShouldBe(["Azul", "Preto"]);
        product.GetProperty("sizes").EnumerateArray().Select(s => s.GetProperty("name").GetString()).ShouldBe(["P", "M"]);
        product.GetProperty("variants").GetArrayLength().ShouldBe(3);
        product.GetProperty("images").EnumerateArray().Select(i => i.GetProperty("colorId").GetGuid()).ShouldBe([colors.Blue, colors.Black]);

        var blueMedium = Variant(product, "CAM-AZUL-M");
        blueMedium.GetProperty("colorId").GetGuid().ShouldBe(colors.Blue);
        blueMedium.GetProperty("sizeId").GetGuid().ShouldBe(sizes.Medium);
        blueMedium.GetProperty("price").GetDecimal().ShouldBe(64.90m);
        blueMedium.GetProperty("gtin").GetString().ShouldBe("7891234567895");
    }

    [Fact, Trait("Spec", "RF-021-CA1")]
    public async Task GetProduct_DoesNotExposeStockQuantity()
    {
        await SeedShirtAsync();

        var json = await Client.GetStringAsync(new Uri("/api/v1/catalog/products/camiseta-basica", UriKind.Relative), CancellationToken);

        json.ShouldNotContain("quantity", Case.Insensitive);
        json.ShouldNotContain("stock", Case.Insensitive);
    }

    [Fact, Trait("Spec", "RF-021-CA2")]
    public async Task GetProduct_MarksOutOfStockVariantAsUnavailableAndOmitsInactiveOnes()
    {
        await SeedShirtAsync();

        var product = await Client.GetFromJsonAsync<JsonElement>("/api/v1/catalog/products/camiseta-basica", CancellationToken);

        Variant(product, "CAM-AZUL-P").GetProperty("available").GetBoolean().ShouldBeTrue();
        Variant(product, "CAM-PRETO-P").GetProperty("available").GetBoolean().ShouldBeFalse();
        product.GetProperty("variants").EnumerateArray()
            .Select(v => v.GetProperty("sku").GetString())
            .ShouldNotContain("CAM-PRETO-M");
    }

    [Fact, Trait("Spec", "SEO-02")]
    public async Task GetProduct_WithOldSlug_RedirectsPermanentlyToCurrentSlug()
    {
        var (productId, _, _) = await SeedShirtAsync();
        await Catalog.SlugHistoryAsync("camiseta-basica-algodao", productId);
        using var client = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync(new Uri("/api/v1/catalog/products/camiseta-basica-algodao", UriKind.Relative), CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.MovedPermanently);
        response.Headers.Location?.OriginalString.ShouldBe("/api/v1/catalog/products/camiseta-basica");
    }

    [Fact, Trait("Spec", "SEO-02")]
    public async Task GetProduct_WhenInactive_ReturnsGoneWithCategorySlug()
    {
        var category = await Catalog.CategoryAsync("Camisetas", "camisetas");
        var product = await Catalog.ProductAsync(category, "Camiseta Fora de Linha", "camiseta-fora-de-linha", isActive: false);
        await Catalog.VariantAsync(product, "CAM-FORA");

        var response = await Client.GetAsync(new Uri("/api/v1/catalog/products/camiseta-fora-de-linha", UriKind.Relative), CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Gone);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken);
        problem.GetProperty("code").GetString().ShouldBe("Catalog.ProductUnavailable");
        problem.GetProperty("categorySlug").GetString().ShouldBe("camisetas");
    }

    [Fact]
    public async Task GetProduct_WhenUnknown_ReturnsNotFoundProblem()
    {
        var response = await Client.GetAsync(new Uri("/api/v1/catalog/products/nao-existe", UriKind.Relative), CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken);
        problem.GetProperty("code").GetString().ShouldBe("Catalog.ProductNotFound");
    }

    [Fact, Trait("Spec", "12-CA2")]
    public async Task GetProduct_AvailabilityIsReadFreshEvenWhenProductIsCached()
    {
        await SeedShirtAsync();
        var first = await Client.GetFromJsonAsync<JsonElement>("/api/v1/catalog/products/camiseta-basica", CancellationToken);
        Variant(first, "CAM-AZUL-P").GetProperty("available").GetBoolean().ShouldBeTrue();

        await Factory.ExecuteSqlAsync(
            """
            UPDATE inventory.stock_items s SET quantity_on_hand = 0
              FROM catalog.product_variants v
             WHERE v.id = s.variant_id AND v.sku = 'CAM-AZUL-P'
            """);

        var second = await Client.GetFromJsonAsync<JsonElement>("/api/v1/catalog/products/camiseta-basica", CancellationToken);
        Variant(second, "CAM-AZUL-P").GetProperty("available").GetBoolean().ShouldBeFalse();
    }

    private static JsonElement Variant(JsonElement product, string sku) =>
        product.GetProperty("variants").EnumerateArray().Single(v => v.GetProperty("sku").GetString() == sku);

    /// <summary>Camiseta 2 cores × 2 tamanhos: Azul P/M, Preto P sem estoque, Preto M inativa.</summary>
    private async Task<(Guid ProductId, (Guid Blue, Guid Black) Colors, (Guid Small, Guid Medium) Sizes)> SeedShirtAsync()
    {
        var category = await Catalog.CategoryAsync("Camisetas", "camisetas");
        var blue = await Catalog.ColorAsync("Azul", "azul", "#1B3A6B");
        var black = await Catalog.ColorAsync("Preto", "preto", "#151515");
        var small = await Catalog.SizeAsync("P", 20);
        var medium = await Catalog.SizeAsync("M", 30);

        var product = await Catalog.ProductAsync(category, "Camiseta Básica", "camiseta-basica", basePrice: 59.90m);
        await Catalog.VariantAsync(product, "CAM-AZUL-P", blue, small);
        await Catalog.VariantAsync(product, "CAM-AZUL-M", blue, medium, price: 64.90m, barcode: "7891234567895");
        await Catalog.VariantAsync(product, "CAM-PRETO-P", black, small, stock: 0);
        await Catalog.VariantAsync(product, "CAM-PRETO-M", black, medium, isActive: false);

        return (product, (blue, black), (small, medium));
    }
}
