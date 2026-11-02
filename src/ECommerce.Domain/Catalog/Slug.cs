using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ECommerce.Domain.Abstractions;

namespace ECommerce.Domain.Catalog;

/// <summary>Parte da URL: minúsculas, dígitos e hífen, sem acento (arquivo 24, SEO-01). Ex.: "anel-solitario-zirconia".</summary>
public sealed partial record Slug
{
    public const int MaxLength = 120;

    private Slug(string value) => Value = value;

    public string Value { get; }

    public static Result<Slug> Create(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length is > 0 and <= MaxLength && Format().IsMatch(normalized)
            ? new Slug(normalized)
            : CatalogDomainErrors.InvalidSlug;
    }

    /// <summary>"Anel Solitário Zircônia" → "anel-solitario-zirconia"; usado quando o admin não informa o slug.</summary>
    public static Result<Slug> FromText(string? text)
    {
        var decomposed = (text ?? string.Empty).Normalize(NormalizationForm.FormD);
        var withoutAccents = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                withoutAccents.Append(character);
            }
        }

        var slug = NonAlphanumeric().Replace(withoutAccents.ToString().ToLowerInvariant(), "-").Trim('-');
        return Create(slug.Length > MaxLength ? slug[..MaxLength].TrimEnd('-') : slug);
    }

    /// <summary>Uso exclusivo da Infrastructure, para valores já validados no banco.</summary>
    public static Slug Rehydrate(string value) => new(value);

    public override string ToString() => Value;

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex Format();

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonAlphanumeric();
}
