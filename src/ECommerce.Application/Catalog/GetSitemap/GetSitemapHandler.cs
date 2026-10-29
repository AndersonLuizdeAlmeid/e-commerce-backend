using Dapper;
using ECommerce.Application.Abstractions.Caching;
using ECommerce.Application.Abstractions.Data;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Domain.Abstractions;

namespace ECommerce.Application.Catalog.GetSitemap;

internal sealed class GetSitemapHandler(IDbConnectionFactory connectionFactory, ICacheService cache)
    : IQueryHandler<GetSitemapQuery, SitemapResponse>
{
    private const string Sql = """
        SELECT slug, updated_at
          FROM catalog.categories
         WHERE is_active
         ORDER BY slug;

        SELECT p.slug, p.updated_at
          FROM catalog.products p
          JOIN catalog.categories c ON c.id = p.category_id AND c.is_active
         WHERE p.is_active
           AND EXISTS (SELECT 1 FROM catalog.product_variants v WHERE v.product_id = p.id AND v.is_active)
         ORDER BY p.slug;
        """;

    public async Task<Result<SitemapResponse>> Handle(GetSitemapQuery query, CancellationToken cancellationToken)
    {
        var sitemap = await cache.GetOrCreateAsync(CatalogCache.SitemapKey, LoadAsync, CatalogCache.Sitemap, cancellationToken);
        return sitemap;
    }

    private async ValueTask<SitemapResponse> LoadAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        using var grid = await connection.QueryMultipleAsync(new CommandDefinition(Sql, cancellationToken: cancellationToken));

        var categories = (await grid.ReadAsync<EntryRow>()).Select(ToResponse).ToList();
        var products = (await grid.ReadAsync<EntryRow>()).Select(ToResponse).ToList();

        return new SitemapResponse(categories, products);
    }

    private static SitemapEntryResponse ToResponse(EntryRow row) =>
        new(row.Slug, DateTime.SpecifyKind(row.UpdatedAt, DateTimeKind.Utc));

    private sealed class EntryRow
    {
        public string Slug { get; init; } = string.Empty;
        public DateTime UpdatedAt { get; init; }
    }
}
