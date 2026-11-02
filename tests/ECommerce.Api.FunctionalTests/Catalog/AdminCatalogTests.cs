using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ECommerce.Api.FunctionalTests.Infrastructure;
using ImageMagick;

namespace ECommerce.Api.FunctionalTests.Catalog;

public sealed class AdminCatalogTests(ApiFactory factory) : AdminCatalogTest(factory)
{
    [Theory, Trait("Spec", "RF-050")]
    [InlineData("/api/v1/admin/categories")]
    [InlineData("/api/v1/admin/colors")]
    [InlineData("/api/v1/admin/sizes")]
    [InlineData("/api/v1/admin/products")]
    [InlineData("/api/v1/admin/variants/by-barcode/7891234567895")]
    public async Task AdminRoutes_RejectAnonymousAndCustomer(string path)
    {
        (await Client.GetAsync(new Uri(path, UriKind.Relative), CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var customer = await AuthClient.RegisteredAsync(Factory, "maria@loja.teste");
        (await customer.GetAsync(path)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact, Trait("Spec", "Q07")]
    public async Task Category_AllowsOneSubcategoryLevelOnly()
    {
        using var admin = await AdminAsync();
        var main = await CreateCategoryAsync(admin, "Anéis");
        var sub = await CreateCategoryAsync(admin, "Anéis masculinos", parentId: main);

        var third = await admin.PostAsync("/api/v1/admin/categories", new { name = "Terceiro nível", parentId = sub });

        third.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact, Trait("Spec", "RF-050")]
    public async Task Category_LabelsInheritFromParent_AndFallBackToDefault()
    {
        using var admin = await AdminAsync();
        var main = await CreateCategoryAsync(admin, "Anéis", colorLabel: "Acabamento", sizeLabel: "Aro");
        await CreateCategoryAsync(admin, "Anéis masculinos", parentId: main, sizeLabel: "Numeração");
        await CreateCategoryAsync(admin, "Bolsas");

        var child = await GetPublicCategoryAsync("aneis-masculinos");
        child.GetProperty("variantLabels").GetProperty("color").GetString().ShouldBe("Acabamento");
        child.GetProperty("variantLabels").GetProperty("size").GetString().ShouldBe("Numeração");

        var bags = await GetPublicCategoryAsync("bolsas");
        bags.GetProperty("variantLabels").GetProperty("color").GetString().ShouldBe("Cor");
        bags.GetProperty("variantLabels").GetProperty("size").GetString().ShouldBe("Tamanho");
    }

    [Fact, Trait("Spec", "RF-050")]
    public async Task Category_WithProducts_CannotBeDeleted()
    {
        using var admin = await AdminAsync();
        var category = await CreateCategoryAsync(admin, "Anéis");
        await CreateProductAsync(admin, category, "Anel Solitário");

        (await admin.DeleteAsync($"/api/v1/admin/categories/{category}")).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact, Trait("Spec", "RN-025")]
    public async Task Category_SlugChange_RedirectsOldSlug()
    {
        using var admin = await AdminAsync();
        var category = await CreateCategoryAsync(admin, "Anéis");

        var update = await admin.PutAsync(
            $"/api/v1/admin/categories/{category}",
            new { version = 0, category = new { name = "Anéis", slug = "aneis-novos" } });
        update.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using var noRedirect = Factory.CreateClient(new() { AllowAutoRedirect = false });
        var moved = await noRedirect.GetAsync(new Uri("/api/v1/catalog/categories/aneis", UriKind.Relative), CancellationToken);
        moved.StatusCode.ShouldBe(HttpStatusCode.MovedPermanently);
        moved.Headers.Location!.ToString().ShouldEndWith("/categories/aneis-novos");
    }

    [Fact, Trait("Spec", "RF-050")]
    public async Task Category_StaleVersion_ReturnsConflict()
    {
        using var admin = await AdminAsync();
        var category = await CreateCategoryAsync(admin, "Anéis");

        var stale = await admin.PutAsync(
            $"/api/v1/admin/categories/{category}", new { version = 7, category = new { name = "Anéis" } });

        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact, Trait("Spec", "RF-057")]
    public async Task Color_InUse_CannotBeDeleted_AndUnusedCan()
    {
        using var admin = await AdminAsync();
        var category = await CreateCategoryAsync(admin, "Anéis");
        var product = await CreateProductAsync(admin, category, "Anel Solitário");
        var used = await CreateColorAsync(admin, "Dourado");
        var unused = await CreateColorAsync(admin, "Ródio");
        await GenerateAsync(admin, product, [used], []);

        (await admin.DeleteAsync($"/api/v1/admin/colors/{used}")).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await admin.DeleteAsync($"/api/v1/admin/colors/{unused}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact, Trait("Spec", "RF-057")]
    public async Task Size_DuplicateName_ReturnsConflict()
    {
        using var admin = await AdminAsync();
        (await admin.PostAsync("/api/v1/admin/sizes", new { name = "Aro 99", sortOrder = 1 })).StatusCode.ShouldBe(HttpStatusCode.Created);

        (await admin.PostAsync("/api/v1/admin/sizes", new { name = "aro 99", sortOrder = 2 })).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact, Trait("Spec", "RF-056")]
    public async Task GenerateVariants_CreatesOnlyMissingCombinations_WithSkuAndZeroStock()
    {
        using var admin = await AdminAsync();
        var category = await CreateCategoryAsync(admin, "Anéis");
        var product = await CreateProductAsync(admin, category, "Anel Solitário");
        var gold = await CreateColorAsync(admin, "Dourado");
        var silver = await CreateColorAsync(admin, "Prateado");
        var size14 = await CreateSizeAsync(admin, "Aro T14");
        var size16 = await CreateSizeAsync(admin, "Aro T16");

        (await GenerateAsync(admin, product, [gold], [size14])).ShouldBe(1);
        (await GenerateAsync(admin, product, [gold, silver], [size14, size16])).ShouldBe(3);

        var details = await GetProductAsync(admin, product);
        var variants = details.GetProperty("variants");
        variants.GetArrayLength().ShouldBe(4);
        variants.EnumerateArray().Select(v => v.GetProperty("sku").GetString()).ShouldContain("ANEL-SOLITARIO-DOURADO-ARO-T14");
        variants.EnumerateArray().ShouldAllBe(v => v.GetProperty("quantityOnHand").GetInt32() == 0);
    }

    [Fact, Trait("Spec", "RN-022")]
    public async Task GenerateVariants_WithoutAxes_CreatesSingleVariant()
    {
        using var admin = await AdminAsync();
        var category = await CreateCategoryAsync(admin, "Bolsas");
        var product = await CreateProductAsync(admin, category, "Bolsa Shopper");

        (await GenerateAsync(admin, product, [], [])).ShouldBe(1);
        (await GenerateAsync(admin, product, [], [])).ShouldBe(0);
    }

    [Fact, Trait("Spec", "RN-023")]
    public async Task Product_CannotBeActivatedWithoutVariant_AndAppearsInStoreAfter()
    {
        using var admin = await AdminAsync();
        var category = await CreateCategoryAsync(admin, "Bolsas");
        var product = await CreateProductAsync(admin, category, "Bolsa Shopper");

        await UploadAsync(admin, product, TestImages.Create(MagickFormat.Jpeg, 100, 100), "Frente");
        (await admin.PatchAsync($"/api/v1/admin/products/{product}/status", new { isActive = true })).StatusCode
            .ShouldBe(HttpStatusCode.UnprocessableEntity);

        await GenerateAsync(admin, product, [], []);
        (await admin.PatchAsync($"/api/v1/admin/products/{product}/status", new { isActive = true })).StatusCode
            .ShouldBe(HttpStatusCode.NoContent);

        var store = await Client.GetFromJsonAsync<JsonElement>("/api/v1/catalog/products/bolsa-shopper", CancellationToken);
        store.GetProperty("name").GetString().ShouldBe("Bolsa Shopper");
    }

    [Fact, Trait("Spec", "RN-025")]
    public async Task Product_SlugChange_RedirectsOldSlug_AndOldSlugStaysReservedForOthers()
    {
        using var admin = await AdminAsync();
        var category = await CreateCategoryAsync(admin, "Bolsas");
        var product = await ProductInStoreAsync(admin, "Bolsa Shopper", category);

        var update = await admin.PutAsync(
            $"/api/v1/admin/products/{product}",
            new { version = (await GetProductAsync(admin, product)).GetProperty("version").GetInt32(), product = ProductBody(category, "Bolsa Shopper", "bolsa-shopper-couro") });
        update.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using var noRedirect = Factory.CreateClient(new() { AllowAutoRedirect = false });
        (await noRedirect.GetAsync(new Uri("/api/v1/catalog/products/bolsa-shopper", UriKind.Relative), CancellationToken)).StatusCode
            .ShouldBe(HttpStatusCode.MovedPermanently);

        var taken = await admin.PostAsync("/api/v1/admin/products", ProductBody(category, "Outra bolsa", "bolsa-shopper"));
        taken.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact, Trait("Spec", "RF-059")]
    public async Task Barcode_InvalidUnknownAndFound()
    {
        const string Barcode = "7891234567895";
        using var admin = await AdminAsync();
        var category = await CreateCategoryAsync(admin, "Bolsas");
        var product = await CreateProductAsync(admin, category, "Bolsa Shopper");
        await GenerateAsync(admin, product, [], []);
        var variant = (await GetProductAsync(admin, product)).GetProperty("variants")[0].GetProperty("id").GetGuid();

        (await admin.GetAsync("/api/v1/admin/variants/by-barcode/7891234567890")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.GetAsync($"/api/v1/admin/variants/by-barcode/{Barcode}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var link = await admin.PutAsync(
            $"/api/v1/admin/products/{product}/variants/{variant}", new { sku = "BOLSA-1", barcode = Barcode });
        link.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var found = await admin.GetAsync($"/api/v1/admin/variants/by-barcode/{Barcode}");
        found.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await found.Content.ReadFromJsonAsync<JsonElement>(CancellationToken)).GetProperty("productName").GetString().ShouldBe("Bolsa Shopper");
    }

    [Fact, Trait("Spec", "RN-026")]
    public async Task Barcode_AlreadyInAnotherProduct_ReturnsConflict()
    {
        const string Barcode = "7891234567895";
        using var admin = await AdminAsync();
        var category = await CreateCategoryAsync(admin, "Bolsas");
        var first = await CreateProductAsync(admin, category, "Bolsa Um");
        var second = await CreateProductAsync(admin, category, "Bolsa Dois");
        await GenerateAsync(admin, first, [], []);
        await GenerateAsync(admin, second, [], []);
        var firstVariant = (await GetProductAsync(admin, first)).GetProperty("variants")[0].GetProperty("id").GetGuid();
        var secondVariant = (await GetProductAsync(admin, second)).GetProperty("variants")[0].GetProperty("id").GetGuid();

        (await admin.PutAsync($"/api/v1/admin/products/{first}/variants/{firstVariant}", new { sku = "UM", barcode = Barcode })).EnsureSuccessStatusCode();

        (await admin.PutAsync($"/api/v1/admin/products/{second}/variants/{secondVariant}", new { sku = "DOIS", barcode = Barcode })).StatusCode
            .ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact, Trait("Spec", "Q07")]
    public async Task Store_MainCategoryListsSubcategoryProducts()
    {
        using var admin = await AdminAsync();
        var main = await CreateCategoryAsync(admin, "Anéis");
        var sub = await CreateCategoryAsync(admin, "Anéis masculinos", parentId: main);
        await ProductInStoreAsync(admin, "Anel Liso", sub);

        var list = await Client.GetFromJsonAsync<JsonElement>("/api/v1/catalog/products?category=aneis", CancellationToken);

        list.GetProperty("totalItems").GetInt32().ShouldBe(1);
    }

    private Task<JsonElement> GetPublicCategoryAsync(string slug) =>
        Client.GetFromJsonAsync<JsonElement>($"/api/v1/catalog/categories/{slug}", CancellationToken);
}
