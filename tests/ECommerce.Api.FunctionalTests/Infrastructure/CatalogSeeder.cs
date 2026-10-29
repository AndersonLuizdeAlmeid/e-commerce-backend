using Dapper;
using Npgsql;

namespace ECommerce.Api.FunctionalTests.Infrastructure;

/// <summary>Monta o catálogo direto no banco (a escrita pela API só chega com o admin, na Fase 2).</summary>
public sealed class CatalogSeeder(string connectionString)
{
    public async Task<Guid> CategoryAsync(string name, string slug, bool isActive = true, string description = "")
    {
        var id = Guid.CreateVersion7();
        await ExecuteAsync(
            """
            INSERT INTO catalog.categories (id, name, slug, description, is_active, created_at, updated_at)
            VALUES (@Id, @Name, @Slug, @Description, @IsActive, now(), now())
            """,
            new { Id = id, Name = name, Slug = slug, Description = description, IsActive = isActive });
        return id;
    }

    public async Task<Guid> ColorAsync(string name, string slug, string hex = "#000000")
    {
        var id = Guid.CreateVersion7();
        await ExecuteAsync(
            "INSERT INTO catalog.colors (id, name, slug, hex) VALUES (@Id, @Name, @Slug, @Hex)",
            new { Id = id, Name = name, Slug = slug, Hex = hex });
        return id;
    }

    public async Task<Guid> SizeAsync(string name, short sortOrder)
    {
        var id = Guid.CreateVersion7();
        await ExecuteAsync(
            "INSERT INTO catalog.sizes (id, name, sort_order) VALUES (@Id, @Name, @SortOrder)",
            new { Id = id, Name = name, SortOrder = sortOrder });
        return id;
    }

    public async Task<Guid> ProductAsync(
        Guid categoryId,
        string name,
        string slug,
        decimal basePrice = 100m,
        bool isActive = true,
        string description = "",
        DateTime? createdAt = null)
    {
        var id = Guid.CreateVersion7();
        await ExecuteAsync(
            """
            INSERT INTO catalog.products
              (id, name, slug, description, category_id, base_price, weight_grams, height_cm, width_cm, length_cm,
               is_active, created_at, updated_at)
            VALUES (@Id, @Name, @Slug, @Description, @CategoryId, @BasePrice, 200, 2, 20, 30,
                    @IsActive, @CreatedAt, @CreatedAt)
            """,
            new
            {
                Id = id,
                Name = name,
                Slug = slug,
                Description = description,
                CategoryId = categoryId,
                BasePrice = basePrice,
                IsActive = isActive,
                CreatedAt = createdAt ?? DateTime.UtcNow,
            });
        return id;
    }

    /// <summary>Cria a variação e o saldo de estoque dela.</summary>
    public async Task<Guid> VariantAsync(
        Guid productId,
        string sku,
        Guid? colorId = null,
        Guid? sizeId = null,
        decimal? price = null,
        bool isActive = true,
        int stock = 10,
        string? barcode = null)
    {
        var id = Guid.CreateVersion7();
        await ExecuteAsync(
            """
            INSERT INTO catalog.product_variants
              (id, product_id, sku, barcode, color_id, size_id, price, is_active, created_at, updated_at)
            VALUES (@Id, @ProductId, @Sku, @Barcode, @ColorId, @SizeId, @Price, @IsActive, now(), now());

            INSERT INTO inventory.stock_items (variant_id, quantity_on_hand, updated_at)
            VALUES (@Id, @Stock, now());
            """,
            new
            {
                Id = id,
                ProductId = productId,
                Sku = sku,
                Barcode = barcode,
                ColorId = colorId,
                SizeId = sizeId,
                Price = price,
                IsActive = isActive,
                Stock = stock,
            });
        return id;
    }

    public Task ImageAsync(Guid productId, string url, string alt, short position, Guid? colorId = null) =>
        ExecuteAsync(
            """
            INSERT INTO catalog.product_images (id, product_id, color_id, url, alt, position)
            VALUES (@Id, @ProductId, @ColorId, @Url, @Alt, @Position)
            """,
            new { Id = Guid.CreateVersion7(), ProductId = productId, ColorId = colorId, Url = url, Alt = alt, Position = position });

    public Task SlugHistoryAsync(string oldSlug, Guid productId) =>
        ExecuteAsync(
            """
            INSERT INTO catalog.slug_history (old_slug, entity_type, entity_id, changed_at)
            VALUES (@OldSlug, 'Product', @ProductId, now())
            """,
            new { OldSlug = oldSlug, ProductId = productId });

    /// <summary>Atalho: produto ativo com uma variação "única" (RN-022) e estoque.</summary>
    public async Task<Guid> SimpleProductAsync(Guid categoryId, string name, string slug, decimal price = 100m, DateTime? createdAt = null)
    {
        var productId = await ProductAsync(categoryId, name, slug, price, createdAt: createdAt);
        await VariantAsync(productId, slug.ToUpperInvariant());
        return productId;
    }

    private async Task ExecuteAsync(string sql, object parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.ExecuteAsync(sql, parameters);
    }
}
