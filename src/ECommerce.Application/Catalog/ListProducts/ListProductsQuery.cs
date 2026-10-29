using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Application.Abstractions.Paging;

namespace ECommerce.Application.Catalog.ListProducts;

/// <summary>
/// RF-020 — vitrine paginada. <paramref name="Colors"/> são slugs de cor e <paramref name="Sizes"/> nomes de tamanho;
/// um produto entra no filtro quando tem uma variação ativa que atende aos dois ao mesmo tempo.
/// </summary>
public sealed record ListProductsQuery(
    int Page,
    int PageSize,
    string? Category,
    decimal? MinPrice,
    decimal? MaxPrice,
    string? Search,
    IReadOnlyList<string> Colors,
    IReadOnlyList<string> Sizes,
    string Sort) : IQuery<PagedResponse<ProductSummaryResponse>>
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;
    public const string DefaultSort = "createdAt:desc";
}

public sealed record ProductSummaryResponse(
    Guid Id,
    string Slug,
    string Name,
    string CategorySlug,
    decimal PriceFrom,
    IReadOnlyList<ColorResponse> Colors,
    ProductImageResponse? Image);
