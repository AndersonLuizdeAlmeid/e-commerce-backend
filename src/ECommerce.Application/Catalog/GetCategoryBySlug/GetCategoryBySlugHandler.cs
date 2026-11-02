using Dapper;
using ECommerce.Application.Abstractions.Caching;
using ECommerce.Application.Abstractions.Data;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Domain.Abstractions;

namespace ECommerce.Application.Catalog.GetCategoryBySlug;

internal sealed class ResolveCategorySlugHandler(IDbConnectionFactory connectionFactory) : IQueryHandler<ResolveCategorySlugQuery, string>
{
    private const string Sql = """
        SELECT c.slug
          FROM catalog.slug_history h
          JOIN catalog.categories c ON c.id = h.entity_id
         WHERE h.old_slug = @Slug AND h.entity_type = 'Category' AND c.is_active
        """;

    public async Task<Result<string>> Handle(ResolveCategorySlugQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var slug = await connection.QuerySingleOrDefaultAsync<string>(
            new CommandDefinition(Sql, new { Slug = query.OldSlug }, cancellationToken: cancellationToken));

        return slug is null ? CatalogErrors.CategoryNotFound : slug;
    }
}

internal sealed class GetCategoryBySlugHandler(IDbConnectionFactory connectionFactory, ICacheService cache)
    : IQueryHandler<GetCategoryBySlugQuery, CategoryDetailsResponse>
{
    private const string Sql = $"""
        SELECT c.id, c.name, c.slug, c.description, c.seo_title, c.seo_description,
               {VariantLabelsResponse.SqlColumns}
          FROM catalog.categories c
          {VariantLabelsResponse.SqlParentJoin}
         WHERE c.slug = @Slug AND c.is_active
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
                SeoDefaults.Build(row.SeoTitle, row.SeoDescription, row.Name, row.Description),
                new VariantLabelsResponse(row.ColorLabel, row.SizeLabel));
    }

    private sealed class CategoryRow
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Slug { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string? SeoTitle { get; init; }
        public string? SeoDescription { get; init; }
        public string ColorLabel { get; init; } = string.Empty;
        public string SizeLabel { get; init; } = string.Empty;
    }
}
