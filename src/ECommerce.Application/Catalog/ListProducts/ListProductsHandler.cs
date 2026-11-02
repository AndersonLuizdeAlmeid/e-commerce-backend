using System.Globalization;
using Dapper;
using ECommerce.Application.Abstractions.Caching;
using ECommerce.Application.Abstractions.Data;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Application.Abstractions.Paging;
using ECommerce.Domain.Abstractions;

namespace ECommerce.Application.Catalog.ListProducts;

internal sealed class ListProductsHandler(IDbConnectionFactory connectionFactory, ICacheService cache)
    : IQueryHandler<ListProductsQuery, PagedResponse<ProductSummaryResponse>>
{
    // CA1: produto ativo, em categoria ativa e com ao menos uma variação ativa.
    // CA3: busca sem diferenciar maiúsculas nem acentos (usa ix_products_name_trgm).
    // CA4: "a partir de" = menor preço efetivo entre as variações ativas.
    private const string FilteredCte = """
        WITH filtered AS (
            SELECT p.id, p.slug, p.name, c.slug AS category_slug, p.created_at,
                   min(coalesce(v.price, p.base_price)) AS price_from
              FROM catalog.products p
              JOIN catalog.categories c       ON c.id = p.category_id AND c.is_active
              JOIN catalog.product_variants v ON v.product_id = p.id AND v.is_active
              LEFT JOIN catalog.categories parent ON parent.id = c.parent_id
             WHERE p.is_active
               AND (@Category::text IS NULL OR c.slug = @Category OR parent.slug = @Category)
               AND (@Collection::text IS NULL OR EXISTS (
                    SELECT 1
                      FROM catalog.product_collections pc
                      JOIN catalog.collections col ON col.id = pc.collection_id AND col.is_active
                     WHERE pc.product_id = p.id AND col.slug = @Collection))
               AND (@Search::text IS NULL
                    OR public.immutable_unaccent(lower(p.name)) LIKE '%' || public.immutable_unaccent(lower(@Search)) || '%')
               AND ((cardinality(@Colors::text[]) = 0 AND cardinality(@Sizes::text[]) = 0) OR EXISTS (
                    SELECT 1
                      FROM catalog.product_variants fv
                      LEFT JOIN catalog.colors fc ON fc.id = fv.color_id
                      LEFT JOIN catalog.sizes  fs ON fs.id = fv.size_id
                     WHERE fv.product_id = p.id AND fv.is_active
                       AND (cardinality(@Colors::text[]) = 0 OR fc.slug = ANY (@Colors::text[]))
                       AND (cardinality(@Sizes::text[]) = 0 OR fs.name = ANY (@Sizes::text[]))))
             GROUP BY p.id, c.slug
        ), priced AS (
            SELECT id, slug, name, category_slug, created_at, price_from
              FROM filtered
             WHERE (@MinPrice::numeric IS NULL OR price_from >= @MinPrice)
               AND (@MaxPrice::numeric IS NULL OR price_from <= @MaxPrice)
        )
        """;

    public async Task<Result<PagedResponse<ProductSummaryResponse>>> Handle(
        ListProductsQuery query, CancellationToken cancellationToken)
    {
        var filters = Normalize(query);

        var page = await cache.GetOrCreateAsync(
            CatalogCache.ProductListKey(filters.CacheKey),
            ct => LoadAsync(filters, ct),
            CatalogCache.ProductList,
            cancellationToken);

        return page;
    }

    private async ValueTask<PagedResponse<ProductSummaryResponse>> LoadAsync(
        NormalizedFilters filters, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        var sql = $"""
            {FilteredCte}
            SELECT count(*) FROM priced;

            {FilteredCte}
            SELECT id, slug, name, category_slug, price_from
              FROM priced
             ORDER BY {ProductSort.ToSql(filters.Sort)}
             LIMIT @Limit OFFSET @Offset;
            """;

        var parameters = new
        {
            filters.Category,
            filters.Collection,
            filters.Search,
            Colors = filters.Colors,
            Sizes = filters.Sizes,
            filters.MinPrice,
            filters.MaxPrice,
            Limit = filters.PageSize,
            Offset = (filters.Page - 1) * filters.PageSize,
        };

        long totalItems;
        List<ProductSummaries.ProductRow> rows;
        using (var grid = await connection.QueryMultipleAsync(
            new CommandDefinition(sql, parameters, cancellationToken: cancellationToken)))
        {
            totalItems = await grid.ReadSingleAsync<long>();
            rows = (await grid.ReadAsync<ProductSummaries.ProductRow>()).AsList();
        }

        var items = await ProductSummaries.ToSummariesAsync(connection, rows, cancellationToken);
        return PagedResponse.Create(items, filters.Page, filters.PageSize, (int)totalItems);
    }

    private static NormalizedFilters Normalize(ListProductsQuery query)
    {
        var category = string.IsNullOrWhiteSpace(query.Category) ? null : query.Category.Trim().ToLowerInvariant();
        var collection = string.IsNullOrWhiteSpace(query.Collection) ? null : query.Collection.Trim().ToLowerInvariant();
        var search = string.IsNullOrWhiteSpace(query.Search) ? null : EscapeLike(query.Search.Trim());
        var colors = query.Colors.Select(c => c.Trim().ToLowerInvariant()).Where(c => c.Length > 0).Distinct().Order(StringComparer.Ordinal).ToArray();
        var sizes = query.Sizes.Select(s => s.Trim()).Where(s => s.Length > 0).Distinct().Order(StringComparer.Ordinal).ToArray();

        var cacheKey = string.Join(
            '|',
            query.Page.ToString(CultureInfo.InvariantCulture),
            query.PageSize.ToString(CultureInfo.InvariantCulture),
            category,
            collection,
            search,
            string.Join(',', colors),
            string.Join(',', sizes),
            query.MinPrice?.ToString(CultureInfo.InvariantCulture),
            query.MaxPrice?.ToString(CultureInfo.InvariantCulture),
            query.Sort);

        return new NormalizedFilters(
            query.Page, query.PageSize, category, collection, search, colors, sizes, query.MinPrice, query.MaxPrice, query.Sort, cacheKey);
    }

    // O termo vai como parâmetro; só escapamos os curingas do LIKE para "50%" não virar padrão.
    private static string EscapeLike(string value) =>
        value.Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);

    private sealed record NormalizedFilters(
        int Page,
        int PageSize,
        string? Category,
        string? Collection,
        string? Search,
        string[] Colors,
        string[] Sizes,
        decimal? MinPrice,
        decimal? MaxPrice,
        string Sort,
        string CacheKey);
}
