using System.Text.RegularExpressions;
using ECommerce.Domain.Abstractions;

namespace ECommerce.Domain.Catalog;

public readonly record struct ColorId(Guid Value)
{
    public static ColorId New() => new(Guid.CreateVersion7());
}

public readonly record struct SizeId(Guid Value)
{
    public static SizeId New() => new(Guid.CreateVersion7());
}

/// <summary>
/// Valor do primeiro eixo da grade (RF-057): cor de bolsa ou acabamento de semijoia ("Dourado", "Ródio").
/// O <see cref="Hex"/> pinta a amostra; o nome é sempre escrito ao lado (WCAG, arquivo 26 §2.4).
/// </summary>
public sealed partial class Color : Entity<ColorId>
{
    public const int NameMaxLength = 60;

    private Color()
    {
    }

    public string Name { get; private set; } = string.Empty;
    public Slug Slug { get; private set; } = null!;
    public string Hex { get; private set; } = string.Empty;

    public static Result<Color> Create(string name, string hex)
    {
        var color = new Color { Id = ColorId.New() };
        var applied = color.Update(name, hex);
        return applied.IsSuccess ? color : applied.Error;
    }

    public static Color Rehydrate(ColorId id, string name, Slug slug, string hex) => new() { Id = id, Name = name, Slug = slug, Hex = hex };

    /// <summary>O slug sai do nome: é o valor do filtro na URL da loja (<c>?cor=dourado</c>).</summary>
    public Result Update(string name, string hex)
    {
        var trimmed = name.Trim();
        var slug = Slug.FromText(trimmed);
        if (trimmed.Length is 0 or > NameMaxLength || slug.IsFailure)
        {
            return Result.Failure(CatalogDomainErrors.InvalidName);
        }

        if (!HexFormat().IsMatch(hex))
        {
            return Result.Failure(CatalogDomainErrors.InvalidHex);
        }

        Name = trimmed;
        Slug = slug.Value;
        Hex = hex.ToUpperInvariant();
        return Result.Success();
    }

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex HexFormat();
}

/// <summary>Valor do segundo eixo da grade (RF-057): "Aro 16", "45 cm", "M". A ordem define a sequência na tela.</summary>
public sealed class Size : Entity<SizeId>
{
    public const int NameMaxLength = 30;

    private Size()
    {
    }

    public string Name { get; private set; } = string.Empty;
    public short SortOrder { get; private set; }

    public static Result<Size> Create(string name, short sortOrder)
    {
        var size = new Size { Id = SizeId.New() };
        var applied = size.Update(name, sortOrder);
        return applied.IsSuccess ? size : applied.Error;
    }

    public static Size Rehydrate(SizeId id, string name, short sortOrder) => new() { Id = id, Name = name, SortOrder = sortOrder };

    public Result Update(string name, short sortOrder)
    {
        var trimmed = name.Trim();
        if (trimmed.Length is 0 or > NameMaxLength)
        {
            return Result.Failure(CatalogDomainErrors.InvalidName);
        }

        Name = trimmed;
        SortOrder = sortOrder;
        return Result.Success();
    }
}

public interface IColorRepository
{
    Task<Color?> GetByIdAsync(ColorId id, CancellationToken cancellationToken);
    Task<bool> NameOrSlugInUseAsync(string name, Slug slug, ColorId? exceptId, CancellationToken cancellationToken);
    Task<bool> IsUsedAsync(ColorId id, CancellationToken cancellationToken);
    Task AddAsync(Color color, CancellationToken cancellationToken);
    Task UpdateAsync(Color color, CancellationToken cancellationToken);
    Task DeleteAsync(ColorId id, CancellationToken cancellationToken);
}

public interface ISizeRepository
{
    Task<Size?> GetByIdAsync(SizeId id, CancellationToken cancellationToken);
    Task<bool> NameInUseAsync(string name, SizeId? exceptId, CancellationToken cancellationToken);
    Task<bool> IsUsedAsync(SizeId id, CancellationToken cancellationToken);
    Task AddAsync(Size size, CancellationToken cancellationToken);
    Task UpdateAsync(Size size, CancellationToken cancellationToken);
    Task DeleteAsync(SizeId id, CancellationToken cancellationToken);
}
