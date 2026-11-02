using System.Data.Common;
using Dapper;
using ECommerce.Application.Catalog.ListProducts;

namespace ECommerce.Application.Catalog;

/// <summary>Monta o cartão de produto da loja (RF-020 CA4): usado pela listagem e pela vitrine (RF-025).</summary>
internal static class ProductSummaries
{
    private const string BaseSql = """
        SELECT p.id, p.slug, p.name, c.slug AS category_slug, min(coalesce(v.price, p.base_price)) AS price_from
          FROM catalog.products p
          JOIN catalog.categories c       ON c.id = p.category_id AND c.is_active
          JOIN catalog.product_variants v ON v.product_id = p.id AND v.is_active
         WHERE p.is_active AND p.id = ANY (@Ids)
         GROUP BY p.id, c.slug
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

    /// <summary>Só as peças visíveis na loja (RF-020 CA1), na ordem de <paramref name="ids"/>.</summary>
    public static async Task<IReadOnlyList<ProductSummaryResponse>> LoadVisibleAsync(
        DbConnection connection, IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var rows = (await connection.QueryAsync<ProductRow>(
            new CommandDefinition(BaseSql, new { Ids = ids.ToArray() }, cancellationToken: cancellationToken))).ToDictionary(row => row.Id);

        var ordered = ids.Where(rows.ContainsKey).Select(id => rows[id]).ToList();
        return await ToSummariesAsync(connection, ordered, cancellationToken);
    }

    public static async Task<IReadOnlyList<ProductSummaryResponse>> ToSummariesAsync(
        DbConnection connection, IReadOnlyList<ProductRow> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var ids = rows.Select(row => row.Id).ToArray();
        using var grid = await connection.QueryMultipleAsync(new CommandDefinition(DetailsSql, new { Ids = ids }, cancellationToken: cancellationToken));

        var colorsByProduct = (await grid.ReadAsync<ColorRow>())
            .ToLookup(color => color.ProductId, color => new ColorResponse(color.Id, color.Name, color.Slug, color.Hex));
        var imageByProduct = (await grid.ReadAsync<ImageRow>())
            .ToDictionary(image => image.ProductId, image => new ProductImageResponse(image.Url, image.Alt, image.ColorId));

        return rows
            .Select(row => new ProductSummaryResponse(
                row.Id, row.Slug, row.Name, row.CategorySlug, row.PriceFrom, colorsByProduct[row.Id].ToList(), imageByProduct.GetValueOrDefault(row.Id)))
            .ToList();
    }

    internal sealed class ProductRow
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
