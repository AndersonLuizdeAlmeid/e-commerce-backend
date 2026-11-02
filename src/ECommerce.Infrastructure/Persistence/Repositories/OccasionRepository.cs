using Dapper;
using ECommerce.Application.Abstractions.Data;
using ECommerce.Domain.Catalog;

namespace ECommerce.Infrastructure.Persistence.Repositories;

internal sealed class OccasionRepository(IUnitOfWork unitOfWork) : IOccasionRepository
{
    private const string ReplaceProducts = """
        DELETE FROM catalog.occasion_products WHERE occasion_id = @Id;
        INSERT INTO catalog.occasion_products (occasion_id, product_id, position)
        SELECT @Id, product_id, (position - 1)::smallint
          FROM unnest(@ProductIds) WITH ORDINALITY AS chosen(product_id, position);
        """;

    public async Task<Occasion?> GetByIdAsync(OccasionId id, CancellationToken cancellationToken)
    {
        using var grid = await unitOfWork.Connection.QueryMultipleAsync(Command(
            """
            SELECT id, name, short_text, starts_on, ends_on, is_active, image_url, image_key, version, created_at, updated_at
              FROM catalog.occasions
             WHERE id = @Id;

            SELECT product_id FROM catalog.occasion_products WHERE occasion_id = @Id ORDER BY position;
            """,
            new { Id = id.Value },
            cancellationToken));

        var row = await grid.ReadSingleOrDefaultAsync<OccasionRow>();
        if (row is null)
        {
            return null;
        }

        var productIds = (await grid.ReadAsync<Guid>()).ToList();
        return Occasion.Rehydrate(
            new OccasionId(row.Id),
            new OccasionContent(
                row.Name, row.ShortText, DateOnly.FromDateTime(row.StartsOn), DateOnly.FromDateTime(row.EndsOn), row.IsActive, productIds),
            row.ImageUrl,
            row.ImageKey,
            row.Version,
            row.CreatedAt,
            row.UpdatedAt);
    }

    public Task<bool> OverlapsActiveAsync(DateOnly startsOn, DateOnly endsOn, OccasionId? exceptId, CancellationToken cancellationToken) =>
        unitOfWork.Connection.ExecuteScalarAsync<bool>(Command(
            """
            SELECT EXISTS (
                SELECT 1 FROM catalog.occasions
                 WHERE is_active
                   AND daterange(starts_on, ends_on, '[]') && daterange(@StartsOn::date, @EndsOn::date, '[]')
                   AND (@ExceptId::uuid IS NULL OR id <> @ExceptId))
            """,
            new { StartsOn = ToDate(startsOn), EndsOn = ToDate(endsOn), ExceptId = exceptId?.Value },
            cancellationToken));

    public Task AddAsync(Occasion occasion, CancellationToken cancellationToken) =>
        unitOfWork.Connection.ExecuteAsync(Command(
            $"""
            INSERT INTO catalog.occasions
                (id, name, short_text, starts_on, ends_on, is_active, image_url, image_key, version, created_at, updated_at)
            VALUES
                (@Id, @Name, @Text, @StartsOn::date, @EndsOn::date, @IsActive, @ImageUrl, @ImageKey, 0, @CreatedAt, @UpdatedAt);
            {ReplaceProducts}
            """,
            Parameters(occasion),
            cancellationToken));

    public async Task<bool> UpdateAsync(Occasion occasion, CancellationToken cancellationToken)
    {
        var updated = await unitOfWork.Connection.ExecuteAsync(Command(
            """
            UPDATE catalog.occasions
               SET name = @Name, short_text = @Text, starts_on = @StartsOn::date, ends_on = @EndsOn::date, is_active = @IsActive,
                   image_url = @ImageUrl, image_key = @ImageKey, updated_at = @UpdatedAt, version = version + 1
             WHERE id = @Id AND version = @Version
            """,
            Parameters(occasion),
            cancellationToken));
        if (updated != 1)
        {
            return false;
        }

        await unitOfWork.Connection.ExecuteAsync(Command(ReplaceProducts, Parameters(occasion), cancellationToken));
        return true;
    }

    public Task DeleteAsync(OccasionId id, CancellationToken cancellationToken) =>
        unitOfWork.Connection.ExecuteAsync(Command("DELETE FROM catalog.occasions WHERE id = @Id", new { Id = id.Value }, cancellationToken));

    public Task<int> CountExistingProductsAsync(IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken) =>
        unitOfWork.Connection.ExecuteScalarAsync<int>(Command(
            "SELECT count(*)::int FROM catalog.products WHERE id = ANY (@ProductIds)",
            new { ProductIds = productIds.ToArray() },
            cancellationToken));

    // Sem depender do suporte a DateOnly do Dapper: vai como timestamp e o SQL converte com ::date.
    private static DateTime ToDate(DateOnly date) => date.ToDateTime(TimeOnly.MinValue);

    private static object Parameters(Occasion occasion) => new
    {
        Id = occasion.Id.Value,
        occasion.Name,
        occasion.Text,
        StartsOn = ToDate(occasion.StartsOn),
        EndsOn = ToDate(occasion.EndsOn),
        occasion.IsActive,
        occasion.ImageUrl,
        occasion.ImageKey,
        occasion.Version,
        CreatedAt = occasion.CreatedAtUtc,
        UpdatedAt = occasion.UpdatedAtUtc,
        ProductIds = occasion.ProductIds.ToArray(),
    };

    private CommandDefinition Command(string sql, object parameters, CancellationToken cancellationToken) =>
        new(sql, parameters, unitOfWork.Transaction, cancellationToken: cancellationToken);

    private sealed class OccasionRow
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string ShortText { get; init; } = string.Empty;
        public DateTime StartsOn { get; init; }
        public DateTime EndsOn { get; init; }
        public bool IsActive { get; init; }
        public string? ImageUrl { get; init; }
        public string? ImageKey { get; init; }
        public int Version { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime UpdatedAt { get; init; }
    }
}

internal sealed class ShowcaseFeaturedRepository(IUnitOfWork unitOfWork) : IShowcaseFeaturedRepository
{
    public Task SetAsync(ProductId productId, ColorId? colorId, DateTime nowUtc, CancellationToken cancellationToken) =>
        unitOfWork.Connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO catalog.showcase_featured (id, product_id, color_id, updated_at)
            VALUES (true, @ProductId, @ColorId, @Now)
            ON CONFLICT (id) DO UPDATE SET product_id = EXCLUDED.product_id, color_id = EXCLUDED.color_id, updated_at = EXCLUDED.updated_at
            """,
            new { ProductId = productId.Value, ColorId = colorId?.Value, Now = nowUtc },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));

    public Task ClearAsync(CancellationToken cancellationToken) =>
        unitOfWork.Connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM catalog.showcase_featured", transaction: unitOfWork.Transaction, cancellationToken: cancellationToken));
}
