using System.Net;
using System.Xml.Linq;
using ECommerce.Api.FunctionalTests.Infrastructure;

namespace ECommerce.Api.FunctionalTests.Catalog;

/// <summary>Fase 6.6: feed do Google Merchant Center (SEO-14, entrega 6 §4.2).</summary>
public sealed class MerchantFeedTests(ApiFactory factory) : AdminCatalogTest(factory)
{
    private static readonly XNamespace G = "http://base.google.com/ns/1.0";
    private const string Barcode = "7891234567895";

    private async Task<XElement[]> FeedAsync()
    {
        var response = await Client.GetAsync("/api/v1/feeds/google-merchant.xml", CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/xml");
        response.Headers.CacheControl?.Public.ShouldBeTrue();
        response.Headers.CacheControl?.MaxAge.ShouldBe(TimeSpan.FromHours(1));
        var document = XDocument.Parse(await response.Content.ReadAsStringAsync(CancellationToken));
        document.Root!.Name.LocalName.ShouldBe("rss");
        return document.Descendants("item").ToArray();
    }

    private static string? Field(XElement item, string name) => item.Element(G + name)?.Value;

    [Fact, Trait("Spec", "SEO-14")]
    public async Task Feed_HasOneItemPerVariant_GroupedByProduct_WithGtinPriceAndAvailability()
    {
        using var admin = await AdminAsync();
        var category = await CreateCategoryAsync(admin, "Anéis");
        var dourado = await CreateColorAsync(admin, "Dourado");
        var prateado = await CreateColorAsync(admin, "Prateado");
        var aro = await CreateSizeAsync(admin, "16");
        var product = await CreateProductAsync(admin, category, "Anel Solitário & Brilho");
        (await GenerateAsync(admin, product, [dourado, prateado], [aro])).ShouldBe(2);
        (await UploadAsync(admin, product, TestImages.Create(ImageMagick.MagickFormat.Jpeg, 100, 100), "Anel")).EnsureSuccessStatusCode();
        (await admin.PatchAsync($"/api/v1/admin/products/{product}/status", new { isActive = true })).EnsureSuccessStatusCode();
        var variants = (await GetProductAsync(admin, product)).GetProperty("variants").EnumerateArray().ToList();
        var golden = variants.Single(v => v.GetProperty("colorId").GetGuid() == dourado);
        var silver = variants.Single(v => v.GetProperty("colorId").GetGuid() == prateado);
        (await admin.PutAsync($"/api/v1/admin/products/{product}/variants/{golden.GetProperty("id").GetGuid()}",
            new { sku = "ANEL-DOU-16", barcode = Barcode, price = 99.90m })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await admin.PutAsync($"/api/v1/admin/products/{product}/variants/{silver.GetProperty("id").GetGuid()}",
            new { sku = "ANEL-PRA-16" })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await admin.PostAsync($"/api/v1/admin/stock/{golden.GetProperty("id").GetGuid()}/movements", new { type = "In", quantity = 2, reason = "Teste" }))
            .EnsureSuccessStatusCode();

        var items = await FeedAsync();

        items.Length.ShouldBe(2);
        items.Select(item => Field(item, "item_group_id")).Distinct().ShouldBe([product.ToString()]);

        var gold = items.Single(item => Field(item, "id") == "ANEL-DOU-16");
        gold.Element("title")!.Value.ShouldBe("Anel Solitário & Brilho - Dourado - 16");
        Field(gold, "gtin").ShouldBe(Barcode);
        Field(gold, "identifier_exists").ShouldBeNull();
        Field(gold, "price").ShouldBe("99.90 BRL");
        Field(gold, "availability").ShouldBe("in_stock");
        Field(gold, "color").ShouldBe("Dourado");
        Field(gold, "size").ShouldBe("16");
        Field(gold, "product_type").ShouldBe("Anéis");
        Field(gold, "condition").ShouldBe("new");
        gold.Element("link")!.Value.ShouldStartWith("https://loja.teste/produto/anel-solitario-brilho?cor=dourado&tamanho=16");
        Field(gold, "image_link").ShouldNotBeNullOrWhiteSpace();

        var silverItem = items.Single(item => Field(item, "id") == "ANEL-PRA-16");
        Field(silverItem, "gtin").ShouldBeNull();
        Field(silverItem, "identifier_exists").ShouldBe("no");
        Field(silverItem, "price").ShouldBe("89.90 BRL");
        Field(silverItem, "availability").ShouldBe("out_of_stock");

        // Variação desativada some na próxima leitura: a escrita do painel apaga o cache do catálogo, sem esperar a hora.
        (await admin.PatchAsync($"/api/v1/admin/products/{product}/variants/{silver.GetProperty("id").GetGuid()}/status", new { isActive = false }))
            .EnsureSuccessStatusCode();
        await WaitUntilAsync(async () => (await FeedAsync()).Select(item => Field(item, "id")).SequenceEqual(["ANEL-DOU-16"]));
    }

    [Fact, Trait("Spec", "SEO-14")]
    public async Task Feed_LeavesOutInactiveProductsVariantsAndPiecesWithoutPhoto()
    {
        using var admin = await AdminAsync();
        var visible = await ProductInStoreAsync(admin, "Colar Visível");
        var hidden = await ProductInStoreAsync(admin, "Colar Fora da Loja");
        (await admin.PatchAsync($"/api/v1/admin/products/{hidden}/status", new { isActive = false })).EnsureSuccessStatusCode();
        await DraftProductAsync(admin, "Colar Rascunho");

        var items = await FeedAsync();

        items.Select(item => Field(item, "item_group_id")).ShouldBe([visible.ToString()]);
    }

    [Fact, Trait("Spec", "SEO-14")]
    public async Task Feed_IsPublic_AndEmptyWithoutProducts()
    {
        var items = await FeedAsync();

        items.ShouldBeEmpty();
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, "o feed não mudou depois da escrita do painel");
            await Task.Delay(200, CancellationToken);
        }
    }
}
