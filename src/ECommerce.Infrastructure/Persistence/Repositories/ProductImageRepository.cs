using Dapper;
using ECommerce.Application.Abstractions.Data;
using ECommerce.Domain.Catalog;

namespace ECommerce.Infrastructure.Persistence.Repositories;

internal sealed class ProductImageRepository(IUnitOfWork unitOfWork) : IProductImageRepository
{
    private const string SelectColumns =
        "SELECT id, product_id, color_id, url, storage_key, alt, position, width, height, created_at FROM catalog.product_images";

    public async Task<ProductImage?> GetByIdAsync(ProductImageId id, CancellationToken cancellationToken)
    {
        var row = await unitOfWork.Connection.QuerySingleOrDefaultAsync<ImageRow>(
            Command($"{SelectColumns} WHERE id = @Id", new { Id = id.Value }, cancellationToken));
        return row is null ? null : ToImage(row);
    }

    public async Task<IReadOnlyList<ProductImage>> ListForGalleryAsync(ProductId productId, ColorId? colorId, CancellationToken cancellationToken)
    {
        var rows = await unitOfWork.Connection.QueryAsync<ImageRow>(Command(
            $"{SelectColumns} WHERE product_id = @ProductId AND color_id IS NOT DISTINCT FROM @ColorId::uuid ORDER BY position",
            new { ProductId = productId.Value, ColorId = colorId?.Value },
            cancellationToken));
        return rows.Select(ToImage).ToList();
    }

    public Task<int> CountForProductAsync(ProductId productId, CancellationToken cancellationToken) =>
        unitOfWork.Connection.ExecuteScalarAsync<int>(Command(
            "SELECT count(*)::int FROM catalog.product_images WHERE product_id = @ProductId", new { ProductId = productId.Value }, cancellationToken));

    public Task<short> NextPositionAsync(ProductId productId, ColorId? colorId, CancellationToken cancellationToken) =>
        unitOfWork.Connection.ExecuteScalarAsync<short>(Command(
            """
            SELECT coalesce(max(position) + 1, 0)::smallint
              FROM catalog.product_images
             WHERE product_id = @ProductId AND color_id IS NOT DISTINCT FROM @ColorId::uuid
            """,
            new { ProductId = productId.Value, ColorId = colorId?.Value },
            cancellationToken));

    public Task AddAsync(ProductImage image, CancellationToken cancellationToken) =>
        unitOfWork.Connection.ExecuteAsync(Command(
            """
            INSERT INTO catalog.product_images (id, product_id, color_id, url, storage_key, alt, position, width, height, created_at)
            VALUES (@Id, @ProductId, @ColorId, @Url, @StorageKey, @Alt, @Position, @Width, @Height, @CreatedAt);

            UPDATE catalog.products SET updated_at = @CreatedAt WHERE id = @ProductId;
            """,
            new
            {
                Id = image.Id.Value,
                ProductId = image.ProductId.Value,
                ColorId = image.ColorId?.Value,
                image.Url,
                image.StorageKey,
                image.Alt,
                image.Position,
                image.Width,
                image.Height,
                CreatedAt = image.CreatedAtUtc,
            },
            cancellationToken));

    public Task UpdateAsync(ProductImage image, CancellationToken cancellationToken) =>
        unitOfWork.Connection.ExecuteAsync(Command(
            """
            UPDATE catalog.product_images SET alt = @Alt, color_id = @ColorId, position = @Position WHERE id = @Id;
            UPDATE catalog.products SET updated_at = now() WHERE id = @ProductId;
            """,
            new { Id = image.Id.Value, ProductId = image.ProductId.Value, image.Alt, ColorId = image.ColorId?.Value, image.Position },
            cancellationToken));

    public Task DeleteAsync(ProductImageId id, CancellationToken cancellationToken) =>
        unitOfWork.Connection.ExecuteAsync(Command(
            """
            UPDATE catalog.products SET updated_at = now()
             WHERE id = (SELECT product_id FROM catalog.product_images WHERE id = @Id);
            DELETE FROM catalog.product_images WHERE id = @Id;
            """,
            new { Id = id.Value },
            cancellationToken));

    /// <summary>Em duas etapas: a posição é única por produto + cor, e a troca direta colidiria no meio do caminho (RF-061 CA5).</summary>
    public async Task ReorderAsync(ProductId productId, IReadOnlyList<ProductImageId> orderedIds, CancellationToken cancellationToken)
    {
        var ids = orderedIds.Select(id => id.Value).ToArray();
        await unitOfWork.Connection.ExecuteAsync(Command(
            """
            UPDATE catalog.product_images SET position = -1 - position WHERE id = ANY (@Ids);

            UPDATE catalog.product_images i
               SET position = (o.ordinality - 1)::smallint
              FROM unnest(@Ids) WITH ORDINALITY AS o(id, ordinality)
             WHERE i.id = o.id;

            UPDATE catalog.products SET updated_at = now() WHERE id = @ProductId;
            """,
            new { Ids = ids, ProductId = productId.Value },
            cancellationToken));
    }

    private static ProductImage ToImage(ImageRow row) =>
        ProductImage.Rehydrate(
            new ProductImageId(row.Id),
            new ProductId(row.ProductId),
            row.ColorId is { } colorId ? new ColorId(colorId) : null,
            row.Url,
            row.StorageKey,
            row.Alt,
            row.Position,
            row.Width,
            row.Height,
            row.CreatedAt);

    private CommandDefinition Command(string sql, object parameters, CancellationToken cancellationToken) =>
        new(sql, parameters, unitOfWork.Transaction, cancellationToken: cancellationToken);

    private sealed class ImageRow
    {
        public Guid Id { get; init; }
        public Guid ProductId { get; init; }
        public Guid? ColorId { get; init; }
        public string Url { get; init; } = string.Empty;
        public string? StorageKey { get; init; }
        public string Alt { get; init; } = string.Empty;
        public short Position { get; init; }
        public int? Width { get; init; }
        public int? Height { get; init; }
        public DateTime CreatedAt { get; init; }
    }
}
