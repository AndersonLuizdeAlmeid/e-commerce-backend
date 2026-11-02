using System.Diagnostics.CodeAnalysis;
using ECommerce.Domain.Abstractions;

namespace ECommerce.Domain.Catalog;

public readonly record struct CollectionId(Guid Value)
{
    public static CollectionId New() => new(Guid.CreateVersion7());
}

/// <summary>Tema das cartelas da coleção (arquivo 26 §2.2): marfim ou grafite com folha prateada.</summary>
public enum CardTheme
{
    Ivory,
    Graphite,
}

public sealed record CollectionContent(
    string Name,
    Slug Slug,
    string Description,
    string? SeoTitle,
    string? SeoDescription,
    CardTheme CardTheme,
    int Position);

/// <summary>Coleção (RF-024, ADR-F08): agrupa peças de qualquer categoria. Os vínculos ficam no repositório.</summary>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "É o nome do conceito na loja, não um tipo de coleção do .NET.")]
public sealed class Collection : AggregateRoot<CollectionId>
{
    public const int NameMinLength = 2;
    public const int NameMaxLength = 120;

    private Collection()
    {
    }

    public string Name { get; private set; } = string.Empty;
    public Slug Slug { get; private set; } = null!;
    public string Description { get; private set; } = string.Empty;
    public string? SeoTitle { get; private set; }
    public string? SeoDescription { get; private set; }
    public CardTheme CardTheme { get; private set; }
    public int Position { get; private set; }
    public string? ImageUrl { get; private set; }
    public string? ImageKey { get; private set; }
    public bool IsActive { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    public static Result<Collection> Create(CollectionContent content, DateTime nowUtc)
    {
        var collection = new Collection { Id = CollectionId.New(), IsActive = true, CreatedAtUtc = nowUtc };
        var applied = collection.Apply(content, nowUtc);
        return applied.IsSuccess ? collection : applied.Error;
    }

    public static Collection Rehydrate(
        CollectionId id,
        CollectionContent content,
        string? imageUrl,
        string? imageKey,
        bool isActive,
        int version,
        DateTime createdAtUtc,
        DateTime updatedAtUtc) =>
        new()
        {
            Id = id,
            Name = content.Name,
            Slug = content.Slug,
            Description = content.Description,
            SeoTitle = content.SeoTitle,
            SeoDescription = content.SeoDescription,
            CardTheme = content.CardTheme,
            Position = content.Position,
            ImageUrl = imageUrl,
            ImageKey = imageKey,
            IsActive = isActive,
            Version = version,
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = updatedAtUtc,
        };

    /// <summary>Devolve o slug anterior quando ele mudou, para o redirect 301 (RF-024 CA4); nulo quando não mudou.</summary>
    public Result<Slug?> Update(CollectionContent content, DateTime nowUtc)
    {
        var previousSlug = Slug;
        var applied = Apply(content, nowUtc);
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

    /// <summary>Troca a capa (nula remove) e devolve a chave da anterior, para apagar do bucket depois do commit.</summary>
    public string? ReplaceImage(string? url, string? key, DateTime nowUtc)
    {
        var previousKey = ImageKey;
        ImageUrl = url;
        ImageKey = key;
        UpdatedAtUtc = nowUtc;
        return previousKey;
    }

    private Result Apply(CollectionContent content, DateTime nowUtc)
    {
        var name = content.Name.Trim();
        if (name.Length is < NameMinLength or > NameMaxLength)
        {
            return Result.Failure(CatalogDomainErrors.InvalidName);
        }

        Name = name;
        Slug = content.Slug;
        Description = content.Description.Trim();
        SeoTitle = string.IsNullOrWhiteSpace(content.SeoTitle) ? null : content.SeoTitle.Trim();
        SeoDescription = string.IsNullOrWhiteSpace(content.SeoDescription) ? null : content.SeoDescription.Trim();
        CardTheme = content.CardTheme;
        Position = content.Position;
        UpdatedAtUtc = nowUtc;
        return Result.Success();
    }
}

public interface ICollectionRepository
{
    Task<Collection?> GetByIdAsync(CollectionId id, CancellationToken cancellationToken);
    Task<bool> NameInUseAsync(string name, CollectionId? exceptId, CancellationToken cancellationToken);
    Task<bool> SlugInUseAsync(Slug slug, CollectionId? exceptId, CancellationToken cancellationToken);
    Task AddAsync(Collection collection, CancellationToken cancellationToken);

    /// <summary>Concorrência otimista pela <c>version</c>: falso quando outra pessoa salvou antes.</summary>
    Task<bool> UpdateAsync(Collection collection, CancellationToken cancellationToken);

    Task DeleteAsync(CollectionId id, CancellationToken cancellationToken);

    /// <summary>RF-024 CA5: produto que já está na coleção fica como está. Devolve quantos entraram.</summary>
    Task<int> AddProductsAsync(CollectionId id, IReadOnlyCollection<Guid> productIds, DateTime nowUtc, CancellationToken cancellationToken);

    Task RemoveProductsAsync(CollectionId id, IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken);
}
