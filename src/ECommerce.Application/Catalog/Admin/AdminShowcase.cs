using Dapper;
using ECommerce.Application.Abstractions.Data;
using ECommerce.Application.Abstractions.Images;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Application.Abstractions.Outbox;
using ECommerce.Domain.Abstractions;
using ECommerce.Domain.Catalog;
using FluentValidation;

namespace ECommerce.Application.Catalog.Admin;

// RF-025 — vitrine no painel: a peça em destaque da home e as ocasiões.

/// <summary><c>IsAvailable</c> falso: a home mostra a novidade mais recente no lugar dela (CA1), e o painel avisa.</summary>
public sealed record AdminFeaturedResponse(Guid ProductId, string ProductName, Guid? ColorId, bool IsAvailable);

public sealed record GetAdminFeaturedQuery : IQuery<AdminFeaturedResponse?>;

public sealed record SetFeaturedCommand(Guid? ProductId, Guid? ColorId) : ICommand;

internal sealed class GetAdminFeaturedHandler(IDbConnectionFactory connectionFactory) : IQueryHandler<GetAdminFeaturedQuery, AdminFeaturedResponse?>
{
    private const string Sql = $"""
        SELECT f.product_id, p.name AS product_name, f.color_id,
               p.is_active AND EXISTS ({ShowcaseSql.VariantWithStock}) AS is_available
          FROM catalog.showcase_featured f
          JOIN catalog.products p ON p.id = f.product_id
        """;

    public async Task<Result<AdminFeaturedResponse?>> Handle(GetAdminFeaturedQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(Sql, cancellationToken: cancellationToken));
        return Result.Success(row is null ? null : new AdminFeaturedResponse(row.ProductId, row.ProductName, row.ColorId, row.IsAvailable));
    }

    private sealed class Row
    {
        public Guid ProductId { get; init; }
        public string ProductName { get; init; } = string.Empty;
        public Guid? ColorId { get; init; }
        public bool IsAvailable { get; init; }
    }
}

internal sealed class SetFeaturedHandler(IProductRepository products, IShowcaseFeaturedRepository featured, IOutbox outbox, TimeProvider time)
    : ICommandHandler<SetFeaturedCommand>
{
    public async Task<Result> Handle(SetFeaturedCommand command, CancellationToken cancellationToken)
    {
        if (command.ProductId is not { } id)
        {
            await featured.ClearAsync(cancellationToken);
            await CatalogChanged.ShowcaseAsync(outbox, cancellationToken);
            return Result.Success();
        }

        var productId = new ProductId(id);
        var product = await products.GetByIdAsync(productId, cancellationToken);
        if (product is null)
        {
            return Result.Failure(CatalogErrors.ProductNotFound);
        }

        var colorId = command.ColorId is { } color ? new ColorId(color) : (ColorId?)null;
        if (colorId is not null && product.Variants.All(variant => variant.ColorId != colorId))
        {
            return Result.Failure(CatalogErrors.ColorNotInProduct);
        }

        await featured.SetAsync(productId, colorId, time.GetUtcNow().UtcDateTime, cancellationToken);
        await CatalogChanged.ShowcaseAsync(outbox, cancellationToken);
        return Result.Success();
    }
}

public sealed record AdminOccasionProductResponse(Guid Id, string Name, string? ImageUrl, bool IsActive);

public sealed record AdminOccasionResponse(
    Guid Id,
    string Name,
    string Text,
    string? ImageUrl,
    DateOnly StartsOn,
    DateOnly EndsOn,
    bool IsActive,
    int Version,
    IReadOnlyList<AdminOccasionProductResponse> Products);

public sealed record ListAdminOccasionsQuery : IQuery<IReadOnlyList<AdminOccasionResponse>>;

internal sealed class ListAdminOccasionsHandler(IDbConnectionFactory connectionFactory)
    : IQueryHandler<ListAdminOccasionsQuery, IReadOnlyList<AdminOccasionResponse>>
{
    private const string Sql = """
        SELECT id, name, short_text, image_url, starts_on, ends_on, is_active, version
          FROM catalog.occasions
         ORDER BY starts_on DESC, name;

        SELECT op.occasion_id, p.id, p.name, p.is_active,
               (SELECT i.url FROM catalog.product_images i WHERE i.product_id = p.id ORDER BY i.position LIMIT 1) AS image_url
          FROM catalog.occasion_products op
          JOIN catalog.products p ON p.id = op.product_id
         ORDER BY op.occasion_id, op.position;
        """;

    public async Task<Result<IReadOnlyList<AdminOccasionResponse>>> Handle(ListAdminOccasionsQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        using var grid = await connection.QueryMultipleAsync(new CommandDefinition(Sql, cancellationToken: cancellationToken));

        var occasions = (await grid.ReadAsync<OccasionRow>()).AsList();
        var products = (await grid.ReadAsync<ProductRow>())
            .ToLookup(p => p.OccasionId, p => new AdminOccasionProductResponse(p.Id, p.Name, p.ImageUrl, p.IsActive));

        return occasions
            .Select(o => new AdminOccasionResponse(
                o.Id, o.Name, o.ShortText, o.ImageUrl, DateOnly.FromDateTime(o.StartsOn), DateOnly.FromDateTime(o.EndsOn), o.IsActive, o.Version, products[o.Id].ToList()))
            .ToList();
    }

    private sealed class OccasionRow
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string ShortText { get; init; } = string.Empty;
        public string? ImageUrl { get; init; }
        public DateTime StartsOn { get; init; }
        public DateTime EndsOn { get; init; }
        public bool IsActive { get; init; }
        public int Version { get; init; }
    }

    private sealed class ProductRow
    {
        public Guid OccasionId { get; init; }
        public Guid Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public bool IsActive { get; init; }
        public string? ImageUrl { get; init; }
    }
}

public sealed record OccasionInput(string Name, string? Text, DateOnly StartsOn, DateOnly EndsOn, bool IsActive, IReadOnlyList<Guid> ProductIds)
{
    public OccasionContent ToContent() => new(Name, Text ?? string.Empty, StartsOn, EndsOn, IsActive, ProductIds);
}

public sealed record CreateOccasionCommand(OccasionInput Input) : ICommand<Guid>;

public sealed record UpdateOccasionCommand(Guid Id, int Version, OccasionInput Input) : ICommand;

public sealed record DeleteOccasionCommand(Guid Id) : ICommand;

public sealed record SetOccasionImageCommand(Guid Id, byte[] Content) : ICommand<string>;

public sealed record RemoveOccasionImageCommand(Guid Id) : ICommand;

internal sealed class OccasionInputValidator : AbstractValidator<OccasionInput>
{
    public OccasionInputValidator()
    {
        RuleFor(x => x.Name).NotEmpty().Length(Occasion.NameMinLength, Occasion.NameMaxLength);
        RuleFor(x => x.Text).MaximumLength(Occasion.TextMaxLength);
        RuleFor(x => x.EndsOn).GreaterThanOrEqualTo(x => x.StartsOn).WithMessage("O fim não pode ser antes do início.");
        RuleFor(x => x.ProductIds.Count).LessThanOrEqualTo(CatalogLimits.MaxProductsPerBatch).OverridePropertyName("productIds");
    }
}

internal sealed class CreateOccasionValidator : AbstractValidator<CreateOccasionCommand>
{
    public CreateOccasionValidator() => RuleFor(x => x.Input).SetValidator(new OccasionInputValidator());
}

internal sealed class UpdateOccasionValidator : AbstractValidator<UpdateOccasionCommand>
{
    public UpdateOccasionValidator() => RuleFor(x => x.Input).SetValidator(new OccasionInputValidator());
}

internal sealed class SetOccasionImageValidator : AbstractValidator<SetOccasionImageCommand>
{
    public SetOccasionImageValidator() => RuleFor(x => x.Content.Length).ValidImageSize();
}

internal sealed class CreateOccasionHandler(IOccasionRepository occasions, IOutbox outbox, TimeProvider time)
    : ICommandHandler<CreateOccasionCommand, Guid>
{
    public async Task<Result<Guid>> Handle(CreateOccasionCommand command, CancellationToken cancellationToken)
    {
        var refused = await OccasionRules.CheckAsync(command.Input, null, occasions, cancellationToken);
        if (refused is not null)
        {
            return refused;
        }

        var occasion = Occasion.Create(command.Input.ToContent(), time.GetUtcNow().UtcDateTime);
        if (occasion.IsFailure)
        {
            return occasion.Error;
        }

        await occasions.AddAsync(occasion.Value, cancellationToken);
        await CatalogChanged.ShowcaseAsync(outbox, cancellationToken);
        return occasion.Value.Id.Value;
    }
}

internal sealed class UpdateOccasionHandler(IOccasionRepository occasions, IOutbox outbox, TimeProvider time)
    : ICommandHandler<UpdateOccasionCommand>
{
    public async Task<Result> Handle(UpdateOccasionCommand command, CancellationToken cancellationToken)
    {
        var id = new OccasionId(command.Id);
        var occasion = await occasions.GetByIdAsync(id, cancellationToken);
        if (occasion is null)
        {
            return Result.Failure(CatalogErrors.OccasionNotFound);
        }

        if (occasion.Version != command.Version)
        {
            return Result.Failure(CatalogErrors.ConcurrentChange);
        }

        var refused = await OccasionRules.CheckAsync(command.Input, id, occasions, cancellationToken);
        if (refused is not null)
        {
            return Result.Failure(refused);
        }

        var updated = occasion.Update(command.Input.ToContent(), time.GetUtcNow().UtcDateTime);
        if (updated.IsFailure)
        {
            return updated;
        }

        if (!await occasions.UpdateAsync(occasion, cancellationToken))
        {
            return Result.Failure(CatalogErrors.ConcurrentChange);
        }

        await CatalogChanged.ShowcaseAsync(outbox, cancellationToken);
        return Result.Success();
    }
}

internal sealed class DeleteOccasionHandler(IOccasionRepository occasions, IOutbox outbox) : ICommandHandler<DeleteOccasionCommand>
{
    public async Task<Result> Handle(DeleteOccasionCommand command, CancellationToken cancellationToken)
    {
        var id = new OccasionId(command.Id);
        var occasion = await occasions.GetByIdAsync(id, cancellationToken);
        if (occasion is null)
        {
            return Result.Failure(CatalogErrors.OccasionNotFound);
        }

        await occasions.DeleteAsync(id, cancellationToken);
        if (occasion.ImageKey is not null)
        {
            await outbox.EnqueueAsync(DeleteStoredImage.Type, new DeleteStoredImage(occasion.ImageKey), cancellationToken);
        }

        await CatalogChanged.ShowcaseAsync(outbox, cancellationToken);
        return Result.Success();
    }
}

internal sealed class SetOccasionImageHandler(
    IOccasionRepository occasions, IImageProcessor processor, IImageStorage storage, IOutbox outbox, TimeProvider time)
    : ICommandHandler<SetOccasionImageCommand, string>
{
    public async Task<Result<string>> Handle(SetOccasionImageCommand command, CancellationToken cancellationToken)
    {
        var occasion = await occasions.GetByIdAsync(new OccasionId(command.Id), cancellationToken);
        if (occasion is null)
        {
            return CatalogErrors.OccasionNotFound;
        }

        var processed = processor.Process(command.Content);
        if (processed.IsFailure)
        {
            return processed.Error;
        }

        var key = $"occasions/{command.Id}/{Guid.CreateVersion7()}.webp";
        var url = storage.PublicUrl(key);
        var previousKey = occasion.ReplaceImage(url, key, time.GetUtcNow().UtcDateTime);
        if (!await occasions.UpdateAsync(occasion, cancellationToken))
        {
            return CatalogErrors.ConcurrentChange;
        }

        if (previousKey is not null)
        {
            await outbox.EnqueueAsync(DeleteStoredImage.Type, new DeleteStoredImage(previousKey), cancellationToken);
        }

        await CatalogChanged.ShowcaseAsync(outbox, cancellationToken);

        // Por último: se o envio falhar, a transação desfaz a linha e a imagem antiga continua valendo.
        await storage.PutAsync(key, processed.Value.Content, ProcessedImage.ContentType, cancellationToken);
        return url;
    }
}

internal sealed class RemoveOccasionImageHandler(IOccasionRepository occasions, IOutbox outbox, TimeProvider time)
    : ICommandHandler<RemoveOccasionImageCommand>
{
    public async Task<Result> Handle(RemoveOccasionImageCommand command, CancellationToken cancellationToken)
    {
        var occasion = await occasions.GetByIdAsync(new OccasionId(command.Id), cancellationToken);
        if (occasion is null)
        {
            return Result.Failure(CatalogErrors.OccasionNotFound);
        }

        var previousKey = occasion.ReplaceImage(null, null, time.GetUtcNow().UtcDateTime);
        if (!await occasions.UpdateAsync(occasion, cancellationToken))
        {
            return Result.Failure(CatalogErrors.ConcurrentChange);
        }

        if (previousKey is not null)
        {
            await outbox.EnqueueAsync(DeleteStoredImage.Type, new DeleteStoredImage(previousKey), cancellationToken);
        }

        await CatalogChanged.ShowcaseAsync(outbox, cancellationToken);
        return Result.Success();
    }
}

internal static class OccasionRules
{
    /// <summary>Período sem sobrepor outra ocasião ativa (RF-025 CA2) e peças que ainda existem.</summary>
    public static async Task<Error?> CheckAsync(OccasionInput input, OccasionId? currentId, IOccasionRepository occasions, CancellationToken cancellationToken)
    {
        if (input.IsActive && await occasions.OverlapsActiveAsync(input.StartsOn, input.EndsOn, currentId, cancellationToken))
        {
            return CatalogErrors.OccasionOverlap;
        }

        var productIds = input.ProductIds.Distinct().ToList();
        return await occasions.CountExistingProductsAsync(productIds, cancellationToken) == productIds.Count
            ? null
            : CatalogErrors.ProductNotFound;
    }
}

/// <summary>Trechos de SQL que o painel e a loja precisam entender igual.</summary>
internal static class ShowcaseSql
{
    /// <summary>"Tem estoque" do RF-025 CA1, para <c>p</c> = produto.</summary>
    public const string VariantWithStock = """
        SELECT 1
          FROM catalog.product_variants v
          JOIN inventory.stock_items s ON s.variant_id = v.id
         WHERE v.product_id = p.id AND v.is_active AND s.quantity_on_hand > 0
        """;
}
