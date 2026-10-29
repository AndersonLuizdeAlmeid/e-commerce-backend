using Dapper;
using ECommerce.Application.Abstractions.Caching;
using ECommerce.Application.Abstractions.Data;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Domain.Abstractions;

namespace ECommerce.Application.Catalog.ListCategories;

internal sealed class ListCategoriesHandler(IDbConnectionFactory connectionFactory, ICacheService cache)
    : IQueryHandler<ListCategoriesQuery, IReadOnlyList<CategorySummaryResponse>>
{
    private const string Sql = """
        SELECT id, name, slug
          FROM catalog.categories
         WHERE is_active
         ORDER BY name
        """;

    public async Task<Result<IReadOnlyList<CategorySummaryResponse>>> Handle(
        ListCategoriesQuery query, CancellationToken cancellationToken)
    {
        var categories = await cache.GetOrCreateAsync(
            CatalogCache.CategoriesKey, LoadAsync, CatalogCache.Categories, cancellationToken);

        return Result.Success(categories);
    }

    private async ValueTask<IReadOnlyList<CategorySummaryResponse>> LoadAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        var categories = await connection.QueryAsync<CategorySummaryResponse>(
            new CommandDefinition(Sql, cancellationToken: cancellationToken));

        return categories.AsList();
    }
}
