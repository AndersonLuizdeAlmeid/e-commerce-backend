using Dapper;
using ECommerce.Application.Abstractions.Caching;
using ECommerce.Application.Abstractions.Data;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Domain.Abstractions;

namespace ECommerce.Application.Catalog.GetCategoryBySlug;

internal sealed class GetCategoryBySlugHandler(IDbConnectionFactory connectionFactory, ICacheService cache)
    : IQueryHandler<GetCategoryBySlugQuery, CategoryDetailsResponse>
{
    private const string Sql = """
        SELECT id, name, slug, description, seo_title, seo_description
          FROM catalog.categories
         WHERE slug = @Slug AND is_active
        """;

    public async Task<Result<CategoryDetailsResponse>> Handle(
        GetCategoryBySlugQuery query, CancellationToken cancellationToken)
    {
        var category = await cache.GetOrCreateAsync(
            CatalogCache.CategoryKey(query.Slug),
            ct => LoadAsync(query.Slug, ct),
            CatalogCache.Categories,
            cancellationToken);

        return category is null ? CatalogErrors.CategoryNotFound : category;
    }

    private async ValueTask<CategoryDetailsResponse?> LoadAsync(string slug, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        var row = await connection.QuerySingleOrDefaultAsync<CategoryRow>(
            new CommandDefinition(Sql, new { Slug = slug }, cancellationToken: cancellationToken));

        return row is null
            ? null
            : new CategoryDetailsResponse(
                row.Id, row.Name, row.Slug, row.Description,
                SeoDefaults.Build(row.SeoTitle, row.SeoDescription, row.Name, row.Description));
    }

    private sealed class CategoryRow
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Slug { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string? SeoTitle { get; init; }
        public string? SeoDescription { get; init; }
    }
}
