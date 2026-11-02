using Dapper;
using ECommerce.Application.Abstractions.Data;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Application.Abstractions.Outbox;
using ECommerce.Application.Abstractions.Paging;
using ECommerce.Domain.Abstractions;
using ECommerce.Domain.Catalog;
using FluentValidation;

namespace ECommerce.Application.Catalog.Admin;

// RF-051 (produtos), RF-056 (grade de variações) e RF-059 (código de barras). A entrada de estoque por leitura é da
// Fase 3. As fotos ficam em AdminProductImages (RF-061).

public sealed record AdminProductSummaryResponse(
    Guid Id,
    string Name,
    string Slug,
    string CategoryName,
    decimal BasePrice,
    bool IsActive,
    int VariantCount,
    int ImageCount,
    string? ImageUrl,
    DateTime UpdatedAt);

public sealed record AdminProductResponse(
    Guid Id,
    Guid CategoryId,
    string Name,
    string Slug,
    string Description,
    decimal BasePrice,
    int WeightGrams,
    decimal HeightCm,
    decimal WidthCm,
    decimal LengthCm,
    string? SeoTitle,
    string? SeoDescription,
    bool IsActive,
    int Version,
    VariantLabelsResponse VariantLabels,
    IReadOnlyList<AdminVariantResponse> Variants,
    IReadOnlyList<AdminProductImageResponse> Images,
    IReadOnlyList<Guid> CollectionIds);

/// <summary><c>Price</c> nulo herda o preço base; <c>QuantityOnHand</c> é só leitura aqui (ajustes na Fase 3).</summary>
public sealed record AdminVariantResponse(
    Guid Id,
    Guid? ColorId,
    string? ColorName,
    Guid? SizeId,
    string? SizeName,
    string Sku,
    string? Barcode,
    decimal? Price,
    int? WeightGrams,
    decimal? HeightCm,
    decimal? WidthCm,
    decimal? LengthCm,
    bool IsActive,
    int QuantityOnHand);

public sealed record AdminProductImageResponse(Guid Id, string Url, string Alt, Guid? ColorId, short Position, int? Width, int? Height);

public sealed record VariantByBarcodeResponse(
    Guid VariantId, Guid ProductId, string ProductName, string? ColorName, string? SizeName, string Sku, string Barcode);

public sealed record ListAdminProductsQuery(int Page, int PageSize, string? Search, Guid? CategoryId, bool? IsActive, Guid? CollectionId = null)
    : IQuery<PagedResponse<AdminProductSummaryResponse>>
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;
}

public sealed record GetAdminProductQuery(Guid Id) : IQuery<AdminProductResponse>;

public sealed record FindVariantByBarcodeQuery(string Code) : IQuery<VariantByBarcodeResponse>;

/// <summary>Campos do formulário de produto. <see cref="Slug"/> vazio gera a partir do nome.</summary>
public sealed record ProductInput(
    Guid CategoryId,
    string Name,
    string? Slug,
    string? Description,
    decimal BasePrice,
    int WeightGrams,
    decimal HeightCm,
    decimal WidthCm,
    decimal LengthCm,
    string? SeoTitle,
    string? SeoDescription);

public sealed record CreateProductCommand(ProductInput Input) : ICommand<Guid>;

public sealed record UpdateProductCommand(Guid Id, int Version, ProductInput Input) : ICommand;

public sealed record SetProductStatusCommand(Guid Id, bool IsActive) : ICommand;

/// <summary>Devolve quantas combinações foram criadas; as que já existem ficam como estão.</summary>
public sealed record GenerateVariantsCommand(Guid ProductId, IReadOnlyList<Guid> ColorIds, IReadOnlyList<Guid> SizeIds) : ICommand<int>;

public sealed record UpdateVariantCommand(
    Guid ProductId,
    Guid VariantId,
    string Sku,
    decimal? Price,
    string? Barcode,
    int? WeightGrams,
    decimal? HeightCm,
    decimal? WidthCm,
    decimal? LengthCm) : ICommand;

public sealed record SetVariantStatusCommand(Guid ProductId, Guid VariantId, bool IsActive) : ICommand;

internal sealed class ListAdminProductsValidator : AbstractValidator<ListAdminProductsQuery>
{
    public ListAdminProductsValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, ListAdminProductsQuery.MaxPageSize);
        RuleFor(x => x.Search).MaximumLength(100);
    }
}

internal sealed class ProductInputValidator : AbstractValidator<ProductInput>
{
    public const int DescriptionMaxLength = 5000;

    public ProductInputValidator()
    {
        RuleFor(x => x.CategoryId).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().Length(Product.NameMinLength, Product.NameMaxLength);
        RuleFor(x => x.Slug).MaximumLength(Domain.Catalog.Slug.MaxLength);
        RuleFor(x => x.Description).MaximumLength(DescriptionMaxLength);
        RuleFor(x => x.BasePrice).GreaterThan(0);
        RuleFor(x => x.WeightGrams).GreaterThan(0);
        RuleFor(x => x.HeightCm).GreaterThan(0);
        RuleFor(x => x.WidthCm).GreaterThan(0);
        RuleFor(x => x.LengthCm).GreaterThan(0);
        RuleFor(x => x.SeoTitle).MaximumLength(SeoDefaults.TitleMaxLength);
        RuleFor(x => x.SeoDescription).MaximumLength(SeoDefaults.DescriptionMaxLength);
    }
}

internal sealed class CreateProductValidator : AbstractValidator<CreateProductCommand>
{
    public CreateProductValidator() => RuleFor(x => x.Input).SetValidator(new ProductInputValidator());
}

internal sealed class UpdateProductValidator : AbstractValidator<UpdateProductCommand>
{
    public UpdateProductValidator() => RuleFor(x => x.Input).SetValidator(new ProductInputValidator());
}

internal sealed class GenerateVariantsValidator : AbstractValidator<GenerateVariantsCommand>
{
    private const int MaxValuesPerAxis = 50;

    public GenerateVariantsValidator()
    {
        RuleFor(x => x.ColorIds.Count).LessThanOrEqualTo(MaxValuesPerAxis).OverridePropertyName("colorIds");
        RuleFor(x => x.SizeIds.Count).LessThanOrEqualTo(MaxValuesPerAxis).OverridePropertyName("sizeIds");
    }
}

internal sealed class UpdateVariantValidator : AbstractValidator<UpdateVariantCommand>
{
    public UpdateVariantValidator()
    {
        RuleFor(x => x.Sku).NotEmpty().MaximumLength(ProductVariant.SkuMaxLength);
        RuleFor(x => x.Price).GreaterThan(0).When(x => x.Price is not null);

        // Medidas próprias vêm completas ou não vêm: meio pacote não serve para cotar frete.
        RuleFor(x => x.WeightGrams).NotNull().GreaterThan(0).When(HasAnyPackageField).WithMessage(CatalogDomainErrors.InvalidPackage.Message);
        RuleFor(x => x.HeightCm).NotNull().GreaterThan(0).When(HasAnyPackageField).WithMessage(CatalogDomainErrors.InvalidPackage.Message);
        RuleFor(x => x.WidthCm).NotNull().GreaterThan(0).When(HasAnyPackageField).WithMessage(CatalogDomainErrors.InvalidPackage.Message);
        RuleFor(x => x.LengthCm).NotNull().GreaterThan(0).When(HasAnyPackageField).WithMessage(CatalogDomainErrors.InvalidPackage.Message);
    }

    private static bool HasAnyPackageField(UpdateVariantCommand x) =>
        x.WeightGrams is not null || x.HeightCm is not null || x.WidthCm is not null || x.LengthCm is not null;
}

internal sealed class ListAdminProductsHandler(IDbConnectionFactory connectionFactory)
    : IQueryHandler<ListAdminProductsQuery, PagedResponse<AdminProductSummaryResponse>>
{
    private const string Filters = """
         WHERE (@CategoryId::uuid IS NULL OR p.category_id = @CategoryId OR c.parent_id = @CategoryId)
           AND (@IsActive::boolean IS NULL OR p.is_active = @IsActive)
           AND (@CollectionId::uuid IS NULL OR EXISTS (
                SELECT 1 FROM catalog.product_collections pc WHERE pc.product_id = p.id AND pc.collection_id = @CollectionId))
           AND (@Search::text IS NULL
                OR public.immutable_unaccent(lower(p.name)) LIKE '%' || public.immutable_unaccent(lower(@Search)) || '%'
                OR EXISTS (SELECT 1 FROM catalog.product_variants sv
                            WHERE sv.product_id = p.id AND (sv.sku ILIKE '%' || @Search || '%' OR sv.barcode = @Search)))
        """;

    private const string Sql = $"""
        SELECT count(*)
          FROM catalog.products p
          JOIN catalog.categories c ON c.id = p.category_id
        {Filters};

        SELECT p.id, p.name, p.slug, c.name AS category_name, p.base_price, p.is_active, p.updated_at,
               (SELECT count(*)::int FROM catalog.product_variants v WHERE v.product_id = p.id) AS variant_count,
               (SELECT count(*)::int FROM catalog.product_images i WHERE i.product_id = p.id) AS image_count,
               (SELECT i.url FROM catalog.product_images i WHERE i.product_id = p.id ORDER BY i.position LIMIT 1) AS image_url
          FROM catalog.products p
          JOIN catalog.categories c ON c.id = p.category_id
        {Filters}
         ORDER BY p.updated_at DESC, p.id
         LIMIT @PageSize OFFSET @Offset;
        """;

    public async Task<Result<PagedResponse<AdminProductSummaryResponse>>> Handle(ListAdminProductsQuery query, CancellationToken cancellationToken)
    {
        var search = string.IsNullOrWhiteSpace(query.Search)
            ? null
            : query.Search.Trim().Replace(@"\", @"\\", StringComparison.Ordinal).Replace("%", @"\%", StringComparison.Ordinal).Replace("_", @"\_", StringComparison.Ordinal);

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        using var grid = await connection.QueryMultipleAsync(new CommandDefinition(
            Sql,
            new { query.CategoryId, query.IsActive, query.CollectionId, Search = search, query.PageSize, Offset = (query.Page - 1) * query.PageSize },
            cancellationToken: cancellationToken));

        var total = await grid.ReadSingleAsync<int>();
        var items = (await grid.ReadAsync<Row>())
            .Select(r => new AdminProductSummaryResponse(
                r.Id, r.Name, r.Slug, r.CategoryName, r.BasePrice, r.IsActive, r.VariantCount, r.ImageCount, r.ImageUrl,
                DateTime.SpecifyKind(r.UpdatedAt, DateTimeKind.Utc)))
            .ToList();

        return PagedResponse.Create<AdminProductSummaryResponse>(items, query.Page, query.PageSize, total);
    }

    private sealed class Row
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Slug { get; init; } = string.Empty;
        public string CategoryName { get; init; } = string.Empty;
        public decimal BasePrice { get; init; }
        public bool IsActive { get; init; }
        public int VariantCount { get; init; }
        public int ImageCount { get; init; }
        public string? ImageUrl { get; init; }
        public DateTime UpdatedAt { get; init; }
    }
}

internal sealed class GetAdminProductHandler(IDbConnectionFactory connectionFactory) : IQueryHandler<GetAdminProductQuery, AdminProductResponse>
{
    private const string Sql = $"""
        SELECT p.id, p.category_id, p.name, p.slug, p.description, p.base_price, p.weight_grams, p.height_cm, p.width_cm, p.length_cm,
               p.seo_title, p.seo_description, p.is_active, p.version, {VariantLabelsResponse.SqlColumns}
          FROM catalog.products p
          JOIN catalog.categories c ON c.id = p.category_id
          {VariantLabelsResponse.SqlParentJoin}
         WHERE p.id = @Id;

        SELECT v.id, v.color_id, co.name AS color_name, v.size_id, s.name AS size_name, v.sku, v.barcode, v.price,
               v.weight_grams, v.height_cm, v.width_cm, v.length_cm, v.is_active,
               coalesce(st.quantity_on_hand, 0) AS quantity_on_hand
          FROM catalog.product_variants v
          LEFT JOIN catalog.colors co ON co.id = v.color_id
          LEFT JOIN catalog.sizes s ON s.id = v.size_id
          LEFT JOIN inventory.stock_items st ON st.variant_id = v.id
         WHERE v.product_id = @Id
         ORDER BY co.name NULLS FIRST, s.sort_order NULLS FIRST;

        SELECT id, url, alt, color_id, position, width, height
          FROM catalog.product_images
         WHERE product_id = @Id
         ORDER BY color_id NULLS FIRST, position;

        SELECT collection_id FROM catalog.product_collections WHERE product_id = @Id;
        """;

    public async Task<Result<AdminProductResponse>> Handle(GetAdminProductQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        using var grid = await connection.QueryMultipleAsync(new CommandDefinition(Sql, new { query.Id }, cancellationToken: cancellationToken));

        var product = await grid.ReadSingleOrDefaultAsync<ProductRow>();
        if (product is null)
        {
            return CatalogErrors.ProductNotFound;
        }

        var variants = (await grid.ReadAsync<VariantRow>())
            .Select(v => new AdminVariantResponse(
                v.Id, v.ColorId, v.ColorName, v.SizeId, v.SizeName, v.Sku, v.Barcode, v.Price,
                v.WeightGrams, v.HeightCm, v.WidthCm, v.LengthCm, v.IsActive, v.QuantityOnHand))
            .ToList();
        var images = (await grid.ReadAsync<ImageRow>())
            .Select(i => new AdminProductImageResponse(i.Id, i.Url, i.Alt, i.ColorId, i.Position, i.Width, i.Height))
            .ToList();
        var collectionIds = (await grid.ReadAsync<Guid>()).ToList();

        return new AdminProductResponse(
            product.Id, product.CategoryId, product.Name, product.Slug, product.Description, product.BasePrice,
            product.WeightGrams, product.HeightCm, product.WidthCm, product.LengthCm, product.SeoTitle, product.SeoDescription,
            product.IsActive, product.Version, new VariantLabelsResponse(product.ColorLabel, product.SizeLabel), variants, images, collectionIds);
    }

    private sealed class ProductRow
    {
        public Guid Id { get; init; }
        public Guid CategoryId { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Slug { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public decimal BasePrice { get; init; }
        public int WeightGrams { get; init; }
        public decimal HeightCm { get; init; }
        public decimal WidthCm { get; init; }
        public decimal LengthCm { get; init; }
        public string? SeoTitle { get; init; }
        public string? SeoDescription { get; init; }
        public bool IsActive { get; init; }
        public int Version { get; init; }
        public string ColorLabel { get; init; } = string.Empty;
        public string SizeLabel { get; init; } = string.Empty;
    }

    private sealed class VariantRow
    {
        public Guid Id { get; init; }
        public Guid? ColorId { get; init; }
        public string? ColorName { get; init; }
        public Guid? SizeId { get; init; }
        public string? SizeName { get; init; }
        public string Sku { get; init; } = string.Empty;
        public string? Barcode { get; init; }
        public decimal? Price { get; init; }
        public int? WeightGrams { get; init; }
        public decimal? HeightCm { get; init; }
        public decimal? WidthCm { get; init; }
        public decimal? LengthCm { get; init; }
        public bool IsActive { get; init; }
        public int QuantityOnHand { get; init; }
    }

    private sealed class ImageRow
    {
        public Guid Id { get; init; }
        public string Url { get; init; } = string.Empty;
        public string Alt { get; init; } = string.Empty;
        public Guid? ColorId { get; init; }
        public short Position { get; init; }
        public int? Width { get; init; }
        public int? Height { get; init; }
    }
}

/// <summary>RF-059: localiza a variação pelo código lido. Código com dígito errado → 400 (CA2); sem dono → 404 (CA1).</summary>
internal sealed class FindVariantByBarcodeHandler(IDbConnectionFactory connectionFactory) : IQueryHandler<FindVariantByBarcodeQuery, VariantByBarcodeResponse>
{
    private const string Sql = """
        SELECT v.id AS variant_id, v.product_id, p.name AS product_name, co.name AS color_name, s.name AS size_name, v.sku, v.barcode
          FROM catalog.product_variants v
          JOIN catalog.products p ON p.id = v.product_id
          LEFT JOIN catalog.colors co ON co.id = v.color_id
          LEFT JOIN catalog.sizes s ON s.id = v.size_id
         WHERE v.barcode = @Barcode
        """;

    public async Task<Result<VariantByBarcodeResponse>> Handle(FindVariantByBarcodeQuery query, CancellationToken cancellationToken)
    {
        var barcode = Gtin.Create(query.Code);
        if (barcode.IsFailure)
        {
            return barcode.Error;
        }

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<Row>(
            new CommandDefinition(Sql, new { Barcode = barcode.Value.Value }, cancellationToken: cancellationToken));

        return row is null
            ? CatalogErrors.BarcodeNotFound
            : new VariantByBarcodeResponse(row.VariantId, row.ProductId, row.ProductName, row.ColorName, row.SizeName, row.Sku, row.Barcode);
    }

    private sealed class Row
    {
        public Guid VariantId { get; init; }
        public Guid ProductId { get; init; }
        public string ProductName { get; init; } = string.Empty;
        public string? ColorName { get; init; }
        public string? SizeName { get; init; }
        public string Sku { get; init; } = string.Empty;
        public string Barcode { get; init; } = string.Empty;
    }
}

internal sealed class CreateProductHandler(
    IProductRepository products, ICategoryRepository categories, ISlugHistory slugHistory, IOutbox outbox, TimeProvider time)
    : ICommandHandler<CreateProductCommand, Guid>
{
    public async Task<Result<Guid>> Handle(CreateProductCommand command, CancellationToken cancellationToken)
    {
        var content = await ProductContentFactory.BuildAsync(command.Input, null, products, categories, slugHistory, cancellationToken);
        if (content.IsFailure)
        {
            return content.Error;
        }

        var product = Product.Create(content.Value, time.GetUtcNow().UtcDateTime);
        if (product.IsFailure)
        {
            return product.Error;
        }

        await products.AddAsync(product.Value, cancellationToken);
        await CatalogChanged.ProductsAsync(outbox, cancellationToken);
        return product.Value.Id.Value;
    }
}

internal sealed class UpdateProductHandler(
    IProductRepository products, ICategoryRepository categories, ISlugHistory slugHistory, IOutbox outbox, TimeProvider time)
    : ICommandHandler<UpdateProductCommand>
{
    public async Task<Result> Handle(UpdateProductCommand command, CancellationToken cancellationToken)
    {
        var id = new ProductId(command.Id);
        var product = await products.GetByIdAsync(id, cancellationToken);
        if (product is null)
        {
            return Result.Failure(CatalogErrors.ProductNotFound);
        }

        if (product.Version != command.Version)
        {
            return Result.Failure(CatalogErrors.ConcurrentChange);
        }

        var content = await ProductContentFactory.BuildAsync(command.Input, id, products, categories, slugHistory, cancellationToken);
        if (content.IsFailure)
        {
            return Result.Failure(content.Error);
        }

        var now = time.GetUtcNow().UtcDateTime;
        var previousSlug = product.Update(content.Value, now);
        if (previousSlug.IsFailure)
        {
            return Result.Failure(previousSlug.Error);
        }

        if (!await products.UpdateAsync(product, cancellationToken))
        {
            return Result.Failure(CatalogErrors.ConcurrentChange);
        }

        if (previousSlug.Value is not null)
        {
            await slugHistory.RecordChangeAsync(SlugEntityTypes.Product, id.Value, previousSlug.Value, product.Slug, now, cancellationToken);
        }

        await CatalogChanged.ProductsAsync(outbox, cancellationToken);
        return Result.Success();
    }
}

internal sealed class SetProductStatusHandler(IProductRepository products, IProductImageRepository images, IOutbox outbox, TimeProvider time)
    : ICommandHandler<SetProductStatusCommand>
{
    public async Task<Result> Handle(SetProductStatusCommand command, CancellationToken cancellationToken)
    {
        var product = await products.GetByIdAsync(new ProductId(command.Id), cancellationToken);
        if (product is null)
        {
            return Result.Failure(CatalogErrors.ProductNotFound);
        }

        // RF-051 CA3: a variação é regra do agregado; a foto mora em outro repositório, então a conferência fica aqui.
        if (command.IsActive && await images.CountForProductAsync(product.Id, cancellationToken) == 0)
        {
            return Result.Failure(CatalogErrors.ActiveProductNeedsImage);
        }

        var changed = product.SetActive(command.IsActive, time.GetUtcNow().UtcDateTime);
        if (changed.IsFailure)
        {
            return changed;
        }

        return await ProductSaver.SaveAsync(product, products, outbox, cancellationToken);
    }
}

internal sealed class GenerateVariantsHandler(
    IProductRepository products, IColorRepository colors, ISizeRepository sizes, IOutbox outbox, TimeProvider time)
    : ICommandHandler<GenerateVariantsCommand, int>
{
    public async Task<Result<int>> Handle(GenerateVariantsCommand command, CancellationToken cancellationToken)
    {
        var product = await products.GetByIdAsync(new ProductId(command.ProductId), cancellationToken);
        if (product is null)
        {
            return CatalogErrors.ProductNotFound;
        }

        var colorAxis = new List<Color?>();
        foreach (var colorId in command.ColorIds.Distinct())
        {
            var color = await colors.GetByIdAsync(new ColorId(colorId), cancellationToken);
            if (color is null)
            {
                return CatalogErrors.ColorNotFound;
            }

            colorAxis.Add(color);
        }

        var sizeAxis = new List<Size?>();
        foreach (var sizeId in command.SizeIds.Distinct())
        {
            var size = await sizes.GetByIdAsync(new SizeId(sizeId), cancellationToken);
            if (size is null)
            {
                return CatalogErrors.SizeNotFound;
            }

            sizeAxis.Add(size);
        }

        // Eixo vazio vale como "sem" esse eixo; os dois vazios dão a variação única (RN-022).
        if (colorAxis.Count == 0)
        {
            colorAxis.Add(null);
        }

        if (sizeAxis.Count == 0)
        {
            sizeAxis.Add(null);
        }

        var reserved = new HashSet<string>(product.Variants.Select(v => v.Sku), StringComparer.OrdinalIgnoreCase);
        var combinations = new List<NewVariant>();
        foreach (var color in colorAxis)
        {
            foreach (var size in sizeAxis)
            {
                if (product.Variants.Any(v => v.ColorId == color?.Id && v.SizeId == size?.Id))
                {
                    continue;
                }

                var sku = await UniqueSkuAsync(product.Slug, color, size, reserved, cancellationToken);
                reserved.Add(sku);
                combinations.Add(new NewVariant(color?.Id, size?.Id, sku));
            }
        }

        var added = product.AddVariants(combinations, time.GetUtcNow().UtcDateTime);
        if (added.IsFailure)
        {
            return added.Error;
        }

        var saved = await ProductSaver.SaveAsync(product, products, outbox, cancellationToken);
        return saved.IsSuccess ? added.Value.Count : saved.Error;
    }

    /// <summary>"ANEL-SOLITARIO-DOURADO-16"; repete com "-2", "-3"... se já existir na loja (RF-056 CA1).</summary>
    private async Task<string> UniqueSkuAsync(Slug productSlug, Color? color, Size? size, HashSet<string> reserved, CancellationToken cancellationToken)
    {
        const int MaxProductPart = 30;

        var productPart = productSlug.Value.Length <= MaxProductPart ? productSlug.Value : productSlug.Value[..MaxProductPart].TrimEnd('-');
        var sizePart = size is null ? null : Slug.FromText(size.Name) is { IsSuccess: true } slug ? slug.Value.Value : null;
        var parts = new[] { productPart, color?.Slug.Value, sizePart }.Where(part => part is not null);
        var candidate = string.Join('-', parts).ToUpperInvariant();
        if (candidate.Length > ProductVariant.SkuMaxLength - 4)
        {
            candidate = candidate[..(ProductVariant.SkuMaxLength - 4)];
        }

        var sku = candidate;
        for (var suffix = 2; reserved.Contains(sku) || await products.SkuInUseAsync(sku, null, cancellationToken); suffix++)
        {
            sku = $"{candidate}-{suffix}";
        }

        return sku;
    }
}

internal sealed class UpdateVariantHandler(IProductRepository products, IOutbox outbox, TimeProvider time) : ICommandHandler<UpdateVariantCommand>
{
    public async Task<Result> Handle(UpdateVariantCommand command, CancellationToken cancellationToken)
    {
        var product = await products.GetByIdAsync(new ProductId(command.ProductId), cancellationToken);
        if (product is null)
        {
            return Result.Failure(CatalogErrors.ProductNotFound);
        }

        Gtin? barcode = null;
        if (!string.IsNullOrWhiteSpace(command.Barcode))
        {
            var parsed = Gtin.Create(command.Barcode);
            if (parsed.IsFailure)
            {
                return Result.Failure(parsed.Error);
            }

            barcode = parsed.Value;
        }

        var variantId = new VariantId(command.VariantId);
        if (await products.SkuInUseAsync(command.Sku.Trim(), variantId, cancellationToken))
        {
            return Result.Failure(CatalogDomainErrors.DuplicateSku);
        }

        if (barcode is not null && await products.BarcodeInUseAsync(barcode, variantId, cancellationToken))
        {
            return Result.Failure(CatalogDomainErrors.DuplicateBarcode);
        }

        var package = command.WeightGrams is { } weight && command.HeightCm is { } height && command.WidthCm is { } width && command.LengthCm is { } length
            ? new PackageSize(weight, height, width, length)
            : null;

        var updated = product.UpdateVariant(
            variantId, new VariantChanges(command.Sku, command.Price, barcode, package), time.GetUtcNow().UtcDateTime);
        if (updated.IsFailure)
        {
            return updated;
        }

        return await ProductSaver.SaveAsync(product, products, outbox, cancellationToken);
    }
}

internal sealed class SetVariantStatusHandler(IProductRepository products, IOutbox outbox, TimeProvider time) : ICommandHandler<SetVariantStatusCommand>
{
    public async Task<Result> Handle(SetVariantStatusCommand command, CancellationToken cancellationToken)
    {
        var product = await products.GetByIdAsync(new ProductId(command.ProductId), cancellationToken);
        if (product is null)
        {
            return Result.Failure(CatalogErrors.ProductNotFound);
        }

        var changed = product.SetVariantActive(new VariantId(command.VariantId), command.IsActive, time.GetUtcNow().UtcDateTime);
        if (changed.IsFailure)
        {
            return changed;
        }

        return await ProductSaver.SaveAsync(product, products, outbox, cancellationToken);
    }
}

internal static class ProductSaver
{
    public static async Task<Result> SaveAsync(Product product, IProductRepository products, IOutbox outbox, CancellationToken cancellationToken)
    {
        if (!await products.UpdateAsync(product, cancellationToken))
        {
            return Result.Failure(CatalogErrors.ConcurrentChange);
        }

        await CatalogChanged.ProductsAsync(outbox, cancellationToken);
        return Result.Success();
    }
}

internal static class ProductContentFactory
{
    /// <summary>Monta o conteúdo do domínio conferindo categoria e slug (formato, uso e histórico).</summary>
    public static async Task<Result<ProductContent>> BuildAsync(
        ProductInput input,
        ProductId? currentId,
        IProductRepository products,
        ICategoryRepository categories,
        ISlugHistory slugHistory,
        CancellationToken cancellationToken)
    {
        if (await categories.GetByIdAsync(new CategoryId(input.CategoryId), cancellationToken) is null)
        {
            return CatalogErrors.CategoryNotFound;
        }

        var slug = string.IsNullOrWhiteSpace(input.Slug) ? Slug.FromText(input.Name) : Slug.Create(input.Slug);
        if (slug.IsFailure)
        {
            return slug.Error;
        }

        if (await products.SlugInUseAsync(slug.Value, currentId, cancellationToken)
            || await slugHistory.BelongsToAnotherAsync(slug.Value, SlugEntityTypes.Product, currentId?.Value ?? Guid.Empty, cancellationToken))
        {
            return CatalogErrors.SlugInUse;
        }

        return new ProductContent(
            new CategoryId(input.CategoryId),
            input.Name,
            slug.Value,
            input.Description ?? string.Empty,
            input.BasePrice,
            new PackageSize(input.WeightGrams, input.HeightCm, input.WidthCm, input.LengthCm),
            input.SeoTitle,
            input.SeoDescription);
    }
}
