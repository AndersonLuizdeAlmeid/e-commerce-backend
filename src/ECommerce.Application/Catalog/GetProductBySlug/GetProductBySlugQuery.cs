using ECommerce.Application.Abstractions.Messaging;

namespace ECommerce.Application.Catalog.GetProductBySlug;

/// <summary>
/// RF-021 — página do produto. Além do produto em si, resolve o slug (SEO-02):
/// slug antigo → <see cref="ProductMoved"/> (301) e produto fora da loja → <see cref="ProductGone"/> (410).
/// Slug desconhecido é falha <c>Catalog.ProductNotFound</c> (404).
/// </summary>
public sealed record GetProductBySlugQuery(string Slug) : IQuery<ProductBySlugResult>;

public abstract record ProductBySlugResult;

public sealed record ProductFound(ProductDetailsResponse Product) : ProductBySlugResult;

public sealed record ProductMoved(string Slug) : ProductBySlugResult;

/// <summary><paramref name="CategorySlug"/> é nulo quando a categoria também saiu da loja.</summary>
public sealed record ProductGone(string? CategorySlug) : ProductBySlugResult;

public sealed record ProductDetailsResponse(
    Guid Id,
    string Slug,
    string Name,
    string Description,
    ProductCategoryResponse Category,
    decimal PriceFrom,
    SeoResponse Seo,
    IReadOnlyList<ColorResponse> Colors,
    IReadOnlyList<SizeResponse> Sizes,
    IReadOnlyList<ProductVariantResponse> Variants,
    IReadOnlyList<ProductImageResponse> Images,
    DateTime UpdatedAt);

public sealed record ProductCategoryResponse(string Slug, string Name);

/// <summary>CA1: só informa se há estoque, nunca a quantidade. <c>Gtin</c> alimenta o JSON-LD (SEO-05).</summary>
public sealed record ProductVariantResponse(
    Guid Id,
    string Sku,
    Guid? ColorId,
    Guid? SizeId,
    decimal Price,
    string? Gtin,
    bool Available);
