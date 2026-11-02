using ECommerce.Application.Abstractions.Messaging;

namespace ECommerce.Application.Catalog.GetCatalogFilters;

/// <summary>RF-023 — cores, tamanhos, faixa de preço e categorias existentes na vitrine (opcionalmente numa categoria ou coleção).</summary>
public sealed record GetCatalogFiltersQuery(string? Category, string? Collection = null) : IQuery<CatalogFiltersResponse>;

public sealed record FilterCategoryResponse(Guid Id, string Name, string Slug);

public sealed record CatalogFiltersResponse(
    IReadOnlyList<ColorResponse> Colors,
    IReadOnlyList<SizeResponse> Sizes,
    decimal? MinPrice,
    decimal? MaxPrice,
    IReadOnlyList<FilterCategoryResponse> Categories);
