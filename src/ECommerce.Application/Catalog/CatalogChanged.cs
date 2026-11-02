using ECommerce.Application.Abstractions.Outbox;

namespace ECommerce.Application.Catalog;

/// <summary>
/// Mensagem do Outbox <see cref="Type"/> gravada por toda escrita do admin no catálogo. Depois do commit, o
/// processador invalida o HybridCache por tag e pede ao Next.js para revalidar as páginas com as mesmas tags
/// (arquivo 12 §4, arquivo 24; entrega 2B §3.2). As tags da API e do Next têm os mesmos nomes de propósito.
/// </summary>
public sealed record CatalogChanged(IReadOnlyList<string> Tags)
{
    public const string Type = "catalog.changed";

    internal static Task CategoriesAsync(IOutbox outbox, CancellationToken cancellationToken) =>
        outbox.EnqueueAsync(Type, new CatalogChanged([CatalogCache.CategoriesTag, CatalogCache.ProductsTag]), cancellationToken);

    internal static Task ProductsAsync(IOutbox outbox, CancellationToken cancellationToken) =>
        outbox.EnqueueAsync(Type, new CatalogChanged([CatalogCache.ProductsTag]), cancellationToken);

    /// <summary>Quem está na coleção muda as listagens, então a tag de produtos vai junto.</summary>
    internal static Task CollectionsAsync(IOutbox outbox, CancellationToken cancellationToken) =>
        outbox.EnqueueAsync(Type, new CatalogChanged([CatalogCache.CollectionsTag, CatalogCache.ProductsTag]), cancellationToken);

    /// <summary>RF-025 CA5: destaque ou ocasião mudou; a home é revalidada.</summary>
    internal static Task ShowcaseAsync(IOutbox outbox, CancellationToken cancellationToken) =>
        outbox.EnqueueAsync(Type, new CatalogChanged([CatalogCache.ShowcaseTag]), cancellationToken);

    /// <summary>Uma variação esgotou ou voltou: mudam as listagens, o produto e, se for a peça em destaque, a home.</summary>
    internal static Task AvailabilityAsync(IOutbox outbox, CancellationToken cancellationToken) =>
        outbox.EnqueueAsync(Type, new CatalogChanged([CatalogCache.ProductsTag, CatalogCache.ShowcaseTag]), cancellationToken);
}

/// <summary>Valores de <c>catalog.slug_history.entity_type</c>.</summary>
public static class SlugEntityTypes
{
    public const string Category = "Category";
    public const string Product = "Product";
    public const string Collection = "Collection";
}
