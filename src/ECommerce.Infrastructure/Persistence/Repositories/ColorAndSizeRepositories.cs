using Dapper;
using ECommerce.Application.Abstractions.Data;
using ECommerce.Domain.Catalog;

namespace ECommerce.Infrastructure.Persistence.Repositories;

internal sealed class ColorRepository(IUnitOfWork unitOfWork) : IColorRepository
{
    public async Task<Color?> GetByIdAsync(ColorId id, CancellationToken cancellationToken)
    {
        var row = await unitOfWork.Connection.QuerySingleOrDefaultAsync<ColorRow>(Command(
            "SELECT id, name, slug, hex FROM catalog.colors WHERE id = @Id", new { Id = id.Value }, cancellationToken));
        return row is null ? null : Color.Rehydrate(new ColorId(row.Id), row.Name, Slug.Rehydrate(row.Slug), row.Hex);
    }

    public Task<bool> NameOrSlugInUseAsync(string name, Slug slug, ColorId? exceptId, CancellationToken cancellationToken) =>
        unitOfWork.Connection.ExecuteScalarAsync<bool>(Command(
            """
            SELECT EXISTS (
                SELECT 1 FROM catalog.colors
                 WHERE (lower(name) = lower(@Name) OR slug = @Slug) AND (@ExceptId::uuid IS NULL OR id <> @ExceptId))
            """,
            new { Name = name, Slug = slug.Value, ExceptId = exceptId?.Value },
            cancellationToken));

    public Task<bool> IsUsedAsync(ColorId id, CancellationToken cancellationToken) =>
        unitOfWork.Connection.ExecuteScalarAsync<bool>(Command(
            """
            SELECT EXISTS (SELECT 1 FROM catalog.product_variants WHERE color_id = @Id)
                OR EXISTS (SELECT 1 FROM catalog.product_images WHERE color_id = @Id)
            """,
            new { Id = id.Value },
            cancellationToken));

    public Task AddAsync(Color color, CancellationToken cancellationToken) =>
        unitOfWork.Connection.ExecuteAsync(Command(
            "INSERT INTO catalog.colors (id, name, slug, hex) VALUES (@Id, @Name, @Slug, @Hex)", Parameters(color), cancellationToken));

    public Task UpdateAsync(Color color, CancellationToken cancellationToken) =>
        unitOfWork.Connection.ExecuteAsync(Command(
            "UPDATE catalog.colors SET name = @Name, slug = @Slug, hex = @Hex WHERE id = @Id", Parameters(color), cancellationToken));

    public Task DeleteAsync(ColorId id, CancellationToken cancellationToken) =>
        unitOfWork.Connection.ExecuteAsync(Command("DELETE FROM catalog.colors WHERE id = @Id", new { Id = id.Value }, cancellationToken));

    private static object Parameters(Color color) => new { Id = color.Id.Value, color.Name, Slug = color.Slug.Value, color.Hex };

    private CommandDefinition Command(string sql, object parameters, CancellationToken cancellationToken) =>
        new(sql, parameters, unitOfWork.Transaction, cancellationToken: cancellationToken);

    private sealed class ColorRow
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Slug { get; init; } = string.Empty;
        public string Hex { get; init; } = string.Empty;
    }
}

internal sealed class SizeRepository(IUnitOfWork unitOfWork) : ISizeRepository
{
    public async Task<Size?> GetByIdAsync(SizeId id, CancellationToken cancellationToken)
    {
        var row = await unitOfWork.Connection.QuerySingleOrDefaultAsync<SizeRow>(Command(
            "SELECT id, name, sort_order FROM catalog.sizes WHERE id = @Id", new { Id = id.Value }, cancellationToken));
        return row is null ? null : Size.Rehydrate(new SizeId(row.Id), row.Name, row.SortOrder);
    }

    public Task<bool> NameInUseAsync(string name, SizeId? exceptId, CancellationToken cancellationToken) =>
        unitOfWork.Connection.ExecuteScalarAsync<bool>(Command(
            "SELECT EXISTS (SELECT 1 FROM catalog.sizes WHERE lower(name) = lower(@Name) AND (@ExceptId::uuid IS NULL OR id <> @ExceptId))",
            new { Name = name, ExceptId = exceptId?.Value },
            cancellationToken));

    public Task<bool> IsUsedAsync(SizeId id, CancellationToken cancellationToken) =>
        unitOfWork.Connection.ExecuteScalarAsync<bool>(Command(
            "SELECT EXISTS (SELECT 1 FROM catalog.product_variants WHERE size_id = @Id)", new { Id = id.Value }, cancellationToken));

    public Task AddAsync(Size size, CancellationToken cancellationToken) =>
        unitOfWork.Connection.ExecuteAsync(Command(
            "INSERT INTO catalog.sizes (id, name, sort_order) VALUES (@Id, @Name, @SortOrder)", Parameters(size), cancellationToken));

    public Task UpdateAsync(Size size, CancellationToken cancellationToken) =>
        unitOfWork.Connection.ExecuteAsync(Command(
            "UPDATE catalog.sizes SET name = @Name, sort_order = @SortOrder WHERE id = @Id", Parameters(size), cancellationToken));

    public Task DeleteAsync(SizeId id, CancellationToken cancellationToken) =>
        unitOfWork.Connection.ExecuteAsync(Command("DELETE FROM catalog.sizes WHERE id = @Id", new { Id = id.Value }, cancellationToken));

    private static object Parameters(Size size) => new { Id = size.Id.Value, size.Name, size.SortOrder };

    private CommandDefinition Command(string sql, object parameters, CancellationToken cancellationToken) =>
        new(sql, parameters, unitOfWork.Transaction, cancellationToken: cancellationToken);

    private sealed class SizeRow
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public short SortOrder { get; init; }
    }
}
