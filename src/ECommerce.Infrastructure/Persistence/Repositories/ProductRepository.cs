using Dapper;
using ECommerce.Application.Abstractions.Data;
using ECommerce.Domain.Catalog;

namespace ECommerce.Infrastructure.Persistence.Repositories;

internal sealed class ProductRepository(IUnitOfWork unitOfWork) : IProductRepository
{
    public async Task<Product?> GetByIdAsync(ProductId id, CancellationToken cancellationToken)
    {
        using var grid = await unitOfWork.Connection.QueryMultipleAsync(Command(
            """
            SELECT id, category_id, name, slug, description, base_price, weight_grams, height_cm, width_cm, length_cm,
                   seo_title, seo_description, is_active, version, created_at, updated_at
              FROM catalog.products
             WHERE id = @Id;

            SELECT id, color_id, size_id, sku, barcode, price, weight_grams, height_cm, width_cm, length_cm,
                   is_active, created_at, updated_at
              FROM catalog.product_variants
             WHERE product_id = @Id;
            """,
            new { Id = id.Value },
            cancellationToken));

        var row = await grid.ReadSingleOrDefaultAsync<ProductRow>();
        if (row is null)
        {
            return null;
        }

        var variants = (await grid.ReadAsync<VariantRow>()).Select(ToVariant).ToList();
        return Product.Rehydrate(
            new ProductId(row.Id),
            new ProductContent(
                new CategoryId(row.CategoryId),
                row.Name,
                Slug.Rehydrate(row.Slug),
                row.Description,
                row.BasePrice,
                new PackageSize(row.WeightGrams, row.HeightCm, row.WidthCm, row.LengthCm),
                row.SeoTitle,
                row.SeoDescription),
            row.IsActive,
            row.Version,
            row.CreatedAt,
            row.UpdatedAt,
            variants);
    }

    public Task<bool> SlugInUseAsync(Slug slug, ProductId? exceptId, CancellationToken cancellationToken) =>
        Exists(
            "SELECT EXISTS (SELECT 1 FROM catalog.products WHERE slug = @Slug AND (@ExceptId::uuid IS NULL OR id <> @ExceptId))",
            new { Slug = slug.Value, ExceptId = exceptId?.Value },
            cancellationToken);

    public Task<bool> SkuInUseAsync(string sku, VariantId? exceptId, CancellationToken cancellationToken) =>
        Exists(
            "SELECT EXISTS (SELECT 1 FROM catalog.product_variants WHERE lower(sku) = lower(@Sku) AND (@ExceptId::uuid IS NULL OR id <> @ExceptId))",
            new { Sku = sku, ExceptId = exceptId?.Value },
            cancellationToken);

    public Task<bool> BarcodeInUseAsync(Gtin barcode, VariantId? exceptId, CancellationToken cancellationToken) =>
        Exists(
            "SELECT EXISTS (SELECT 1 FROM catalog.product_variants WHERE barcode = @Barcode AND (@ExceptId::uuid IS NULL OR id <> @ExceptId))",
            new { Barcode = barcode.Value, ExceptId = exceptId?.Value },
            cancellationToken);

    public async Task AddAsync(Product product, CancellationToken cancellationToken)
    {
        await unitOfWork.Connection.ExecuteAsync(Command(
            """
            INSERT INTO catalog.products
                (id, category_id, name, slug, description, base_price, weight_grams, height_cm, width_cm, length_cm,
                 seo_title, seo_description, is_active, version, created_at, updated_at)
            VALUES
                (@Id, @CategoryId, @Name, @Slug, @Description, @BasePrice, @WeightGrams, @HeightCm, @WidthCm, @LengthCm,
                 @SeoTitle, @SeoDescription, @IsActive, 0, @CreatedAt, @UpdatedAt)
            """,
            Parameters(product),
            cancellationToken));

        await SaveVariantsAsync(product, cancellationToken);
    }

    public async Task<bool> UpdateAsync(Product product, CancellationToken cancellationToken)
    {
        var rows = await unitOfWork.Connection.ExecuteAsync(Command(
            """
            UPDATE catalog.products
               SET category_id = @CategoryId, name = @Name, slug = @Slug, description = @Description, base_price = @BasePrice,
                   weight_grams = @WeightGrams, height_cm = @HeightCm, width_cm = @WidthCm, length_cm = @LengthCm,
                   seo_title = @SeoTitle, seo_description = @SeoDescription, is_active = @IsActive,
                   updated_at = @UpdatedAt, version = version + 1
             WHERE id = @Id AND version = @Version
            """,
            Parameters(product),
            cancellationToken));

        if (rows != 1)
        {
            return false;
        }

        await SaveVariantsAsync(product, cancellationToken);
        return true;
    }

    /// <summary>
    /// Insere as novas e atualiza as existentes; variação nunca é apagada. Toda variação nasce com saldo zero em
    /// <c>inventory.stock_items</c>: a vitrine lê o saldo dali e a entrada de estoque chega na Fase 3 (RF-052).
    /// </summary>
    private async Task SaveVariantsAsync(Product product, CancellationToken cancellationToken)
    {
        if (product.Variants.Count == 0)
        {
            return;
        }

        var parameters = product.Variants.Select(variant => new
        {
            Id = variant.Id.Value,
            ProductId = product.Id.Value,
            ColorId = variant.ColorId?.Value,
            SizeId = variant.SizeId?.Value,
            variant.Sku,
            Barcode = variant.Barcode?.Value,
            variant.Price,
            WeightGrams = variant.Package?.WeightGrams,
            HeightCm = variant.Package?.HeightCm,
            WidthCm = variant.Package?.WidthCm,
            LengthCm = variant.Package?.LengthCm,
            variant.IsActive,
            CreatedAt = variant.CreatedAtUtc,
            UpdatedAt = variant.UpdatedAtUtc,
        }).ToList();

        await unitOfWork.Connection.ExecuteAsync(Command(
            """
            INSERT INTO catalog.product_variants
                (id, product_id, color_id, size_id, sku, barcode, price, weight_grams, height_cm, width_cm, length_cm,
                 is_active, created_at, updated_at)
            VALUES
                (@Id, @ProductId, @ColorId, @SizeId, @Sku, @Barcode, @Price, @WeightGrams, @HeightCm, @WidthCm, @LengthCm,
                 @IsActive, @CreatedAt, @UpdatedAt)
            ON CONFLICT (id) DO UPDATE
               SET sku = EXCLUDED.sku, barcode = EXCLUDED.barcode, price = EXCLUDED.price,
                   weight_grams = EXCLUDED.weight_grams, height_cm = EXCLUDED.height_cm,
                   width_cm = EXCLUDED.width_cm, length_cm = EXCLUDED.length_cm,
                   is_active = EXCLUDED.is_active, updated_at = EXCLUDED.updated_at;

            INSERT INTO inventory.stock_items (variant_id, quantity_on_hand, updated_at)
            VALUES (@Id, 0, @UpdatedAt)
            ON CONFLICT (variant_id) DO NOTHING;
            """,
            parameters,
            cancellationToken));
    }

    private static ProductVariant ToVariant(VariantRow row) =>
        ProductVariant.Rehydrate(
            new VariantId(row.Id),
            row.ColorId is { } colorId ? new ColorId(colorId) : null,
            row.SizeId is { } sizeId ? new SizeId(sizeId) : null,
            row.Sku,
            row.Barcode is null ? null : Gtin.Rehydrate(row.Barcode),
            row.Price,
            row.WeightGrams is { } weight && row.HeightCm is { } height && row.WidthCm is { } width && row.LengthCm is { } length
                ? new PackageSize(weight, height, width, length)
                : null,
            row.IsActive,
            row.CreatedAt,
            row.UpdatedAt);

    private Task<bool> Exists(string sql, object parameters, CancellationToken cancellationToken) =>
        unitOfWork.Connection.ExecuteScalarAsync<bool>(Command(sql, parameters, cancellationToken));

    private static object Parameters(Product product) => new
    {
        Id = product.Id.Value,
        CategoryId = product.CategoryId.Value,
        product.Name,
        Slug = product.Slug.Value,
        product.Description,
        product.BasePrice,
        product.Package.WeightGrams,
        product.Package.HeightCm,
        product.Package.WidthCm,
        product.Package.LengthCm,
        product.SeoTitle,
        product.SeoDescription,
        product.IsActive,
        product.Version,
        CreatedAt = product.CreatedAtUtc,
        UpdatedAt = product.UpdatedAtUtc,
    };

    private CommandDefinition Command(string sql, object parameters, CancellationToken cancellationToken) =>
        new(sql, parameters, unitOfWork.Transaction, cancellationToken: cancellationToken);

    private sealed class ProductRow
    {
        public Guid Id { get; init; }
        public Guid CategoryId { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Slug { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public decimal BasePrice { get; init; }
        public int WeightGrams { get; init; }
        public decimal HeightCm { get; init; }
        public decimal WidthCm { get; init; }
        public decimal LengthCm { get; init; }
        public string? SeoTitle { get; init; }
        public string? SeoDescription { get; init; }
        public bool IsActive { get; init; }
        public int Version { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime UpdatedAt { get; init; }
    }

    private sealed class VariantRow
    {
        public Guid Id { get; init; }
        public Guid? ColorId { get; init; }
        public Guid? SizeId { get; init; }
        public string Sku { get; init; } = string.Empty;
        public string? Barcode { get; init; }
        public decimal? Price { get; init; }
        public int? WeightGrams { get; init; }
        public decimal? HeightCm { get; init; }
        public decimal? WidthCm { get; init; }
        public decimal? LengthCm { get; init; }
        public bool IsActive { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime UpdatedAt { get; init; }
    }
}
