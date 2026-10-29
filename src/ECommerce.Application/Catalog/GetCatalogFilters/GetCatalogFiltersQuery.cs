using ECommerce.Application.Abstractions.Messaging;

namespace ECommerce.Application.Catalog.GetCatalogFilters;

/// <summary>RF-023 — cores, tamanhos e faixa de preço existentes na vitrine (opcionalmente numa categoria).</summary>
public sealed record GetCatalogFiltersQuery(string? Category) : IQuery<CatalogFiltersResponse>;

public sealed record CatalogFiltersResponse(
    IReadOnlyList<ColorResponse> Colors,
    IReadOnlyList<SizeResponse> Sizes,
    decimal? MinPrice,
    decimal? MaxPrice);
