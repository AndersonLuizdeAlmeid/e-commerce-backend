using Dapper;
using ECommerce.Application.Abstractions.Caching;
using ECommerce.Application.Abstractions.Data;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Domain.Abstractions;

namespace ECommerce.Application.Catalog.GetCollections;

/// <summary>RF-024 — coleções ativas, na ordem da navegação.</summary>
public sealed record ListCollectionsQuery : IQuery<IReadOnlyList<CollectionSummaryResponse>>;

public sealed record CollectionSummaryResponse(Guid Id, string Name, string Slug, string CardTheme);

/// <summary>RF-024 CA2: coleção inativa ou inexistente → 404.</summary>
public sealed record GetCollectionBySlugQuery(string Slug) : IQuery<CollectionDetailsResponse>;

/// <summary>RF-024 CA4 — slug atual de uma coleção ativa a partir de um slug antigo; a API responde 301.</summary>
public sealed record ResolveCollectionSlugQuery(string OldSlug) : IQuery<string>;

public sealed record CollectionDetailsResponse(
    Guid Id,
    string Name,
    string Slug,
    string Description,
    SeoResponse Seo,
    string? ImageUrl,
    string CardTheme);

internal sealed class ListCollectionsHandler(IDbConnectionFactory connectionFactory, ICacheService cache)
    : IQueryHandler<ListCollectionsQuery, IReadOnlyList<CollectionSummaryResponse>>
{
    private const string Sql = """
        SELECT id, name, slug, card_theme
          FROM catalog.collections
         WHERE is_active
         ORDER BY position, name
        """;

    public async Task<Result<IReadOnlyList<CollectionSummaryResponse>>> Handle(ListCollectionsQuery query, CancellationToken cancellationToken)
    {
        var collections = await cache.GetOrCreateAsync(CatalogCache.CollectionsKey, LoadAsync, CatalogCache.Collections, cancellationToken);
        return Result.Success(collections);
    }

    private async ValueTask<IReadOnlyList<CollectionSummaryResponse>> LoadAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<Row>(new CommandDefinition(Sql, cancellationToken: cancellationToken));
        return rows.Select(r => new CollectionSummaryResponse(r.Id, r.Name, r.Slug, r.CardTheme)).ToList();
    }

    private sealed class Row
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Slug { get; init; } = string.Empty;
        public string CardTheme { get; init; } = string.Empty;
    }
}

internal sealed class GetCollectionBySlugHandler(IDbConnectionFactory connectionFactory, ICacheService cache)
    : IQueryHandler<GetCollectionBySlugQuery, CollectionDetailsResponse>
{
    private const string Sql = """
        SELECT id, name, slug, description, seo_title, seo_description, image_url, card_theme
          FROM catalog.collections
         WHERE slug = @Slug AND is_active
        """;

    public async Task<Result<CollectionDetailsResponse>> Handle(GetCollectionBySlugQuery query, CancellationToken cancellationToken)
    {
        var collection = await cache.GetOrCreateAsync(
            CatalogCache.CollectionKey(query.Slug), ct => LoadAsync(query.Slug, ct), CatalogCache.Collections, cancellationToken);

        return collection is null ? CatalogErrors.CollectionNotFound : collection;
    }

    private async ValueTask<CollectionDetailsResponse?> LoadAsync(string slug, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(Sql, new { Slug = slug }, cancellationToken: cancellationToken));

        return row is null
            ? null
            : new CollectionDetailsResponse(
                row.Id, row.Name, row.Slug, row.Description,
                SeoDefaults.Build(row.SeoTitle, row.SeoDescription, row.Name, row.Description),
                row.ImageUrl, row.CardTheme);
    }

    private sealed class Row
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Slug { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string? SeoTitle { get; init; }
        public string? SeoDescription { get; init; }
        public string? ImageUrl { get; init; }
        public string CardTheme { get; init; } = string.Empty;
    }
}

internal sealed class ResolveCollectionSlugHandler(IDbConnectionFactory connectionFactory) : IQueryHandler<ResolveCollectionSlugQuery, string>
{
    private const string Sql = """
        SELECT c.slug
          FROM catalog.slug_history h
          JOIN catalog.collections c ON c.id = h.entity_id
         WHERE h.old_slug = @Slug AND h.entity_type = 'Collection' AND c.is_active
        """;

    public async Task<Result<string>> Handle(ResolveCollectionSlugQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var slug = await connection.QuerySingleOrDefaultAsync<string>(
            new CommandDefinition(Sql, new { Slug = query.OldSlug }, cancellationToken: cancellationToken));

        return slug is null ? CatalogErrors.CollectionNotFound : slug;
    }
}
