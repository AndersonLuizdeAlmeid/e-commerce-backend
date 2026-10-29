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
             WHERE p.is_active
               AND (@Category::text IS NULL OR c.slug = @Category)
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

    private const string DetailsSql = """
        SELECT DISTINCT v.product_id, c.id, c.name, c.slug, c.hex
          FROM catalog.product_variants v
          JOIN catalog.colors c ON c.id = v.color_id
         WHERE v.product_id = ANY (@Ids) AND v.is_active
         ORDER BY c.name;

        SELECT DISTINCT ON (i.product_id) i.product_id, i.url, i.alt, i.color_id
          FROM catalog.product_images i
          LEFT JOIN catalog.colors c ON c.id = i.color_id
         WHERE i.product_id = ANY (@Ids)
         ORDER BY i.product_id, c.name NULLS FIRST, i.position;
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
            filters.Search,
            Colors = filters.Colors,
            Sizes = filters.Sizes,
            filters.MinPrice,
            filters.MaxPrice,
            Limit = filters.PageSize,
            Offset = (filters.Page - 1) * filters.PageSize,
        };

        long totalItems;
        List<ProductRow> rows;
        using (var grid = await connection.QueryMultipleAsync(
            new CommandDefinition(sql, parameters, cancellationToken: cancellationToken)))
        {
            totalItems = await grid.ReadSingleAsync<long>();
            rows = (await grid.ReadAsync<ProductRow>()).AsList();
        }

        if (rows.Count == 0)
        {
            return PagedResponse.Create<ProductSummaryResponse>([], filters.Page, filters.PageSize, (int)totalItems);
        }

        var ids = rows.Select(row => row.Id).ToArray();
        ILookup<Guid, ColorResponse> colorsByProduct;
        Dictionary<Guid, ProductImageResponse> imageByProduct;
        using (var grid = await connection.QueryMultipleAsync(
            new CommandDefinition(DetailsSql, new { Ids = ids }, cancellationToken: cancellationToken)))
        {
            colorsByProduct = (await grid.ReadAsync<ColorRow>())
                .ToLookup(color => color.ProductId, color => new ColorResponse(color.Id, color.Name, color.Slug, color.Hex));
            imageByProduct = (await grid.ReadAsync<ImageRow>())
                .ToDictionary(image => image.ProductId, image => new ProductImageResponse(image.Url, image.Alt, image.ColorId));
        }

        var items = rows
            .Select(row => new ProductSummaryResponse(
                row.Id,
                row.Slug,
                row.Name,
                row.CategorySlug,
                row.PriceFrom,
                colorsByProduct[row.Id].ToList(),
                imageByProduct.GetValueOrDefault(row.Id)))
            .ToList();

        return PagedResponse.Create(items, filters.Page, filters.PageSize, (int)totalItems);
    }

    private static NormalizedFilters Normalize(ListProductsQuery query)
    {
        var category = string.IsNullOrWhiteSpace(query.Category) ? null : query.Category.Trim().ToLowerInvariant();
        var search = string.IsNullOrWhiteSpace(query.Search) ? null : EscapeLike(query.Search.Trim());
        var colors = query.Colors.Select(c => c.Trim().ToLowerInvariant()).Where(c => c.Length > 0).Distinct().Order(StringComparer.Ordinal).ToArray();
        var sizes = query.Sizes.Select(s => s.Trim()).Where(s => s.Length > 0).Distinct().Order(StringComparer.Ordinal).ToArray();

        var cacheKey = string.Join(
            '|',
            query.Page.ToString(CultureInfo.InvariantCulture),
            query.PageSize.ToString(CultureInfo.InvariantCulture),
            category,
            search,
            string.Join(',', colors),
            string.Join(',', sizes),
            query.MinPrice?.ToString(CultureInfo.InvariantCulture),
            query.MaxPrice?.ToString(CultureInfo.InvariantCulture),
            query.Sort);

        return new NormalizedFilters(
            query.Page, query.PageSize, category, search, colors, sizes, query.MinPrice, query.MaxPrice, query.Sort, cacheKey);
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
        string? Search,
        string[] Colors,
        string[] Sizes,
        decimal? MinPrice,
        decimal? MaxPrice,
        string Sort,
        string CacheKey);

    private sealed class ProductRow
    {
        public Guid Id { get; init; }
        public string Slug { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string CategorySlug { get; init; } = string.Empty;
        public decimal PriceFrom { get; init; }
    }

    private sealed class ColorRow
    {
        public Guid ProductId { get; init; }
        public Guid Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Slug { get; init; } = string.Empty;
        public string Hex { get; init; } = string.Empty;
    }

    private sealed class ImageRow
    {
        public Guid ProductId { get; init; }
        public string Url { get; init; } = string.Empty;
        public string Alt { get; init; } = string.Empty;
        public Guid? ColorId { get; init; }
    }
}
