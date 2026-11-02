using Dapper;
using ECommerce.Application.Abstractions.Data;
using ECommerce.Domain.Catalog;

namespace ECommerce.Infrastructure.Persistence.Repositories;

internal sealed class CategoryRepository(IUnitOfWork unitOfWork) : ICategoryRepository
{
    private const string SelectColumns = """
        SELECT id, parent_id, name, slug, description, seo_title, seo_description, color_label, size_label,
               is_active, version, created_at, updated_at
          FROM catalog.categories
        """;

    public async Task<Category?> GetByIdAsync(CategoryId id, CancellationToken cancellationToken)
    {
        var row = await unitOfWork.Connection.QuerySingleOrDefaultAsync<CategoryRow>(
            Command($"{SelectColumns} WHERE id = @Id", new { Id = id.Value }, cancellationToken));

        return row is null
            ? null
            : Category.Rehydrate(
                new CategoryId(row.Id),
                row.ParentId is { } parentId ? new CategoryId(parentId) : null,
                new CategoryContent(
                    row.Name, Slug.Rehydrate(row.Slug), row.Description, row.SeoTitle, row.SeoDescription, row.ColorLabel, row.SizeLabel),
                row.IsActive,
                row.Version,
                row.CreatedAt,
                row.UpdatedAt);
    }

    public Task<bool> HasChildrenAsync(CategoryId id, CancellationToken cancellationToken) =>
        Exists("SELECT EXISTS (SELECT 1 FROM catalog.categories WHERE parent_id = @Id)", new { Id = id.Value }, cancellationToken);

    public Task<bool> HasProductsAsync(CategoryId id, CancellationToken cancellationToken) =>
        Exists("SELECT EXISTS (SELECT 1 FROM catalog.products WHERE category_id = @Id)", new { Id = id.Value }, cancellationToken);

    public Task<bool> NameInUseAsync(string name, CategoryId? exceptId, CancellationToken cancellationToken) =>
        Exists(
            "SELECT EXISTS (SELECT 1 FROM catalog.categories WHERE lower(name) = lower(@Name) AND (@ExceptId::uuid IS NULL OR id <> @ExceptId))",
            new { Name = name, ExceptId = exceptId?.Value },
            cancellationToken);

    public Task<bool> SlugInUseAsync(Slug slug, CategoryId? exceptId, CancellationToken cancellationToken) =>
        Exists(
            "SELECT EXISTS (SELECT 1 FROM catalog.categories WHERE slug = @Slug AND (@ExceptId::uuid IS NULL OR id <> @ExceptId))",
            new { Slug = slug.Value, ExceptId = exceptId?.Value },
            cancellationToken);

    public Task AddAsync(Category category, CancellationToken cancellationToken) =>
        unitOfWork.Connection.ExecuteAsync(Command(
            """
            INSERT INTO catalog.categories
                (id, parent_id, name, slug, description, seo_title, seo_description, color_label, size_label,
                 is_active, version, created_at, updated_at)
            VALUES
                (@Id, @ParentId, @Name, @Slug, @Description, @SeoTitle, @SeoDescription, @ColorLabel, @SizeLabel,
                 @IsActive, 0, @CreatedAt, @UpdatedAt)
            """,
            Parameters(category),
            cancellationToken));

    public async Task<bool> UpdateAsync(Category category, CancellationToken cancellationToken)
    {
        var rows = await unitOfWork.Connection.ExecuteAsync(Command(
            """
            UPDATE catalog.categories
               SET parent_id = @ParentId, name = @Name, slug = @Slug, description = @Description,
                   seo_title = @SeoTitle, seo_description = @SeoDescription,
                   color_label = @ColorLabel, size_label = @SizeLabel, is_active = @IsActive,
                   updated_at = @UpdatedAt, version = version + 1
             WHERE id = @Id AND version = @Version
            """,
            Parameters(category),
            cancellationToken));
        return rows == 1;
    }

    /// <summary>Sai junto o histórico de slugs: os redirects apontariam para uma categoria que não existe mais.</summary>
    public Task DeleteAsync(CategoryId id, CancellationToken cancellationToken) =>
        unitOfWork.Connection.ExecuteAsync(Command(
            """
            DELETE FROM catalog.slug_history WHERE entity_type = 'Category' AND entity_id = @Id;
            DELETE FROM catalog.categories WHERE id = @Id;
            """,
            new { Id = id.Value },
            cancellationToken));

    private Task<bool> Exists(string sql, object parameters, CancellationToken cancellationToken) =>
        unitOfWork.Connection.ExecuteScalarAsync<bool>(Command(sql, parameters, cancellationToken));

    private static object Parameters(Category category) => new
    {
        Id = category.Id.Value,
        ParentId = category.ParentId?.Value,
        category.Name,
        Slug = category.Slug.Value,
        category.Description,
        category.SeoTitle,
        category.SeoDescription,
        category.ColorLabel,
        category.SizeLabel,
        category.IsActive,
        category.Version,
        CreatedAt = category.CreatedAtUtc,
        UpdatedAt = category.UpdatedAtUtc,
    };

    private CommandDefinition Command(string sql, object parameters, CancellationToken cancellationToken) =>
        new(sql, parameters, unitOfWork.Transaction, cancellationToken: cancellationToken);

    private sealed class CategoryRow
    {
        public Guid Id { get; init; }
        public Guid? ParentId { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Slug { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string? SeoTitle { get; init; }
        public string? SeoDescription { get; init; }
        public string? ColorLabel { get; init; }
        public string? SizeLabel { get; init; }
        public bool IsActive { get; init; }
        public int Version { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime UpdatedAt { get; init; }
    }
}

internal sealed class SlugHistoryRepository(IUnitOfWork unitOfWork) : ISlugHistory
{
    public Task<bool> BelongsToAnotherAsync(Slug slug, string entityType, Guid entityId, CancellationToken cancellationToken) =>
        unitOfWork.Connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            SELECT EXISTS (
                SELECT 1 FROM catalog.slug_history
                 WHERE old_slug = @Slug AND NOT (entity_type = @EntityType AND entity_id = @EntityId))
            """,
            new { Slug = slug.Value, EntityType = entityType, EntityId = entityId },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));

    public Task RecordChangeAsync(
        string entityType, Guid entityId, Slug oldSlug, Slug newSlug, DateTime nowUtc, CancellationToken cancellationToken) =>
        unitOfWork.Connection.ExecuteAsync(new CommandDefinition(
            """
            DELETE FROM catalog.slug_history WHERE old_slug = @NewSlug AND entity_type = @EntityType AND entity_id = @EntityId;
            INSERT INTO catalog.slug_history (old_slug, entity_type, entity_id, changed_at)
            VALUES (@OldSlug, @EntityType, @EntityId, @Now)
            ON CONFLICT (old_slug) DO UPDATE SET entity_type = EXCLUDED.entity_type, entity_id = EXCLUDED.entity_id, changed_at = EXCLUDED.changed_at;
            """,
            new { OldSlug = oldSlug.Value, NewSlug = newSlug.Value, EntityType = entityType, EntityId = entityId, Now = nowUtc },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));
}
