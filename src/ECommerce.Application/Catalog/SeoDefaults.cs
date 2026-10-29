namespace ECommerce.Application.Catalog;

/// <summary>SEO-03: sem título/descrição próprios, gera um padrão a partir do conteúdo (limites do VO SeoMetadata).</summary>
internal static class SeoDefaults
{
    public const int TitleMaxLength = 70;
    public const int DescriptionMaxLength = 160;

    public static SeoResponse Build(string? seoTitle, string? seoDescription, string name, string description) =>
        new(
            string.IsNullOrWhiteSpace(seoTitle) ? Truncate(name, TitleMaxLength) : seoTitle,
            string.IsNullOrWhiteSpace(seoDescription) ? Truncate(description, DescriptionMaxLength) : seoDescription);

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..(maxLength - 1)].TrimEnd() + "…";
}
