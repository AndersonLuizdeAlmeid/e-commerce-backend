using ECommerce.Application.Abstractions.Messaging;

namespace ECommerce.Application.Catalog.GetSitemap;

/// <summary>SEO-06 — slugs e última alteração de categorias, coleções e produtos visíveis, consumidos pelo sitemap.xml do Next.js.</summary>
public sealed record GetSitemapQuery : IQuery<SitemapResponse>;

public sealed record SitemapResponse(IReadOnlyList<SitemapEntryResponse> Categories, IReadOnlyList<SitemapEntryResponse> Products, IReadOnlyList<SitemapEntryResponse> Collections);

public sealed record SitemapEntryResponse(string Slug, DateTime UpdatedAt);
