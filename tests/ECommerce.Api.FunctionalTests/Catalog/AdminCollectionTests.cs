using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ECommerce.Api.FunctionalTests.Infrastructure;
using ECommerce.Application.Abstractions.Caching;
using ECommerce.Application.Catalog;
using ImageMagick;
using Microsoft.Extensions.DependencyInjection;

namespace ECommerce.Api.FunctionalTests.Catalog;

public sealed class AdminCollectionTests(ApiFactory factory) : AdminCatalogTest(factory)
{
    private static object Body(string name = "Eles", string? slug = null, string cardTheme = "Graphite", int position = 10) =>
        new { name, slug, description = "Para eles.", cardTheme, position };

    private static async Task<Guid> CreateCollectionAsync(AuthClient admin, string name = "Eles", string? slug = null)
    {
        var response = await admin.PostAsync("/api/v1/admin/collections", Body(name, slug));
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken)).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> AdminCollectionAsync(AuthClient admin, Guid id)
    {
        var response = await admin.GetAsync("/api/v1/admin/collections");
        response.EnsureSuccessStatusCode();
        var list = await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken);
        return list.EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == id);
    }

    private async Task<string[]> StoreProductsAsync(string collection)
    {
        await Factory.Services.GetRequiredService<ICacheService>().RemoveByTagAsync(CatalogCache.ProductsTag, CancellationToken);
        var page = await Client.GetFromJsonAsync<JsonElement>($"/api/v1/catalog/products?collection={collection}", CancellationToken);
        return page.GetProperty("items").EnumerateArray().Select(p => p.GetProperty("slug").GetString()!).Order().ToArray();
    }

    [Fact, Trait("Spec", "RF-024")]
    public async Task Collection_CrossesCategories_AndListsOnlyItsProducts()
    {
        using var admin = await AdminAsync();
        var ring = await ProductInStoreAsync(admin, "Anel Liso");
        var bracelet = await ProductInStoreAsync(admin, "Pulseira Grumet");
        await ProductInStoreAsync(admin, "Bolsa Shopper");
        var collection = await CreateCollectionAsync(admin);

        var added = await admin.PostAsync($"/api/v1/admin/collections/{collection}/products", new { productIds = new[] { ring, bracelet } });

        added.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await StoreProductsAsync("eles")).ShouldBe(["anel-liso", "pulseira-grumet"]);
    }

    [Fact, Trait("Spec", "RF-024-CA5")]
    public async Task AddProduct_AlreadyInCollection_DoesNotDuplicateNorFail()
    {
        using var admin = await AdminAsync();
        var ring = await ProductInStoreAsync(admin, "Anel Liso");
        var collection = await CreateCollectionAsync(admin);
        await admin.PostAsync($"/api/v1/admin/collections/{collection}/products", new { productIds = new[] { ring } });

        var again = await admin.PostAsync($"/api/v1/admin/collections/{collection}/products", new { productIds = new[] { ring } });

        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await again.Content.ReadFromJsonAsync<JsonElement>(CancellationToken)).GetProperty("added").GetInt32().ShouldBe(0);
        (await AdminCollectionAsync(admin, collection)).GetProperty("productCount").GetInt32().ShouldBe(1);
    }

    [Fact, Trait("Spec", "RF-024")]
    public async Task RemoveProducts_TakesThemOutOfTheCollection()
    {
        using var admin = await AdminAsync();
        var ring = await ProductInStoreAsync(admin, "Anel Liso");
        var collection = await CreateCollectionAsync(admin);
        await admin.PostAsync($"/api/v1/admin/collections/{collection}/products", new { productIds = new[] { ring } });

        var removed = await admin.DeleteAsync($"/api/v1/admin/collections/{collection}/products", new { productIds = new[] { ring } });

        removed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await StoreProductsAsync("eles")).ShouldBeEmpty();
    }

    [Fact, Trait("Spec", "RF-024-CA1")]
    public async Task InactiveProduct_LeavesPublicList_ButStaysLinked()
    {
        using var admin = await AdminAsync();
        var ring = await ProductInStoreAsync(admin, "Anel Liso");
        var collection = await CreateCollectionAsync(admin);
        await admin.PostAsync($"/api/v1/admin/collections/{collection}/products", new { productIds = new[] { ring } });

        (await admin.PatchAsync($"/api/v1/admin/products/{ring}/status", new { isActive = false })).EnsureSuccessStatusCode();

        (await StoreProductsAsync("eles")).ShouldBeEmpty();
        (await AdminCollectionAsync(admin, collection)).GetProperty("productCount").GetInt32().ShouldBe(1);
    }

    [Fact, Trait("Spec", "RF-024-CA2")]
    public async Task InactiveOrMissingCollection_Returns404()
    {
        using var admin = await AdminAsync();
        var collection = await CreateCollectionAsync(admin);
        (await admin.PatchAsync($"/api/v1/admin/collections/{collection}/status", new { isActive = false })).EnsureSuccessStatusCode();

        var inactive = await Client.GetAsync(new Uri("/api/v1/catalog/collections/eles", UriKind.Relative), CancellationToken);
        var missing = await Client.GetAsync(new Uri("/api/v1/catalog/collections/nao-existe", UriKind.Relative), CancellationToken);

        inactive.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact, Trait("Spec", "RF-024-CA3")]
    public async Task ActiveCollection_EntersSitemap()
    {
        using var admin = await AdminAsync();
        await CreateCollectionAsync(admin);

        var sitemap = await Client.GetFromJsonAsync<JsonElement>("/api/v1/catalog/sitemap", CancellationToken);

        sitemap.GetProperty("collections").EnumerateArray().Select(c => c.GetProperty("slug").GetString()).ShouldBe(["eles"]);
    }

    [Fact, Trait("Spec", "RF-024-CA4")]
    public async Task ChangingSlug_OldOneRedirectsPermanently()
    {
        using var admin = await AdminAsync();
        var collection = await CreateCollectionAsync(admin);

        var update = await admin.PutAsync($"/api/v1/admin/collections/{collection}", new { version = 0, collection = Body(slug: "linha-masculina") });
        update.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        using var noRedirect = Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var old = await noRedirect.GetAsync(new Uri("/api/v1/catalog/collections/eles", UriKind.Relative), CancellationToken);

        old.StatusCode.ShouldBe(HttpStatusCode.MovedPermanently);
        old.Headers.Location!.ToString().ShouldEndWith("/collections/linha-masculina");
    }

    [Fact, Trait("Spec", "RF-024")]
    public async Task Update_WithOldVersion_ReturnsConflict()
    {
        using var admin = await AdminAsync();
        var collection = await CreateCollectionAsync(admin);
        await admin.PutAsync($"/api/v1/admin/collections/{collection}", new { version = 0, collection = Body(position: 20) });

        var stale = await admin.PutAsync($"/api/v1/admin/collections/{collection}", new { version = 0, collection = Body(position: 30) });

        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact, Trait("Spec", "RF-024")]
    public async Task InvalidTheme_ReturnsBadRequest()
    {
        using var admin = await AdminAsync();

        var response = await admin.PostAsync("/api/v1/admin/collections", Body(cardTheme: "Verde"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact, Trait("Spec", "RF-024")]
    public async Task SlugOfAnotherPage_IsRefused()
    {
        using var admin = await AdminAsync();
        await CreateCollectionAsync(admin);
        var collection = await CreateCollectionAsync(admin, "Prata 925");
        await admin.PutAsync($"/api/v1/admin/collections/{collection}", new { version = 0, collection = Body("Prata 925", "prata") });

        var taken = await admin.PostAsync("/api/v1/admin/collections", Body("Outra", "prata-925"));

        taken.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact, Trait("Spec", "RF-024-CA6")]
    public async Task DeletingCollection_KeepsProductsInStore()
    {
        using var admin = await AdminAsync();
        var ring = await ProductInStoreAsync(admin, "Anel Liso");
        var collection = await CreateCollectionAsync(admin);
        await admin.PostAsync($"/api/v1/admin/collections/{collection}/products", new { productIds = new[] { ring } });

        (await admin.DeleteAsync($"/api/v1/admin/collections/{collection}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var product = await Client.GetAsync(new Uri("/api/v1/catalog/products/anel-liso", UriKind.Relative), CancellationToken);
        product.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact, Trait("Spec", "RF-061")]
    public async Task CoverImage_IsSavedAsWebp_AndTheOldOneIsReplaced()
    {
        using var admin = await AdminAsync();
        var collection = await CreateCollectionAsync(admin);

        var first = await SetImageAsync(admin, collection, TestImages.Create(MagickFormat.Jpeg, 100, 100));
        var firstUrl = (await first.Content.ReadFromJsonAsync<JsonElement>(CancellationToken)).GetProperty("imageUrl").GetString()!;
        var second = await SetImageAsync(admin, collection, TestImages.Create(MagickFormat.Png, 100, 100));
        var secondUrl = (await second.Content.ReadFromJsonAsync<JsonElement>(CancellationToken)).GetProperty("imageUrl").GetString()!;

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        secondUrl.ShouldEndWith(".webp");
        secondUrl.ShouldNotBe(firstUrl);
        (await AdminCollectionAsync(admin, collection)).GetProperty("imageUrl").GetString().ShouldBe(secondUrl);
    }

    [Fact, Trait("Spec", "RF-061-CA1")]
    public async Task CoverImage_FakeFile_IsRefused()
    {
        using var admin = await AdminAsync();
        var collection = await CreateCollectionAsync(admin);

        var response = await SetImageAsync(admin, collection, "não sou uma foto"u8.ToArray());

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AdminCollectionAsync(admin, collection)).GetProperty("imageUrl").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact, Trait("Spec", "RF-024")]
    public async Task Customer_CannotManageCollections()
    {
        using var customer = await AuthClient.RegisteredAsync(Factory, "cliente@loja.teste");

        var response = await customer.PostAsync("/api/v1/admin/collections", Body());

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private static Task<HttpResponseMessage> SetImageAsync(AuthClient admin, Guid collection, byte[] file)
    {
        var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(file);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(content, "file", "capa.jpg");
        return admin.PutAsync($"/api/v1/admin/collections/{collection}/image", form);
    }
}
