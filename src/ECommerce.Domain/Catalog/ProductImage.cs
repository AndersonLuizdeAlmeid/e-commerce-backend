using ECommerce.Domain.Abstractions;

namespace ECommerce.Domain.Catalog;

public readonly record struct ProductImageId(Guid Value)
{
    public static ProductImageId New() => new(Guid.CreateVersion7());
}

/// <summary>
/// Foto do produto (RF-061). <see cref="ColorId"/> nulo vale para todos os acabamentos; a de menor
/// <see cref="Position"/> em cada acabamento é a capa dele.
/// </summary>
public sealed class ProductImage : Entity<ProductImageId>
{
    public const int AltMaxLength = 200;

    private ProductImage()
    {
    }

    public ProductId ProductId { get; private set; }
    public ColorId? ColorId { get; private set; }
    public string Url { get; private set; } = string.Empty;

    /// <summary>Nulo em fotos antigas do seed, que não estão no bucket.</summary>
    public string? StorageKey { get; private set; }

    public string Alt { get; private set; } = string.Empty;
    public short Position { get; private set; }
    public int? Width { get; private set; }
    public int? Height { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    public static Result<ProductImage> Create(
        ProductImageId id,
        ProductId productId,
        ColorId? colorId,
        string url,
        string storageKey,
        string alt,
        short position,
        int width,
        int height,
        DateTime nowUtc)
    {
        var image = new ProductImage
        {
            Id = id,
            ProductId = productId,
            ColorId = colorId,
            Url = url,
            StorageKey = storageKey,
            Position = position,
            Width = width,
            Height = height,
            CreatedAtUtc = nowUtc,
        };
        var applied = image.SetAlt(alt);
        return applied.IsSuccess ? image : applied.Error;
    }

    public static ProductImage Rehydrate(
        ProductImageId id,
        ProductId productId,
        ColorId? colorId,
        string url,
        string? storageKey,
        string alt,
        short position,
        int? width,
        int? height,
        DateTime createdAtUtc) =>
        new()
        {
            Id = id,
            ProductId = productId,
            ColorId = colorId,
            Url = url,
            StorageKey = storageKey,
            Alt = alt,
            Position = position,
            Width = width,
            Height = height,
            CreatedAtUtc = createdAtUtc,
        };

    public Result SetAlt(string alt)
    {
        var trimmed = alt.Trim();
        if (trimmed.Length is 0 or > AltMaxLength)
        {
            return Result.Failure(CatalogDomainErrors.InvalidImageAlt);
        }

        Alt = trimmed;
        return Result.Success();
    }

    /// <summary>Muda de acabamento e entra no fim da galeria dele.</summary>
    public void MoveToColor(ColorId? colorId, short position)
    {
        ColorId = colorId;
        Position = position;
    }
}

public interface IProductImageRepository
{
    Task<ProductImage?> GetByIdAsync(ProductImageId id, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProductImage>> ListForGalleryAsync(ProductId productId, ColorId? colorId, CancellationToken cancellationToken);
    Task<int> CountForProductAsync(ProductId productId, CancellationToken cancellationToken);
    Task<short> NextPositionAsync(ProductId productId, ColorId? colorId, CancellationToken cancellationToken);
    Task AddAsync(ProductImage image, CancellationToken cancellationToken);
    Task UpdateAsync(ProductImage image, CancellationToken cancellationToken);
    Task DeleteAsync(ProductImageId id, CancellationToken cancellationToken);

    /// <summary>Grava a nova ordem da galeria: a posição de cada foto é o índice dela na lista.</summary>
    Task ReorderAsync(ProductId productId, IReadOnlyList<ProductImageId> orderedIds, CancellationToken cancellationToken);
}
