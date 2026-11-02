using Dapper;
using ECommerce.Application.Abstractions.Data;
using ECommerce.Domain.Catalog;

namespace ECommerce.Infrastructure.Persistence.Repositories;

internal sealed class CollectionRepository(IUnitOfWork unitOfWork) : ICollectionRepository
{
    public async Task<Collection?> GetByIdAsync(CollectionId id, CancellationToken cancellationToken)
    {
        var row = await unitOfWork.Connection.QuerySingleOrDefaultAsync<CollectionRow>(Command(
            """
            SELECT id, name, slug, description, seo_title, seo_description, card_theme, position,
                   image_url, image_key, is_active, version, created_at, updated_at
              FROM catalog.collections
             WHERE id = @Id
            """,
            new { Id = id.Value },
            cancellationToken));

        return row is null
            ? null
            : Collection.Rehydrate(
                new CollectionId(row.Id),
                new CollectionContent(
                    row.Name, Slug.Rehydrate(row.Slug), row.Description, row.SeoTitle, row.SeoDescription,
                    Enum.Parse<CardTheme>(row.CardTheme), row.Position),
                row.ImageUrl,
                row.ImageKey,
                row.IsActive,
                row.Version,
                row.CreatedAt,
                row.UpdatedAt);
    }

    public Task<bool> NameInUseAsync(string name, CollectionId? exceptId, CancellationToken cancellationToken) =>
        unitOfWork.Connection.ExecuteScalarAsync<bool>(Command(
            "SELECT EXISTS (SELECT 1 FROM catalog.collections WHERE lower(name) = lower(@Name) AND (@ExceptId::uuid IS NULL OR id <> @ExceptId))",
            new { Name = name, ExceptId = exceptId?.Value },
            cancellationToken));

    public Task<bool> SlugInUseAsync(Slug slug, CollectionId? exceptId, CancellationToken cancellationToken) =>
        unitOfWork.Connection.ExecuteScalarAsync<bool>(Command(
            "SELECT EXISTS (SELECT 1 FROM catalog.collections WHERE slug = @Slug AND (@ExceptId::uuid IS NULL OR id <> @ExceptId))",
            new { Slug = slug.Value, ExceptId = exceptId?.Value },
            cancellationToken));

    public Task AddAsync(Collection collection, CancellationToken cancellationToken) =>
        unitOfWork.Connection.ExecuteAsync(Command(
            """
            INSERT INTO catalog.collections
                (id, name, slug, description, seo_title, seo_description, card_theme, position,
                 image_url, image_key, is_active, version, created_at, updated_at)
            VALUES
                (@Id, @Name, @Slug, @Description, @SeoTitle, @SeoDescription, @CardTheme, @Position,
                 @ImageUrl, @ImageKey, @IsActive, 0, @CreatedAt, @UpdatedAt)
            """,
            Parameters(collection),
            cancellationToken));

    public async Task<bool> UpdateAsync(Collection collection, CancellationToken cancellationToken)
    {
        var rows = await unitOfWork.Connection.ExecuteAsync(Command(
            """
            UPDATE catalog.collections
               SET name = @Name, slug = @Slug, description = @Description, seo_title = @SeoTitle,
                   seo_description = @SeoDescription, card_theme = @CardTheme, position = @Position,
                   image_url = @ImageUrl, image_key = @ImageKey, is_active = @IsActive,
                   updated_at = @UpdatedAt, version = version + 1
             WHERE id = @Id AND version = @Version
            """,
            Parameters(collection),
            cancellationToken));
        return rows == 1;
    }

    /// <summary>Os vínculos saem em cascata (RF-024 CA6); o histórico de slugs apontaria para o nada.</summary>
    public Task DeleteAsync(CollectionId id, CancellationToken cancellationToken) =>
        unitOfWork.Connection.ExecuteAsync(Command(
            """
            DELETE FROM catalog.slug_history WHERE entity_type = 'Collection' AND entity_id = @Id;
            DELETE FROM catalog.collections WHERE id = @Id;
            """,
            new { Id = id.Value },
            cancellationToken));

    public Task<int> AddProductsAsync(CollectionId id, IReadOnlyCollection<Guid> productIds, DateTime nowUtc, CancellationToken cancellationToken) =>
        unitOfWork.Connection.ExecuteAsync(Command(
            """
            INSERT INTO catalog.product_collections (collection_id, product_id, added_at)
            SELECT @Id, p.id, @Now
              FROM catalog.products p
             WHERE p.id = ANY (@ProductIds)
            ON CONFLICT (collection_id, product_id) DO NOTHING
            """,
            new { Id = id.Value, ProductIds = productIds.ToArray(), Now = nowUtc },
            cancellationToken));

    public Task RemoveProductsAsync(CollectionId id, IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken) =>
        unitOfWork.Connection.ExecuteAsync(Command(
            "DELETE FROM catalog.product_collections WHERE collection_id = @Id AND product_id = ANY (@ProductIds)",
            new { Id = id.Value, ProductIds = productIds.ToArray() },
            cancellationToken));

    private static object Parameters(Collection collection) => new
    {
        Id = collection.Id.Value,
        collection.Name,
        Slug = collection.Slug.Value,
        collection.Description,
        collection.SeoTitle,
        collection.SeoDescription,
        CardTheme = collection.CardTheme.ToString(),
        collection.Position,
        collection.ImageUrl,
        collection.ImageKey,
        collection.IsActive,
        collection.Version,
        CreatedAt = collection.CreatedAtUtc,
        UpdatedAt = collection.UpdatedAtUtc,
    };

    private CommandDefinition Command(string sql, object parameters, CancellationToken cancellationToken) =>
        new(sql, parameters, unitOfWork.Transaction, cancellationToken: cancellationToken);

    private sealed class CollectionRow
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Slug { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string? SeoTitle { get; init; }
        public string? SeoDescription { get; init; }
        public string CardTheme { get; init; } = string.Empty;
        public int Position { get; init; }
        public string? ImageUrl { get; init; }
        public string? ImageKey { get; init; }
        public bool IsActive { get; init; }
        public int Version { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime UpdatedAt { get; init; }
    }
}
