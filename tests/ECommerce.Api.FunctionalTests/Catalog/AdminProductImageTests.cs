using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ECommerce.Api.FunctionalTests.Infrastructure;
using ImageMagick;

namespace ECommerce.Api.FunctionalTests.Catalog;

public sealed class AdminProductImageTests(ApiFactory factory) : AdminCatalogTest(factory)
{
    [Fact, Trait("Spec", "RF-061")]
    public async Task Upload_SavesWebpWithoutGps_AndAppearsInStore()
    {
        using var admin = await AdminAsync();
        var product = await ProductInStoreAsync(admin, "Bolsa Shopper");

        var response = await UploadAsync(admin, product, TestImages.JpegWithGps(), "Bolsa Shopper, frente");

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken);
        var key = new Uri(body.GetProperty("url").GetString()!).AbsolutePath.TrimStart('/');
        key.ShouldEndWith(".webp");

        using var stored = new MagickImage(Factory.Images.Find(key)!);
        stored.Format.ShouldBe(MagickFormat.WebP);
        stored.GetExifProfile().ShouldBeNull();

        var store = await Client.GetFromJsonAsync<JsonElement>("/api/v1/catalog/products/bolsa-shopper", CancellationToken);
        store.GetProperty("images").EnumerateArray().Select(i => i.GetProperty("alt").GetString()).ShouldContain("Bolsa Shopper, frente");
    }

    [Fact, Trait("Spec", "RF-061-CA1")]
    public async Task Upload_FakeImage_IsRefusedAndSavesNothing()
    {
        using var admin = await AdminAsync();
        var product = await DraftProductAsync(admin, "Bolsa Shopper");

        var response = await UploadAsync(admin, product, "não sou uma foto"u8.ToArray(), "Texto");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ImagesOfAsync(admin, product)).GetArrayLength().ShouldBe(0);
    }

    [Fact, Trait("Spec", "RF-061")]
    public async Task Upload_WhenBucketFails_LeavesNoRow()
    {
        using var admin = await AdminAsync();
        var product = await DraftProductAsync(admin, "Bolsa Shopper");
        Factory.Images.FailNextPut = true;

        var response = await UploadAsync(admin, product, TestImages.Create(MagickFormat.Jpeg, 100, 100), "Frente");

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        (await ImagesOfAsync(admin, product)).GetArrayLength().ShouldBe(0);
    }

    [Fact, Trait("Spec", "RF-051-CA3")]
    public async Task Product_WithoutImage_CannotEnterStore()
    {
        using var admin = await AdminAsync();
        var product = await DraftProductAsync(admin, "Bolsa Shopper");
        await GenerateAsync(admin, product, [], []);

        var refused = await admin.PatchAsync($"/api/v1/admin/products/{product}/status", new { isActive = true });
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        await UploadAsync(admin, product, TestImages.Create(MagickFormat.Jpeg, 100, 100), "Frente");
        (await admin.PatchAsync($"/api/v1/admin/products/{product}/status", new { isActive = true })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact, Trait("Spec", "RF-051-CA3")]
    public async Task Delete_LastImageOfActiveProduct_IsRefused()
    {
        using var admin = await AdminAsync();
        var product = await ProductInStoreAsync(admin, "Bolsa Shopper");
        var only = (await ImagesOfAsync(admin, product))[0].GetProperty("id").GetGuid();

        (await admin.DeleteAsync($"/api/v1/admin/products/{product}/images/{only}")).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact, Trait("Spec", "RF-061-CA4")]
    public async Task Delete_RemovesRowNow_AndFileThroughOutbox()
    {
        using var admin = await AdminAsync();
        var product = await DraftProductAsync(admin, "Bolsa Shopper");
        var uploaded = await UploadAsync(admin, product, TestImages.Create(MagickFormat.Jpeg, 100, 100), "Frente");
        var body = await uploaded.Content.ReadFromJsonAsync<JsonElement>(CancellationToken);
        var key = new Uri(body.GetProperty("url").GetString()!).AbsolutePath.TrimStart('/');

        (await admin.DeleteAsync($"/api/v1/admin/products/{product}/images/{body.GetProperty("id").GetGuid()}")).StatusCode
            .ShouldBe(HttpStatusCode.NoContent);

        (await ImagesOfAsync(admin, product)).GetArrayLength().ShouldBe(0);
        for (var attempt = 0; attempt < 50 && Factory.Images.Find(key) is not null; attempt++)
        {
            await Task.Delay(100, CancellationToken);
        }

        Factory.Images.Find(key).ShouldBeNull();
    }

    [Fact, Trait("Spec", "RF-061-CA5")]
    public async Task Reorder_ChangesCover_AndOutdatedListIsRefused()
    {
        using var admin = await AdminAsync();
        var product = await DraftProductAsync(admin, "Bolsa Shopper");
        var photo = TestImages.Create(MagickFormat.Jpeg, 100, 100);
        await UploadAsync(admin, product, photo, "Primeira");
        await UploadAsync(admin, product, photo, "Segunda");
        var images = await ImagesOfAsync(admin, product);
        var first = images[0].GetProperty("id").GetGuid();
        var second = images[1].GetProperty("id").GetGuid();

        (await admin.PutAsync($"/api/v1/admin/products/{product}/images/order", new { colorId = (Guid?)null, imageIds = new[] { second, first } })).StatusCode
            .ShouldBe(HttpStatusCode.NoContent);
        (await ImagesOfAsync(admin, product))[0].GetProperty("alt").GetString().ShouldBe("Segunda");

        (await admin.PutAsync($"/api/v1/admin/products/{product}/images/order", new { colorId = (Guid?)null, imageIds = new[] { second } })).StatusCode
            .ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact, Trait("Spec", "RF-061")]
    public async Task Update_ChangesAltAndMovesToEndOfAnotherFinish()
    {
        using var admin = await AdminAsync();
        var product = await DraftProductAsync(admin, "Bolsa Shopper");
        var color = await CreateColorAsync(admin, "Caramelo");
        await UploadAsync(admin, product, TestImages.Create(MagickFormat.Jpeg, 100, 100), "Frente");
        var image = (await ImagesOfAsync(admin, product))[0].GetProperty("id").GetGuid();

        (await admin.PutAsync($"/api/v1/admin/products/{product}/images/{image}", new { alt = "Bolsa caramelo", colorId = color })).StatusCode
            .ShouldBe(HttpStatusCode.NoContent);

        var updated = (await ImagesOfAsync(admin, product))[0];
        updated.GetProperty("alt").GetString().ShouldBe("Bolsa caramelo");
        updated.GetProperty("colorId").GetGuid().ShouldBe(color);
    }

    [Fact, Trait("Spec", "RF-061")]
    public async Task Upload_RejectsCustomer()
    {
        using var admin = await AdminAsync();
        var product = await DraftProductAsync(admin, "Bolsa Shopper");
        using var customer = await AuthClient.RegisteredAsync(Factory, "maria@loja.teste");

        var response = await UploadAsync(customer, product, TestImages.Create(MagickFormat.Jpeg, 100, 100), "Frente");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private static async Task<JsonElement> ImagesOfAsync(AuthClient admin, Guid product) =>
        (await GetProductAsync(admin, product)).GetProperty("images");
}
