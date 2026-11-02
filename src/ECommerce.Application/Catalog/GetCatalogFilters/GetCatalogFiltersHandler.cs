using Dapper;
using ECommerce.Application.Abstractions.Caching;
using ECommerce.Application.Abstractions.Data;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Domain.Abstractions;

namespace ECommerce.Application.Catalog.GetCatalogFilters;

internal sealed class GetCatalogFiltersHandler(IDbConnectionFactory connectionFactory, ICacheService cache)
    : IQueryHandler<GetCatalogFiltersQuery, CatalogFiltersResponse>
{
    // Mesmo critério de visibilidade da listagem (RF-020 CA1), para nenhum filtro levar a uma lista vazia.
    private const string VisibleVariants = """
        WITH visible AS (
            SELECT v.color_id, v.size_id, p.category_id, coalesce(v.price, p.base_price) AS price
              FROM catalog.product_variants v
              JOIN catalog.products   p ON p.id = v.product_id AND p.is_active
              JOIN catalog.categories c ON c.id = p.category_id AND c.is_active
              LEFT JOIN catalog.categories parent ON parent.id = c.parent_id
             WHERE v.is_active
               AND (@Category::text IS NULL OR c.slug = @Category OR parent.slug = @Category)
               AND (@Collection::text IS NULL OR EXISTS (
                    SELECT 1
                      FROM catalog.product_collections pc
                      JOIN catalog.collections col ON col.id = pc.collection_id AND col.is_active
                     WHERE pc.product_id = p.id AND col.slug = @Collection))
        )
        """;

    private const string Sql = $"""
        {VisibleVariants}
        SELECT DISTINCT co.id, co.name, co.slug, co.hex
          FROM visible JOIN catalog.colors co ON co.id = visible.color_id
         ORDER BY co.name;

        {VisibleVariants}
        SELECT DISTINCT s.id, s.name, s.sort_order
          FROM visible JOIN catalog.sizes s ON s.id = visible.size_id
         ORDER BY s.sort_order;

        {VisibleVariants}
        SELECT min(price) AS min_price, max(price) AS max_price FROM visible;

        {VisibleVariants}
        SELECT DISTINCT ca.id, ca.name, ca.slug
          FROM visible JOIN catalog.categories ca ON ca.id = visible.category_id
         ORDER BY ca.name;
        """;

    public async Task<Result<CatalogFiltersResponse>> Handle(GetCatalogFiltersQuery query, CancellationToken cancellationToken)
    {
        var category = string.IsNullOrWhiteSpace(query.Category) ? null : query.Category.Trim().ToLowerInvariant();
        var collection = string.IsNullOrWhiteSpace(query.Collection) ? null : query.Collection.Trim().ToLowerInvariant();

        var filters = await cache.GetOrCreateAsync(
            CatalogCache.FiltersKey(category, collection),
            ct => LoadAsync(category, collection, ct),
            CatalogCache.Filters,
            cancellationToken);

        return filters;
    }

    private async ValueTask<CatalogFiltersResponse> LoadAsync(string? category, string? collection, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        using var grid = await connection.QueryMultipleAsync(
            new CommandDefinition(Sql, new { Category = category, Collection = collection }, cancellationToken: cancellationToken));

        var colors = (await grid.ReadAsync<ColorResponse>()).AsList();
        var sizes = (await grid.ReadAsync<SizeRow>()).Select(s => new SizeResponse(s.Id, s.Name)).ToList();
        var range = await grid.ReadSingleAsync<PriceRangeRow>();

        var categories = (await grid.ReadAsync<CategoryRow>()).Select(c => new FilterCategoryResponse(c.Id, c.Name, c.Slug)).ToList();

        return new CatalogFiltersResponse(colors, sizes, range.MinPrice, range.MaxPrice, categories);
    }

    private sealed class SizeRow
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public short SortOrder { get; init; }
    }

    private sealed class CategoryRow
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Slug { get; init; } = string.Empty;
    }

    private sealed class PriceRangeRow
    {
        public decimal? MinPrice { get; init; }
        public decimal? MaxPrice { get; init; }
    }
}
