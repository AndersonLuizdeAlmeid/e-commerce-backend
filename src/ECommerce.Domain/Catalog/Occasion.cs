using ECommerce.Domain.Abstractions;

namespace ECommerce.Domain.Catalog;

public readonly record struct OccasionId(Guid Value)
{
    public static OccasionId New() => new(Guid.CreateVersion7());
}

/// <summary>Campos editáveis da ocasião. O período é inclusivo nas duas pontas, no horário de Brasília (RF-025 CA3).</summary>
public sealed record OccasionContent(
    string Name,
    string Text,
    DateOnly StartsOn,
    DateOnly EndsOn,
    bool IsActive,
    IReadOnlyList<Guid> ProductIds);

/// <summary>Data comemorativa da vitrine (RF-025): nome, texto curto, período e a lista ordenada de peças.</summary>
public sealed class Occasion : AggregateRoot<OccasionId>
{
    public const int NameMinLength = 2;
    public const int NameMaxLength = 120;
    public const int TextMaxLength = 160;

    private Occasion()
    {
    }

    public string Name { get; private set; } = string.Empty;
    public string Text { get; private set; } = string.Empty;
    public DateOnly StartsOn { get; private set; }
    public DateOnly EndsOn { get; private set; }
    public bool IsActive { get; private set; }
    public IReadOnlyList<Guid> ProductIds { get; private set; } = [];
    public string? ImageUrl { get; private set; }
    public string? ImageKey { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    public static Result<Occasion> Create(OccasionContent content, DateTime nowUtc)
    {
        var occasion = new Occasion { Id = OccasionId.New(), CreatedAtUtc = nowUtc };
        var applied = occasion.Update(content, nowUtc);
        return applied.IsSuccess ? occasion : applied.Error;
    }

    public static Occasion Rehydrate(
        OccasionId id,
        OccasionContent content,
        string? imageUrl,
        string? imageKey,
        int version,
        DateTime createdAtUtc,
        DateTime updatedAtUtc) =>
        new()
        {
            Id = id,
            Name = content.Name,
            Text = content.Text,
            StartsOn = content.StartsOn,
            EndsOn = content.EndsOn,
            IsActive = content.IsActive,
            ProductIds = content.ProductIds,
            ImageUrl = imageUrl,
            ImageKey = imageKey,
            Version = version,
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = updatedAtUtc,
        };

    public Result Update(OccasionContent content, DateTime nowUtc)
    {
        var name = content.Name.Trim();
        if (name.Length is < NameMinLength or > NameMaxLength)
        {
            return Result.Failure(CatalogDomainErrors.InvalidName);
        }

        var text = content.Text.Trim();
        if (text.Length > TextMaxLength)
        {
            return Result.Failure(CatalogDomainErrors.InvalidOccasionText);
        }

        if (content.EndsOn < content.StartsOn)
        {
            return Result.Failure(CatalogDomainErrors.InvalidOccasionPeriod);
        }

        Name = name;
        Text = text;
        StartsOn = content.StartsOn;
        EndsOn = content.EndsOn;
        IsActive = content.IsActive;
        ProductIds = content.ProductIds.Distinct().ToList();
        UpdatedAtUtc = nowUtc;
        return Result.Success();
    }

    /// <summary>Troca a imagem (nula remove) e devolve a chave da anterior, para apagar do bucket depois do commit.</summary>
    public string? ReplaceImage(string? url, string? key, DateTime nowUtc)
    {
        var previousKey = ImageKey;
        ImageUrl = url;
        ImageKey = key;
        UpdatedAtUtc = nowUtc;
        return previousKey;
    }
}

public interface IOccasionRepository
{
    Task<Occasion?> GetByIdAsync(OccasionId id, CancellationToken cancellationToken);

    /// <summary>RF-025 CA2: outra ocasião ativa cujo período toca o informado.</summary>
    Task<bool> OverlapsActiveAsync(DateOnly startsOn, DateOnly endsOn, OccasionId? exceptId, CancellationToken cancellationToken);

    Task AddAsync(Occasion occasion, CancellationToken cancellationToken);

    /// <summary>Concorrência otimista pela <c>version</c>; regrava a lista de peças na ordem do agregado.</summary>
    Task<bool> UpdateAsync(Occasion occasion, CancellationToken cancellationToken);

    Task DeleteAsync(OccasionId id, CancellationToken cancellationToken);

    /// <summary>Quantos dos produtos informados existem: uma peça apagada em outra aba não pode virar erro de banco.</summary>
    Task<int> CountExistingProductsAsync(IReadOnlyCollection<Guid> productIds, CancellationToken cancellationToken);
}

/// <summary>Peça em destaque da home (RF-025): uma linha só, sem identidade própria.</summary>
public interface IShowcaseFeaturedRepository
{
    Task SetAsync(ProductId productId, ColorId? colorId, DateTime nowUtc, CancellationToken cancellationToken);

    /// <summary>Sem destaque, a home mostra a novidade mais recente com estoque (RF-025 CA1).</summary>
    Task ClearAsync(CancellationToken cancellationToken);
}
