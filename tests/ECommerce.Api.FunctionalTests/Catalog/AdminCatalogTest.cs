using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ECommerce.Api.FunctionalTests.Infrastructure;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Application.Identity.SeedInitialAdmin;
using ImageMagick;
using Microsoft.Extensions.DependencyInjection;

namespace ECommerce.Api.FunctionalTests.Catalog;

/// <summary>Atalhos dos testes do painel: tudo passa pela API, como faz a tela.</summary>
public abstract class AdminCatalogTest(ApiFactory factory) : FunctionalTest(factory)
{
    private const string AdminEmail = "admin@loja.teste";
    private const string AdminPassword = "Admin@12345";

    protected async Task<AuthClient> AdminAsync()
    {
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<SeedInitialAdminCommand, bool>>();
            await handler.Handle(new SeedInitialAdminCommand(AdminEmail, AdminPassword), CancellationToken);
        }

        var admin = new AuthClient(Factory);
        (await admin.LoginAsync(AdminEmail, AdminPassword)).EnsureSuccessStatusCode();
        return admin;
    }

    protected static Task<Guid> CreateCategoryAsync(AuthClient admin, string name, Guid? parentId = null, string? colorLabel = null, string? sizeLabel = null) =>
        CreateAsync(admin, "/api/v1/admin/categories", new { name, parentId, colorLabel, sizeLabel });

    protected static Task<Guid> CreateColorAsync(AuthClient admin, string name) =>
        CreateAsync(admin, "/api/v1/admin/colors", new { name, hex = "#C9A55C" });

    protected static Task<Guid> CreateSizeAsync(AuthClient admin, string name) =>
        CreateAsync(admin, "/api/v1/admin/sizes", new { name, sortOrder = 1 });

    protected static Task<Guid> CreateProductAsync(AuthClient admin, Guid categoryId, string name) =>
        CreateAsync(admin, "/api/v1/admin/products", ProductBody(categoryId, name));

    protected static object ProductBody(Guid categoryId, string name, string? slug = null) =>
        new { categoryId, name, slug, basePrice = 89.90m, weightGrams = 20, heightCm = 3m, widthCm = 5m, lengthCm = 5m };

    /// <summary>Produto recém-criado, fora da loja, em uma categoria nova.</summary>
    protected static async Task<Guid> DraftProductAsync(AuthClient admin, string name) =>
        await CreateProductAsync(admin, await CreateCategoryAsync(admin, $"Categoria de {name}"), name);

    /// <summary>Produto com variação e foto, já na loja.</summary>
    protected static async Task<Guid> ProductInStoreAsync(AuthClient admin, string name, Guid? categoryId = null)
    {
        var product = categoryId is { } category ? await CreateProductAsync(admin, category, name) : await DraftProductAsync(admin, name);
        await GenerateAsync(admin, product, [], []);
        (await UploadAsync(admin, product, TestImages.Create(MagickFormat.Jpeg, 100, 100), name)).EnsureSuccessStatusCode();
        (await admin.PatchAsync($"/api/v1/admin/products/{product}/status", new { isActive = true })).EnsureSuccessStatusCode();
        return product;
    }

    protected static Task<HttpResponseMessage> UploadAsync(AuthClient client, Guid product, byte[] file, string alt)
    {
        var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(file);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(content, "file", "foto.jpg");
        form.Add(new StringContent(alt), "alt");
        return client.PostContentAsync($"/api/v1/admin/products/{product}/images", form);
    }

    protected static async Task<int> GenerateAsync(AuthClient admin, Guid product, Guid[] colorIds, Guid[] sizeIds)
    {
        var response = await admin.PostAsync($"/api/v1/admin/products/{product}/variants/generate", new { colorIds, sizeIds });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken)).GetProperty("created").GetInt32();
    }

    protected static async Task<JsonElement> GetProductAsync(AuthClient admin, Guid product)
    {
        var response = await admin.GetAsync($"/api/v1/admin/products/{product}");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken);
    }

    private static async Task<Guid> CreateAsync(AuthClient admin, string path, object body)
    {
        var response = await admin.PostAsync(path, body);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken)).GetProperty("id").GetGuid();
    }
}
