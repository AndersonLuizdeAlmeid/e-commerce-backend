using System.Security.Cryptography;
using System.Text;
using ECommerce.Application.Abstractions.Caching;

namespace ECommerce.Application.Catalog;

/// <summary>Chaves, tags e TTLs do catálogo (arquivo 12 §3).</summary>
public static class CatalogCache
{
    public const string CategoriesTag = "categories";
    public const string ProductsTag = "products";
    public const string CollectionsTag = "collections";
    public const string ShowcaseTag = "showcase";

    public const string CategoriesKey = "catalog:categories";
    public const string SitemapKey = "catalog:sitemap";
    public const string CollectionsKey = "catalog:collections";
    public const string ShowcaseKey = "catalog:showcase";

    internal static readonly CachePolicy Categories = new(TimeSpan.FromMinutes(30), [CategoriesTag]);
    internal static readonly CachePolicy ProductList = new(TimeSpan.FromMinutes(2), [ProductsTag]);
    internal static readonly CachePolicy Filters = new(TimeSpan.FromMinutes(10), [ProductsTag]);
    internal static readonly CachePolicy Sitemap = new(TimeSpan.FromMinutes(10), [ProductsTag, CategoriesTag, CollectionsTag]);
    internal static readonly CachePolicy Collections = new(TimeSpan.FromMinutes(30), [CollectionsTag]);

    // TTL curto: é o que faz a ocasião entrar e sair na virada do dia, sem job (RF-025 CA3).
    internal static readonly CachePolicy Showcase = new(TimeSpan.FromMinutes(5), [ShowcaseTag, ProductsTag, CategoriesTag]);
    internal static readonly CachePolicy Product = new(TimeSpan.FromMinutes(10), [ProductsTag]);

    internal static string CategoryKey(string slug) => $"catalog:category:{slug}";
    internal static string ProductKey(string slug) => $"catalog:product:{slug}";
    internal static string CollectionKey(string slug) => $"catalog:collection:{slug}";
    internal static string FiltersKey(string? category, string? collection) => $"catalog:filters:{category ?? "*"}:{collection ?? "*"}";

    /// <summary>catalog:products:{hash(filtros)} — os filtros já chegam normalizados.</summary>
    internal static string ProductListKey(string normalizedFilters) =>
        "catalog:products:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedFilters)), 0, 16);
}
