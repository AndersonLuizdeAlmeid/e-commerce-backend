using ECommerce.Application.Abstractions.Messaging;

namespace ECommerce.Application.Catalog.GetCategoryBySlug;

/// <summary>Dados da categoria ativa (texto indexável e SEO — SEO-03, SEO-10).</summary>
public sealed record GetCategoryBySlugQuery(string Slug) : IQuery<CategoryDetailsResponse>;

/// <summary>RN-025 — slug atual de uma categoria ativa a partir de um slug antigo; a API responde 301.</summary>
public sealed record ResolveCategorySlugQuery(string OldSlug) : IQuery<string>;

public sealed record CategoryDetailsResponse(
    Guid Id,
    string Name,
    string Slug,
    string Description,
    SeoResponse Seo,
    VariantLabelsResponse VariantLabels);
