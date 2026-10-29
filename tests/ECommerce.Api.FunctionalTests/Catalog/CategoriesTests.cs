using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using ECommerce.Api.FunctionalTests.Infrastructure;
using Npgsql;

namespace ECommerce.Api.FunctionalTests.Catalog;

public sealed class CategoriesTests(ApiFactory factory) : FunctionalTest(factory)
{
    [Fact, Trait("Spec", "RF-022")]
    public async Task ListCategories_ReturnsOnlyActiveCategoriesOrderedByName()
    {
        await InsertCategoryAsync("Moletons", "moletons");
        await InsertCategoryAsync("Camisetas", "camisetas");
        await InsertCategoryAsync("Coleção Antiga", "colecao-antiga", isActive: false);

        var categories = await Client.GetFromJsonAsync<CategoryDto[]>(
            "/api/v1/catalog/categories", TestContext.Current.CancellationToken);

        categories.ShouldNotBeNull();
        categories.Select(c => c.Slug).ShouldBe(["camisetas", "moletons"]);
    }

    [Fact]
    public async Task GetCategoryBySlug_WhenActive_ReturnsCategoryWithDefaultSeo()
    {
        await InsertCategoryAsync("Camisetas", "camisetas", description: "Camisetas de algodão.");

        var category = await Client.GetFromJsonAsync<JsonElement>(
            "/api/v1/catalog/categories/camisetas", TestContext.Current.CancellationToken);

        category.GetProperty("name").GetString().ShouldBe("Camisetas");
        category.GetProperty("description").GetString().ShouldBe("Camisetas de algodão.");
        category.GetProperty("seo").GetProperty("title").GetString().ShouldBe("Camisetas");
        category.GetProperty("seo").GetProperty("description").GetString().ShouldBe("Camisetas de algodão.");
    }

    [Theory]
    [InlineData("nao-existe")]
    [InlineData("colecao-antiga")]
    public async Task GetCategoryBySlug_WhenMissingOrInactive_ReturnsNotFoundProblem(string slug)
    {
        await InsertCategoryAsync("Coleção Antiga", "colecao-antiga", isActive: false);

        var response = await Client.GetAsync(
            new Uri($"/api/v1/catalog/categories/{slug}", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        problem.GetProperty("code").GetString().ShouldBe("Catalog.CategoryNotFound");
        problem.GetProperty("type").GetString().ShouldBe("/errors/catalog.category-not-found");
        problem.TryGetProperty("traceId", out _).ShouldBeTrue();
    }

    private async Task InsertCategoryAsync(string name, string slug, bool isActive = true, string description = "")
    {
        await using var connection = new NpgsqlConnection(Factory.ConnectionString);
        await connection.ExecuteAsync(
            """
            INSERT INTO catalog.categories (id, name, slug, description, is_active, created_at, updated_at)
            VALUES (@Id, @Name, @Slug, @Description, @IsActive, now(), now())
            """,
            new { Id = Guid.CreateVersion7(), Name = name, Slug = slug, Description = description, IsActive = isActive });
    }

    private sealed record CategoryDto(Guid Id, string Name, string Slug);
}
