using ECommerce.Application.Abstractions.Messaging;

namespace ECommerce.Application.Catalog.GetCategoryBySlug;

/// <summary>Dados da categoria ativa (texto indexável e SEO — SEO-03, SEO-10).</summary>
public sealed record GetCategoryBySlugQuery(string Slug) : IQuery<CategoryDetailsResponse>;

public sealed record CategoryDetailsResponse(
    Guid Id,
    string Name,
    string Slug,
    string Description,
    SeoResponse Seo);
