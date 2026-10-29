using System.Data.Common;
using Dapper;
using ECommerce.Application.Abstractions.Caching;
using ECommerce.Application.Abstractions.Data;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Domain.Abstractions;

namespace ECommerce.Application.Catalog.GetProductBySlug;

internal sealed class GetProductBySlugHandler(IDbConnectionFactory connectionFactory, ICacheService cache)
    : IQueryHandler<GetProductBySlugQuery, ProductBySlugResult>
{
    // Visível na loja: produto e categoria ativos e ao menos uma variação ativa (RN-023, RN-024).
    private const string ProductSql = """
        SELECT p.id, p.slug, p.name, p.description, p.seo_title, p.seo_description, p.updated_at,
               c.slug AS category_slug, c.name AS category_name
          FROM catalog.products p
          JOIN catalog.categories c ON c.id = p.category_id
         WHERE p.slug = @Slug AND p.is_active AND c.is_active
           AND EXISTS (SELECT 1 FROM catalog.product_variants v WHERE v.product_id = p.id AND v.is_active);

        SELECT v.id, v.sku, v.barcode, v.color_id, v.size_id, coalesce(v.price, p.base_price) AS price
          FROM catalog.product_variants v
          JOIN catalog.products p ON p.id = v.product_id
          LEFT JOIN catalog.colors c ON c.id = v.color_id
          LEFT JOIN catalog.sizes  s ON s.id = v.size_id
         WHERE p.slug = @Slug AND v.is_active
         ORDER BY c.name NULLS FIRST, s.sort_order NULLS FIRST;

        SELECT DISTINCT c.id, c.name, c.slug, c.hex
          FROM catalog.product_variants v
          JOIN catalog.products p ON p.id = v.product_id
          JOIN catalog.colors   c ON c.id = v.color_id
         WHERE p.slug = @Slug AND v.is_active
         ORDER BY c.name;

        SELECT DISTINCT s.id, s.name, s.sort_order
          FROM catalog.product_variants v
          JOIN catalog.products p ON p.id = v.product_id
          JOIN catalog.sizes    s ON s.id = v.size_id
         WHERE p.slug = @Slug AND v.is_active
         ORDER BY s.sort_order;

        SELECT i.url, i.alt, i.color_id
          FROM catalog.product_images i
          JOIN catalog.products p ON p.id = i.product_id
          LEFT JOIN catalog.colors c ON c.id = i.color_id
         WHERE p.slug = @Slug
         ORDER BY c.name NULLS FIRST, i.position;
        """;

    // Disponibilidade não é cacheada no detalhe (arquivo 12 §3).
    private const string AvailabilitySql = """
        SELECT v.id
          FROM catalog.product_variants v
          JOIN inventory.stock_items s ON s.variant_id = v.id
         WHERE v.product_id = @ProductId AND v.is_active AND s.quantity_on_hand > 0
        """;

    private const string ResolveSql = """
        SELECT p.slug
          FROM catalog.slug_history h
          JOIN catalog.products p ON p.id = h.entity_id
         WHERE h.old_slug = @Slug AND h.entity_type = 'Product';

        SELECT CASE WHEN c.is_active THEN c.slug END AS category_slug
          FROM catalog.products p
          JOIN catalog.categories c ON c.id = p.category_id
         WHERE p.slug = @Slug;
        """;

    public async Task<Result<ProductBySlugResult>> Handle(GetProductBySlugQuery query, CancellationToken cancellationToken)
    {
        var product = await cache.GetOrCreateAsync(
            CatalogCache.ProductKey(query.Slug),
            ct => LoadAsync(query.Slug, ct),
            CatalogCache.Product,
            cancellationToken);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        if (product is not null)
        {
            var available = (await connection.QueryAsync<Guid>(
                new CommandDefinition(AvailabilitySql, new { ProductId = product.Id }, cancellationToken: cancellationToken)))
                .ToHashSet();

            var variants = product.Variants.Select(v => v with { Available = available.Contains(v.Id) }).ToList();
            return new ProductFound(product with { Variants = variants });
        }

        return await ResolveAsync(connection, query.Slug, cancellationToken);
    }

    private static async Task<Result<ProductBySlugResult>> ResolveAsync(
        DbConnection connection, string slug, CancellationToken cancellationToken)
    {
        using var grid = await connection.QueryMultipleAsync(
            new CommandDefinition(ResolveSql, new { Slug = slug }, cancellationToken: cancellationToken));

        var newSlug = await grid.ReadSingleOrDefaultAsync<string>();
        if (newSlug is not null)
        {
            return new ProductMoved(newSlug);
        }

        var existing = (await grid.ReadAsync<GoneRow>()).SingleOrDefault();
        if (existing is not null)
        {
            return new ProductGone(existing.CategorySlug);
        }

        return CatalogErrors.ProductNotFound;
    }

    private async ValueTask<ProductDetailsResponse?> LoadAsync(string slug, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        using var grid = await connection.QueryMultipleAsync(
            new CommandDefinition(ProductSql, new { Slug = slug }, cancellationToken: cancellationToken));

        var row = await grid.ReadSingleOrDefaultAsync<ProductRow>();
        if (row is null)
        {
            return null;
        }

        var variants = (await grid.ReadAsync<VariantRow>())
            .Select(v => new ProductVariantResponse(v.Id, v.Sku, v.ColorId, v.SizeId, v.Price, v.Barcode, Available: false))
            .ToList();
        var colors = (await grid.ReadAsync<ColorResponse>()).AsList();
        var sizes = (await grid.ReadAsync<SizeRow>()).Select(s => new SizeResponse(s.Id, s.Name)).ToList();
        var images = (await grid.ReadAsync<ImageRow>()).Select(i => new ProductImageResponse(i.Url, i.Alt, i.ColorId)).ToList();

        return new ProductDetailsResponse(
            row.Id,
            row.Slug,
            row.Name,
            row.Description,
            new ProductCategoryResponse(row.CategorySlug, row.CategoryName),
            variants.Min(v => v.Price),
            SeoDefaults.Build(row.SeoTitle, row.SeoDescription, row.Name, row.Description),
            colors,
            sizes,
            variants,
            images,
            DateTime.SpecifyKind(row.UpdatedAt, DateTimeKind.Utc));
    }

    private sealed class ProductRow
    {
        public Guid Id { get; init; }
        public string Slug { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string? SeoTitle { get; init; }
        public string? SeoDescription { get; init; }
        public DateTime UpdatedAt { get; init; }
        public string CategorySlug { get; init; } = string.Empty;
        public string CategoryName { get; init; } = string.Empty;
    }

    private sealed class VariantRow
    {
        public Guid Id { get; init; }
        public string Sku { get; init; } = string.Empty;
        public string? Barcode { get; init; }
        public Guid? ColorId { get; init; }
        public Guid? SizeId { get; init; }
        public decimal Price { get; init; }
    }

    private sealed class SizeRow
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public short SortOrder { get; init; }
    }

    private sealed class ImageRow
    {
        public string Url { get; init; } = string.Empty;
        public string Alt { get; init; } = string.Empty;
        public Guid? ColorId { get; init; }
    }

    private sealed class GoneRow
    {
        public string? CategorySlug { get; init; }
    }
}
