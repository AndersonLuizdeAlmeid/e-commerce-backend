using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ECommerce.Api.FunctionalTests.Infrastructure;
using ECommerce.Application.Abstractions.Caching;
using ECommerce.Application.Catalog;
using Microsoft.Extensions.DependencyInjection;

namespace ECommerce.Api.FunctionalTests.Catalog;

public sealed class CatalogCachingTests(ApiFactory factory) : FunctionalTest(factory)
{
    [Fact, Trait("Spec", "12-CA1")]
    public async Task ListCategories_SecondReadComesFromCacheUntilTagIsInvalidated()
    {
        await Catalog.CategoryAsync("Camisetas", "camisetas");
        (await GetCategorySlugsAsync()).ShouldBe(["camisetas"]);

        // Escrita por fora da API: se a segunda leitura fosse ao banco, a categoria nova apareceria.
        await Catalog.CategoryAsync("Moletons", "moletons");
        (await GetCategorySlugsAsync()).ShouldBe(["camisetas"]);

        await Factory.Services.GetRequiredService<ICacheService>().RemoveByTagAsync(CatalogCache.CategoriesTag, CancellationToken);
        (await GetCategorySlugsAsync()).ShouldBe(["camisetas", "moletons"]);
    }

    [Theory, Trait("Spec", "12-HTTP")]
    [InlineData("/api/v1/catalog/categories")]
    [InlineData("/api/v1/catalog/products")]
    [InlineData("/api/v1/catalog/filters")]
    [InlineData("/api/v1/catalog/sitemap")]
    public async Task PublicCatalogEndpoints_ReturnCacheControlAndETag(string url)
    {
        var response = await Client.GetAsync(new Uri(url, UriKind.Relative), CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl.ShouldNotBeNull();
        response.Headers.CacheControl.Public.ShouldBeTrue();
        response.Headers.CacheControl.MaxAge.ShouldBe(TimeSpan.FromSeconds(60));
        response.Headers.ETag.ShouldNotBeNull();
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/json");
    }

    [Fact, Trait("Spec", "12-HTTP")]
    public async Task PublicCatalogEndpoint_WithMatchingIfNoneMatch_ReturnsNotModified()
    {
        await Catalog.CategoryAsync("Camisetas", "camisetas");
        var first = await Client.GetAsync(new Uri("/api/v1/catalog/categories", UriKind.Relative), CancellationToken);
        var etag = first.Headers.ETag;
        etag.ShouldNotBeNull();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/catalog/categories");
        request.Headers.IfNoneMatch.Add(etag);
        var second = await Client.SendAsync(request, CancellationToken);

        second.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        second.Headers.ETag.ShouldBe(etag);
        (await second.Content.ReadAsByteArrayAsync(CancellationToken)).ShouldBeEmpty();
    }

    [Fact, Trait("Spec", "12-HTTP")]
    public async Task PublicCatalogEndpoint_WithStaleIfNoneMatch_ReturnsFullBody()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/catalog/categories");
        request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("\"versao-antiga\""));

        var response = await Client.SendAsync(request, CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ErrorResponses_AreNotMarkedAsPubliclyCacheable()
    {
        var response = await Client.GetAsync(new Uri("/api/v1/catalog/categories/nao-existe", UriKind.Relative), CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (response.Headers.CacheControl?.Public ?? false).ShouldBeFalse();
        response.Headers.ETag.ShouldBeNull();
    }

    private async Task<string[]> GetCategorySlugsAsync()
    {
        var categories = await Client.GetFromJsonAsync<CategoryDto[]>("/api/v1/catalog/categories", CancellationToken);
        return categories!.Select(c => c.Slug).ToArray();
    }

    private sealed record CategoryDto(Guid Id, string Name, string Slug);
}
