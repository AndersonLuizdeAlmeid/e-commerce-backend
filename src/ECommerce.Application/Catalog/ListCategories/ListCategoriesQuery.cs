using ECommerce.Application.Abstractions.Messaging;

namespace ECommerce.Application.Catalog.ListCategories;

/// <summary>RF-022 — categorias ativas, ordenadas por nome.</summary>
public sealed record ListCategoriesQuery : IQuery<IReadOnlyList<CategorySummaryResponse>>;

/// <summary><paramref name="ParentId"/> preenchido = subcategoria (Q07); a navegação da loja mostra só as principais.</summary>
public sealed record CategorySummaryResponse(Guid Id, string Name, string Slug, Guid? ParentId);
