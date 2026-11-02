using ECommerce.Domain.Abstractions;

namespace ECommerce.Domain.Catalog;

public readonly record struct CategoryId(Guid Value)
{
    public static CategoryId New() => new(Guid.CreateVersion7());
}

/// <summary>Texto e rótulos editáveis de uma categoria (RF-050). Os limites de tamanho são validados na Application.</summary>
public sealed record CategoryContent(
    string Name,
    Slug Slug,
    string Description,
    string? SeoTitle,
    string? SeoDescription,
    string? ColorLabel,
    string? SizeLabel);

/// <summary>
/// Categoria do catálogo (RF-050). Pode ter uma categoria mãe, com um nível só (Q07). Os rótulos da grade
/// ("Acabamento", "Aro") ficam aqui; nulos herdam da mãe e, sem mãe, a loja usa "Cor"/"Tamanho" (arquivo 26 ADR-F07).
/// </summary>
public sealed class Category : AggregateRoot<CategoryId>
{
    public const int NameMinLength = 2;
    public const int NameMaxLength = 120;
    public const int LabelMaxLength = 30;

    private Category()
    {
    }

    public CategoryId? ParentId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public Slug Slug { get; private set; } = null!;
    public string Description { get; private set; } = string.Empty;
    public string? SeoTitle { get; private set; }
    public string? SeoDescription { get; private set; }
    public string? ColorLabel { get; private set; }
    public string? SizeLabel { get; private set; }
    public bool IsActive { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    /// <summary>
    /// <paramref name="parent"/> é a categoria mãe já carregada (ou nula). Quem chama confere antes, no repositório,
    /// se a categoria tem filhas, porque isso não cabe no agregado.
    /// </summary>
    public static Result<Category> Create(CategoryContent content, Category? parent, DateTime nowUtc)
    {
        var category = new Category { Id = CategoryId.New(), IsActive = true, CreatedAtUtc = nowUtc };
        var applied = category.Apply(content, parent, hasChildren: false, nowUtc);
        return applied.IsSuccess ? category : applied.Error;
    }

    public static Category Rehydrate(
        CategoryId id,
        CategoryId? parentId,
        CategoryContent content,
        bool isActive,
        int version,
        DateTime createdAtUtc,
        DateTime updatedAtUtc) =>
        new()
        {
            Id = id,
            ParentId = parentId,
            Name = content.Name,
            Slug = content.Slug,
            Description = content.Description,
            SeoTitle = content.SeoTitle,
            SeoDescription = content.SeoDescription,
            ColorLabel = content.ColorLabel,
            SizeLabel = content.SizeLabel,
            IsActive = isActive,
            Version = version,
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = updatedAtUtc,
        };

    /// <summary>
    /// Atualiza texto, SEO, rótulos e categoria mãe. Devolve o slug anterior quando ele mudou, para o redirect 301
    /// (RN-025); nulo quando não mudou.
    /// </summary>
    public Result<Slug?> Update(CategoryContent content, Category? parent, bool hasChildren, DateTime nowUtc)
    {
        var previousSlug = Slug;
        var applied = Apply(content, parent, hasChildren, nowUtc);
        if (applied.IsFailure)
        {
            return applied.Error;
        }

        return previousSlug == Slug ? Result.Success<Slug?>(null) : Result.Success<Slug?>(previousSlug);
    }

    public void SetActive(bool isActive, DateTime nowUtc)
    {
        IsActive = isActive;
        UpdatedAtUtc = nowUtc;
    }

    private Result Apply(CategoryContent content, Category? parent, bool hasChildren, DateTime nowUtc)
    {
        var name = content.Name.Trim();
        if (name.Length is < NameMinLength or > NameMaxLength)
        {
            return Result.Failure(CatalogDomainErrors.InvalidName);
        }

        if (parent is not null)
        {
            if (parent.Id == Id)
            {
                return Result.Failure(CatalogDomainErrors.CategoryCannotBeOwnParent);
            }

            // Q07: a mãe precisa ser principal, e quem já tem filhas não pode virar filha.
            if (parent.ParentId is not null || hasChildren)
            {
                return Result.Failure(CatalogDomainErrors.SubcategoryDepth);
            }
        }

        var colorLabel = Blank(content.ColorLabel);
        var sizeLabel = Blank(content.SizeLabel);
        if (colorLabel is { Length: > LabelMaxLength } || sizeLabel is { Length: > LabelMaxLength })
        {
            return Result.Failure(CatalogDomainErrors.InvalidVariantLabel);
        }

        ParentId = parent?.Id;
        Name = name;
        Slug = content.Slug;
        Description = content.Description.Trim();
        SeoTitle = Blank(content.SeoTitle);
        SeoDescription = Blank(content.SeoDescription);
        ColorLabel = colorLabel;
        SizeLabel = sizeLabel;
        UpdatedAtUtc = nowUtc;
        return Result.Success();
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public interface ICategoryRepository
{
    Task<Category?> GetByIdAsync(CategoryId id, CancellationToken cancellationToken);
    Task<bool> HasChildrenAsync(CategoryId id, CancellationToken cancellationToken);
    Task<bool> HasProductsAsync(CategoryId id, CancellationToken cancellationToken);
    Task<bool> NameInUseAsync(string name, CategoryId? exceptId, CancellationToken cancellationToken);
    Task<bool> SlugInUseAsync(Slug slug, CategoryId? exceptId, CancellationToken cancellationToken);
    Task AddAsync(Category category, CancellationToken cancellationToken);
    Task DeleteAsync(CategoryId id, CancellationToken cancellationToken);

    /// <summary>Concorrência otimista pela <c>version</c> (arquivo 06 §6.1): falso quando outra pessoa salvou antes.</summary>
    Task<bool> UpdateAsync(Category category, CancellationToken cancellationToken);
}
