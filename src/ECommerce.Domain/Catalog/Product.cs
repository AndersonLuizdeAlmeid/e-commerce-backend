using ECommerce.Domain.Abstractions;

namespace ECommerce.Domain.Catalog;

public readonly record struct ProductId(Guid Value)
{
    public static ProductId New() => new(Guid.CreateVersion7());
}

public readonly record struct VariantId(Guid Value)
{
    public static VariantId New() => new(Guid.CreateVersion7());
}

/// <summary>Peso e medidas para o frete dos Correios (arquivo 22). Na variação, nulo herda do produto.</summary>
public sealed record PackageSize(int WeightGrams, decimal HeightCm, decimal WidthCm, decimal LengthCm)
{
    public bool IsValid => WeightGrams > 0 && HeightCm > 0 && WidthCm > 0 && LengthCm > 0;
}

/// <summary>Texto, preço base e dados de envio do produto (RF-051). Limites de tamanho validados na Application.</summary>
public sealed record ProductContent(
    CategoryId CategoryId,
    string Name,
    Slug Slug,
    string Description,
    decimal BasePrice,
    PackageSize Package,
    string? SeoTitle,
    string? SeoDescription);

/// <summary>Uma combinação nova da grade, com o SKU já gerado por quem chama (RF-056).</summary>
public sealed record NewVariant(ColorId? ColorId, SizeId? SizeId, string Sku);

/// <summary>Dados editáveis de uma variação (RF-056). <see cref="Price"/> e <see cref="Package"/> nulos herdam do produto.</summary>
public sealed record VariantChanges(string Sku, decimal? Price, Gtin? Barcode, PackageSize? Package);

/// <summary>
/// Produto com a grade de variações (arquivo 04 §4.3). Nasce inativo: só entra na loja com ao menos uma variação
/// ativa (RN-023). Variações nunca são apagadas, só desativadas, porque pedidos guardam o <see cref="VariantId"/>.
/// </summary>
public sealed class Product : AggregateRoot<ProductId>
{
    public const int NameMinLength = 2;
    public const int NameMaxLength = 120;

    private readonly List<ProductVariant> _variants = [];

    private Product()
    {
    }

    public CategoryId CategoryId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public Slug Slug { get; private set; } = null!;
    public string Description { get; private set; } = string.Empty;
    public decimal BasePrice { get; private set; }
    public PackageSize Package { get; private set; } = null!;
    public string? SeoTitle { get; private set; }
    public string? SeoDescription { get; private set; }
    public bool IsActive { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }
    public IReadOnlyList<ProductVariant> Variants => _variants;

    public static Result<Product> Create(ProductContent content, DateTime nowUtc)
    {
        var product = new Product { Id = ProductId.New(), IsActive = false, CreatedAtUtc = nowUtc };
        var applied = product.Apply(content, nowUtc);
        return applied.IsSuccess ? product : applied.Error;
    }

    public static Product Rehydrate(
        ProductId id,
        ProductContent content,
        bool isActive,
        int version,
        DateTime createdAtUtc,
        DateTime updatedAtUtc,
        IEnumerable<ProductVariant> variants)
    {
        var product = new Product
        {
            Id = id,
            CategoryId = content.CategoryId,
            Name = content.Name,
            Slug = content.Slug,
            Description = content.Description,
            BasePrice = content.BasePrice,
            Package = content.Package,
            SeoTitle = content.SeoTitle,
            SeoDescription = content.SeoDescription,
            IsActive = isActive,
            Version = version,
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = updatedAtUtc,
        };
        product._variants.AddRange(variants);
        return product;
    }

    /// <summary>Devolve o slug anterior quando ele mudou, para o redirect 301 (RN-025); nulo quando não mudou.</summary>
    public Result<Slug?> Update(ProductContent content, DateTime nowUtc)
    {
        var previousSlug = Slug;
        var applied = Apply(content, nowUtc);
        if (applied.IsFailure)
        {
            return applied.Error;
        }

        return previousSlug == Slug ? Result.Success<Slug?>(null) : Result.Success<Slug?>(previousSlug);
    }

    /// <summary>
    /// RF-056: acrescenta as combinações que ainda não existem (as existentes ficam como estão, mesmo inativas).
    /// Produto sem cor e sem tamanho tem uma variação única (RN-022): <c>new NewVariant(null, null, sku)</c>.
    /// </summary>
    public Result<IReadOnlyList<ProductVariant>> AddVariants(IReadOnlyCollection<NewVariant> combinations, DateTime nowUtc)
    {
        var added = new List<ProductVariant>();
        foreach (var combination in combinations)
        {
            // RN-021: uma variação por cor + tamanho.
            if (_variants.Concat(added).Any(v => v.ColorId == combination.ColorId && v.SizeId == combination.SizeId))
            {
                continue;
            }

            if (_variants.Concat(added).Any(v => string.Equals(v.Sku, combination.Sku, StringComparison.OrdinalIgnoreCase)))
            {
                return CatalogDomainErrors.DuplicateSku;
            }

            added.Add(ProductVariant.Create(combination, nowUtc));
        }

        _variants.AddRange(added);
        UpdatedAtUtc = nowUtc;
        return added;
    }

    public Result UpdateVariant(VariantId variantId, VariantChanges changes, DateTime nowUtc)
    {
        var variant = _variants.Find(v => v.Id == variantId);
        if (variant is null)
        {
            return Result.Failure(CatalogDomainErrors.VariantNotFound);
        }

        if (_variants.Any(v => v.Id != variantId && string.Equals(v.Sku, changes.Sku.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return Result.Failure(CatalogDomainErrors.DuplicateSku);
        }

        if (changes.Barcode is not null && _variants.Any(v => v.Id != variantId && v.Barcode == changes.Barcode))
        {
            return Result.Failure(CatalogDomainErrors.DuplicateBarcode);
        }

        var updated = variant.Update(changes, nowUtc);
        if (updated.IsSuccess)
        {
            UpdatedAtUtc = nowUtc;
        }

        return updated;
    }

    /// <summary>RN-023: um produto ativo não pode ficar sem nenhuma variação ativa.</summary>
    public Result SetVariantActive(VariantId variantId, bool isActive, DateTime nowUtc)
    {
        var variant = _variants.Find(v => v.Id == variantId);
        if (variant is null)
        {
            return Result.Failure(CatalogDomainErrors.VariantNotFound);
        }

        if (!isActive && IsActive && _variants.Count(v => v.IsActive) == 1 && variant.IsActive)
        {
            return Result.Failure(CatalogDomainErrors.ActiveProductNeedsVariant);
        }

        variant.SetActive(isActive, nowUtc);
        UpdatedAtUtc = nowUtc;
        return Result.Success();
    }

    /// <summary>RN-023: só entra na loja com ao menos uma variação ativa.</summary>
    public Result SetActive(bool isActive, DateTime nowUtc)
    {
        if (isActive && !_variants.Any(v => v.IsActive))
        {
            return Result.Failure(CatalogDomainErrors.ActiveProductNeedsVariant);
        }

        IsActive = isActive;
        UpdatedAtUtc = nowUtc;
        return Result.Success();
    }

    /// <summary>Preço que a loja cobra pela variação: o próprio ou o base do produto.</summary>
    public decimal EffectivePrice(ProductVariant variant) => variant.Price ?? BasePrice;

    private Result Apply(ProductContent content, DateTime nowUtc)
    {
        var name = content.Name.Trim();
        if (name.Length is < NameMinLength or > NameMaxLength)
        {
            return Result.Failure(CatalogDomainErrors.InvalidName);
        }

        // RN-020: preço efetivo > 0; o base vale para toda variação sem preço próprio.
        if (content.BasePrice <= 0)
        {
            return Result.Failure(CatalogDomainErrors.InvalidPrice);
        }

        if (!content.Package.IsValid)
        {
            return Result.Failure(CatalogDomainErrors.InvalidPackage);
        }

        CategoryId = content.CategoryId;
        Name = name;
        Slug = content.Slug;
        Description = content.Description.Trim();
        BasePrice = content.BasePrice;
        Package = content.Package;
        SeoTitle = string.IsNullOrWhiteSpace(content.SeoTitle) ? null : content.SeoTitle.Trim();
        SeoDescription = string.IsNullOrWhiteSpace(content.SeoDescription) ? null : content.SeoDescription.Trim();
        UpdatedAtUtc = nowUtc;
        return Result.Success();
    }
}

/// <summary>Combinação vendável: produto + cor + tamanho, com SKU, preço, código de barras e medidas próprios.</summary>
public sealed class ProductVariant : Entity<VariantId>
{
    public const int SkuMaxLength = 60;

    private ProductVariant()
    {
    }

    public ColorId? ColorId { get; private set; }
    public SizeId? SizeId { get; private set; }
    public string Sku { get; private set; } = string.Empty;
    public Gtin? Barcode { get; private set; }
    public decimal? Price { get; private set; }
    public PackageSize? Package { get; private set; }
    public bool IsActive { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    public static ProductVariant Rehydrate(
        VariantId id,
        ColorId? colorId,
        SizeId? sizeId,
        string sku,
        Gtin? barcode,
        decimal? price,
        PackageSize? package,
        bool isActive,
        DateTime createdAtUtc,
        DateTime updatedAtUtc) =>
        new()
        {
            Id = id,
            ColorId = colorId,
            SizeId = sizeId,
            Sku = sku,
            Barcode = barcode,
            Price = price,
            Package = package,
            IsActive = isActive,
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = updatedAtUtc,
        };

    internal static ProductVariant Create(NewVariant combination, DateTime nowUtc) =>
        new()
        {
            Id = VariantId.New(),
            ColorId = combination.ColorId,
            SizeId = combination.SizeId,
            Sku = combination.Sku.Trim(),
            IsActive = true,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
        };

    internal Result Update(VariantChanges changes, DateTime nowUtc)
    {
        var sku = changes.Sku.Trim();
        if (sku.Length is 0 or > SkuMaxLength)
        {
            return Result.Failure(CatalogDomainErrors.InvalidSku);
        }

        if (changes.Price is <= 0)
        {
            return Result.Failure(CatalogDomainErrors.InvalidPrice);
        }

        if (changes.Package is { IsValid: false })
        {
            return Result.Failure(CatalogDomainErrors.InvalidPackage);
        }

        Sku = sku;
        Price = changes.Price;
        Barcode = changes.Barcode;
        Package = changes.Package;
        UpdatedAtUtc = nowUtc;
        return Result.Success();
    }

    internal void SetActive(bool isActive, DateTime nowUtc)
    {
        IsActive = isActive;
        UpdatedAtUtc = nowUtc;
    }
}

public interface IProductRepository
{
    /// <summary>Carrega o produto com todas as variações (ativas e inativas).</summary>
    Task<Product?> GetByIdAsync(ProductId id, CancellationToken cancellationToken);

    Task<bool> SlugInUseAsync(Slug slug, ProductId? exceptId, CancellationToken cancellationToken);

    /// <summary>SKUs e códigos de barra são únicos na loja inteira (RF-056 CA1, RN-026), não só no produto.</summary>
    Task<bool> SkuInUseAsync(string sku, VariantId? exceptId, CancellationToken cancellationToken);

    Task<bool> BarcodeInUseAsync(Gtin barcode, VariantId? exceptId, CancellationToken cancellationToken);

    Task AddAsync(Product product, CancellationToken cancellationToken);

    /// <summary>Grava produto e variações (insere as novas). Falso quando a <c>version</c> mudou (concorrência otimista).</summary>
    Task<bool> UpdateAsync(Product product, CancellationToken cancellationToken);
}

/// <summary>Slugs antigos para o redirect 301 (RN-025, SEO-02).</summary>
public interface ISlugHistory
{
    /// <summary>O slug está livre para a entidade: nem em uso por outra, nem guardado como antigo de outra.</summary>
    Task<bool> BelongsToAnotherAsync(Slug slug, string entityType, Guid entityId, CancellationToken cancellationToken);

    /// <summary>Guarda o slug antigo e tira do histórico o slug que voltou a ser o atual da mesma entidade.</summary>
    Task RecordChangeAsync(string entityType, Guid entityId, Slug oldSlug, Slug newSlug, DateTime nowUtc, CancellationToken cancellationToken);
}
