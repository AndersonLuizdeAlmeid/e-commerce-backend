using ECommerce.Application.Abstractions.Messaging;

namespace ECommerce.Application.Catalog.ListCategories;

/// <summary>RF-022 — categorias ativas, ordenadas por nome.</summary>
public sealed record ListCategoriesQuery : IQuery<IReadOnlyList<CategorySummaryResponse>>;

public sealed record CategorySummaryResponse(Guid Id, string Name, string Slug);
