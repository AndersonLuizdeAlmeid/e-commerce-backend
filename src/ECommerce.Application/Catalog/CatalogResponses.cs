namespace ECommerce.Application.Catalog;

public sealed record SeoResponse(string Title, string Description);

public sealed record ColorResponse(Guid Id, string Name, string Slug, string Hex);

public sealed record SizeResponse(Guid Id, string Name);

public sealed record ProductImageResponse(string Url, string Alt, Guid? ColorId);

/// <summary>Rótulos da grade já resolvidos: os da categoria, senão os da mãe, senão "Cor"/"Tamanho" (RF-050, arquivo 26 ADR-F07).</summary>
public sealed record VariantLabelsResponse(string Color, string Size)
{
    /// <summary>Colunas <c>color_label</c> e <c>size_label</c> para um SELECT com <c>c</c> = categoria e <c>parent</c> = mãe.</summary>
    internal const string SqlColumns =
        "coalesce(c.color_label, parent.color_label, 'Cor') AS color_label, coalesce(c.size_label, parent.size_label, 'Tamanho') AS size_label";

    internal const string SqlParentJoin = "LEFT JOIN catalog.categories parent ON parent.id = c.parent_id";
}
