using System.Security.Cryptography;
using System.Text;
using ECommerce.Application.Abstractions.Caching;

namespace ECommerce.Application.Catalog;

/// <summary>Chaves, tags e TTLs do catálogo (arquivo 12 §3).</summary>
public static class CatalogCache
{
    public const string CategoriesTag = "categories";
    public const string ProductsTag = "products";

    public const string CategoriesKey = "catalog:categories";
    public const string SitemapKey = "catalog:sitemap";

    internal static readonly CachePolicy Categories = new(TimeSpan.FromMinutes(30), [CategoriesTag]);
    internal static readonly CachePolicy ProductList = new(TimeSpan.FromMinutes(2), [ProductsTag]);
    internal static readonly CachePolicy Filters = new(TimeSpan.FromMinutes(10), [ProductsTag]);
    internal static readonly CachePolicy Sitemap = new(TimeSpan.FromMinutes(10), [ProductsTag, CategoriesTag]);
    internal static readonly CachePolicy Product = new(TimeSpan.FromMinutes(10), [ProductsTag]);

    internal static string CategoryKey(string slug) => $"catalog:category:{slug}";
    internal static string ProductKey(string slug) => $"catalog:product:{slug}";
    internal static string FiltersKey(string? category) => $"catalog:filters:{category ?? "*"}";

    /// <summary>catalog:products:{hash(filtros)} — os filtros já chegam normalizados.</summary>
    internal static string ProductListKey(string normalizedFilters) =>
        "catalog:products:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedFilters)), 0, 16);
}
