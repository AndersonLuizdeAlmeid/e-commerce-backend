using Dapper;
using ECommerce.Application.Abstractions.Data;
using ECommerce.Application.Abstractions.Images;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Application.Abstractions.Outbox;
using ECommerce.Domain.Abstractions;
using ECommerce.Domain.Catalog;
using FluentValidation;

namespace ECommerce.Application.Catalog.Admin;

// RF-024 — coleções no painel: cadastro, capa e inclusão de peças em lote.

public sealed record AdminCollectionResponse(
    Guid Id,
    string Name,
    string Slug,
    string Description,
    string? SeoTitle,
    string? SeoDescription,
    string CardTheme,
    int Position,
    string? ImageUrl,
    bool IsActive,
    int ProductCount,
    int Version);

public sealed record ListAdminCollectionsQuery : IQuery<IReadOnlyList<AdminCollectionResponse>>;

internal sealed class ListAdminCollectionsHandler(IDbConnectionFactory connectionFactory)
    : IQueryHandler<ListAdminCollectionsQuery, IReadOnlyList<AdminCollectionResponse>>
{
    private const string Sql = """
        SELECT c.id, c.name, c.slug, c.description, c.seo_title, c.seo_description, c.card_theme, c.position,
               c.image_url, c.is_active, c.version,
               (SELECT count(*)::int FROM catalog.product_collections pc WHERE pc.collection_id = c.id) AS product_count
          FROM catalog.collections c
         ORDER BY c.position, c.name
        """;

    public async Task<Result<IReadOnlyList<AdminCollectionResponse>>> Handle(ListAdminCollectionsQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<Row>(new CommandDefinition(Sql, cancellationToken: cancellationToken));
        return rows
            .Select(r => new AdminCollectionResponse(
                r.Id, r.Name, r.Slug, r.Description, r.SeoTitle, r.SeoDescription, r.CardTheme, r.Position, r.ImageUrl, r.IsActive, r.ProductCount, r.Version))
            .ToList();
    }

    private sealed class Row
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Slug { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string? SeoTitle { get; init; }
        public string? SeoDescription { get; init; }
        public string CardTheme { get; init; } = string.Empty;
        public int Position { get; init; }
        public string? ImageUrl { get; init; }
        public bool IsActive { get; init; }
        public int ProductCount { get; init; }
        public int Version { get; init; }
    }
}

/// <summary>Campos do formulário de coleção. <see cref="Slug"/> vazio gera a partir do nome.</summary>
public sealed record CollectionInput(
    string Name,
    string? Slug,
    string? Description,
    string? SeoTitle,
    string? SeoDescription,
    string CardTheme,
    int Position);

public sealed record CreateCollectionCommand(CollectionInput Input) : ICommand<Guid>;

public sealed record UpdateCollectionCommand(Guid Id, int Version, CollectionInput Input) : ICommand;

public sealed record SetCollectionStatusCommand(Guid Id, bool IsActive) : ICommand;

public sealed record DeleteCollectionCommand(Guid Id) : ICommand;

public sealed record AddCollectionProductsCommand(Guid Id, IReadOnlyList<Guid> ProductIds) : ICommand<int>;

public sealed record RemoveCollectionProductsCommand(Guid Id, IReadOnlyList<Guid> ProductIds) : ICommand;

public sealed record SetCollectionImageCommand(Guid Id, byte[] Content) : ICommand<string>;

public sealed record RemoveCollectionImageCommand(Guid Id) : ICommand;

internal sealed class CollectionInputValidator : AbstractValidator<CollectionInput>
{
    public CollectionInputValidator()
    {
        RuleFor(x => x.Name).NotEmpty().Length(Collection.NameMinLength, Collection.NameMaxLength);
        RuleFor(x => x.Slug).MaximumLength(Domain.Catalog.Slug.MaxLength);
        RuleFor(x => x.Description).MaximumLength(CategoryInputValidator.DescriptionMaxLength);
        RuleFor(x => x.SeoTitle).MaximumLength(SeoDefaults.TitleMaxLength);
        RuleFor(x => x.SeoDescription).MaximumLength(SeoDefaults.DescriptionMaxLength);
        RuleFor(x => x.CardTheme)
            .Must(theme => Enum.TryParse<CardTheme>(theme, ignoreCase: false, out _) && !int.TryParse(theme, out _))
            .WithMessage("Tema inválido. Use Ivory ou Graphite.");
        RuleFor(x => x.Position).InclusiveBetween(0, 9999);
    }
}

internal sealed class CreateCollectionValidator : AbstractValidator<CreateCollectionCommand>
{
    public CreateCollectionValidator() => RuleFor(x => x.Input).SetValidator(new CollectionInputValidator());
}

internal sealed class UpdateCollectionValidator : AbstractValidator<UpdateCollectionCommand>
{
    public UpdateCollectionValidator() => RuleFor(x => x.Input).SetValidator(new CollectionInputValidator());
}

internal sealed class AddCollectionProductsValidator : AbstractValidator<AddCollectionProductsCommand>
{
    public AddCollectionProductsValidator() => RuleFor(x => x.ProductIds.Count).InclusiveBetween(1, CatalogLimits.MaxProductsPerBatch).OverridePropertyName("productIds");
}

internal sealed class RemoveCollectionProductsValidator : AbstractValidator<RemoveCollectionProductsCommand>
{
    public RemoveCollectionProductsValidator() => RuleFor(x => x.ProductIds.Count).InclusiveBetween(1, CatalogLimits.MaxProductsPerBatch).OverridePropertyName("productIds");
}

internal sealed class SetCollectionImageValidator : AbstractValidator<SetCollectionImageCommand>
{
    public SetCollectionImageValidator() => RuleFor(x => x.Content.Length).ValidImageSize();
}

internal static class CatalogLimits
{
    /// <summary>Uma página cheia da lista de produtos do painel.</summary>
    public const int MaxProductsPerBatch = ListAdminProductsQuery.MaxPageSize;

    public static IRuleBuilderOptions<T, int> ValidImageSize<T>(this IRuleBuilder<T, int> rule) =>
        rule.GreaterThan(0).WithMessage("Escolha um arquivo.")
            .LessThanOrEqualTo(UploadProductImageCommand.MaxBytes).WithMessage("A foto passa de 15 MB.")
            .OverridePropertyName("file");
}

internal sealed class CreateCollectionHandler(ICollectionRepository collections, ISlugHistory slugHistory, IOutbox outbox, TimeProvider time)
    : ICommandHandler<CreateCollectionCommand, Guid>
{
    public async Task<Result<Guid>> Handle(CreateCollectionCommand command, CancellationToken cancellationToken)
    {
        var content = await CollectionContentFactory.BuildAsync(command.Input, null, collections, slugHistory, cancellationToken);
        if (content.IsFailure)
        {
            return content.Error;
        }

        var collection = Collection.Create(content.Value, time.GetUtcNow().UtcDateTime);
        if (collection.IsFailure)
        {
            return collection.Error;
        }

        await collections.AddAsync(collection.Value, cancellationToken);
        await CatalogChanged.CollectionsAsync(outbox, cancellationToken);
        return collection.Value.Id.Value;
    }
}

internal sealed class UpdateCollectionHandler(ICollectionRepository collections, ISlugHistory slugHistory, IOutbox outbox, TimeProvider time)
    : ICommandHandler<UpdateCollectionCommand>
{
    public async Task<Result> Handle(UpdateCollectionCommand command, CancellationToken cancellationToken)
    {
        var id = new CollectionId(command.Id);
        var collection = await collections.GetByIdAsync(id, cancellationToken);
        if (collection is null)
        {
            return Result.Failure(CatalogErrors.CollectionNotFound);
        }

        if (collection.Version != command.Version)
        {
            return Result.Failure(CatalogErrors.ConcurrentChange);
        }

        var content = await CollectionContentFactory.BuildAsync(command.Input, id, collections, slugHistory, cancellationToken);
        if (content.IsFailure)
        {
            return Result.Failure(content.Error);
        }

        var now = time.GetUtcNow().UtcDateTime;
        var previousSlug = collection.Update(content.Value, now);
        if (previousSlug.IsFailure)
        {
            return Result.Failure(previousSlug.Error);
        }

        if (!await collections.UpdateAsync(collection, cancellationToken))
        {
            return Result.Failure(CatalogErrors.ConcurrentChange);
        }

        if (previousSlug.Value is not null)
        {
            await slugHistory.RecordChangeAsync(SlugEntityTypes.Collection, id.Value, previousSlug.Value, collection.Slug, now, cancellationToken);
        }

        await CatalogChanged.CollectionsAsync(outbox, cancellationToken);
        return Result.Success();
    }
}

internal sealed class SetCollectionStatusHandler(ICollectionRepository collections, IOutbox outbox, TimeProvider time)
    : ICommandHandler<SetCollectionStatusCommand>
{
    public async Task<Result> Handle(SetCollectionStatusCommand command, CancellationToken cancellationToken)
    {
        var collection = await collections.GetByIdAsync(new CollectionId(command.Id), cancellationToken);
        if (collection is null)
        {
            return Result.Failure(CatalogErrors.CollectionNotFound);
        }

        collection.SetActive(command.IsActive, time.GetUtcNow().UtcDateTime);
        if (!await collections.UpdateAsync(collection, cancellationToken))
        {
            return Result.Failure(CatalogErrors.ConcurrentChange);
        }

        await CatalogChanged.CollectionsAsync(outbox, cancellationToken);
        return Result.Success();
    }
}

/// <summary>RF-024 CA6: excluir tira só a coleção e os vínculos; as peças continuam na loja.</summary>
internal sealed class DeleteCollectionHandler(ICollectionRepository collections, IOutbox outbox) : ICommandHandler<DeleteCollectionCommand>
{
    public async Task<Result> Handle(DeleteCollectionCommand command, CancellationToken cancellationToken)
    {
        var id = new CollectionId(command.Id);
        var collection = await collections.GetByIdAsync(id, cancellationToken);
        if (collection is null)
        {
            return Result.Failure(CatalogErrors.CollectionNotFound);
        }

        await collections.DeleteAsync(id, cancellationToken);
        if (collection.ImageKey is not null)
        {
            await outbox.EnqueueAsync(DeleteStoredImage.Type, new DeleteStoredImage(collection.ImageKey), cancellationToken);
        }

        await CatalogChanged.CollectionsAsync(outbox, cancellationToken);
        return Result.Success();
    }
}

internal sealed class AddCollectionProductsHandler(ICollectionRepository collections, IOutbox outbox, TimeProvider time)
    : ICommandHandler<AddCollectionProductsCommand, int>
{
    public async Task<Result<int>> Handle(AddCollectionProductsCommand command, CancellationToken cancellationToken)
    {
        var id = new CollectionId(command.Id);
        if (await collections.GetByIdAsync(id, cancellationToken) is null)
        {
            return CatalogErrors.CollectionNotFound;
        }

        var added = await collections.AddProductsAsync(id, command.ProductIds, time.GetUtcNow().UtcDateTime, cancellationToken);
        await CatalogChanged.CollectionsAsync(outbox, cancellationToken);
        return added;
    }
}

internal sealed class RemoveCollectionProductsHandler(ICollectionRepository collections, IOutbox outbox)
    : ICommandHandler<RemoveCollectionProductsCommand>
{
    public async Task<Result> Handle(RemoveCollectionProductsCommand command, CancellationToken cancellationToken)
    {
        var id = new CollectionId(command.Id);
        if (await collections.GetByIdAsync(id, cancellationToken) is null)
        {
            return Result.Failure(CatalogErrors.CollectionNotFound);
        }

        await collections.RemoveProductsAsync(id, command.ProductIds, cancellationToken);
        await CatalogChanged.CollectionsAsync(outbox, cancellationToken);
        return Result.Success();
    }
}

internal sealed class SetCollectionImageHandler(
    ICollectionRepository collections, IImageProcessor processor, IImageStorage storage, IOutbox outbox, TimeProvider time)
    : ICommandHandler<SetCollectionImageCommand, string>
{
    public async Task<Result<string>> Handle(SetCollectionImageCommand command, CancellationToken cancellationToken)
    {
        var collection = await collections.GetByIdAsync(new CollectionId(command.Id), cancellationToken);
        if (collection is null)
        {
            return CatalogErrors.CollectionNotFound;
        }

        var processed = processor.Process(command.Content);
        if (processed.IsFailure)
        {
            return processed.Error;
        }

        var key = $"collections/{command.Id}/{Guid.CreateVersion7()}.webp";
        var url = storage.PublicUrl(key);
        var previousKey = collection.ReplaceImage(url, key, time.GetUtcNow().UtcDateTime);
        if (!await collections.UpdateAsync(collection, cancellationToken))
        {
            return CatalogErrors.ConcurrentChange;
        }

        if (previousKey is not null)
        {
            await outbox.EnqueueAsync(DeleteStoredImage.Type, new DeleteStoredImage(previousKey), cancellationToken);
        }

        await CatalogChanged.CollectionsAsync(outbox, cancellationToken);

        // Por último: se o envio falhar, a transação desfaz a linha e a capa antiga continua valendo.
        await storage.PutAsync(key, processed.Value.Content, ProcessedImage.ContentType, cancellationToken);
        return url;
    }
}

internal sealed class RemoveCollectionImageHandler(ICollectionRepository collections, IOutbox outbox, TimeProvider time)
    : ICommandHandler<RemoveCollectionImageCommand>
{
    public async Task<Result> Handle(RemoveCollectionImageCommand command, CancellationToken cancellationToken)
    {
        var collection = await collections.GetByIdAsync(new CollectionId(command.Id), cancellationToken);
        if (collection is null)
        {
            return Result.Failure(CatalogErrors.CollectionNotFound);
        }

        var previousKey = collection.ReplaceImage(null, null, time.GetUtcNow().UtcDateTime);
        if (!await collections.UpdateAsync(collection, cancellationToken))
        {
            return Result.Failure(CatalogErrors.ConcurrentChange);
        }

        if (previousKey is not null)
        {
            await outbox.EnqueueAsync(DeleteStoredImage.Type, new DeleteStoredImage(previousKey), cancellationToken);
        }

        await CatalogChanged.CollectionsAsync(outbox, cancellationToken);
        return Result.Success();
    }
}

internal static class CollectionContentFactory
{
    public static async Task<Result<CollectionContent>> BuildAsync(
        CollectionInput input, CollectionId? currentId, ICollectionRepository collections, ISlugHistory slugHistory, CancellationToken cancellationToken)
    {
        var slug = string.IsNullOrWhiteSpace(input.Slug) ? Slug.FromText(input.Name) : Slug.Create(input.Slug);
        if (slug.IsFailure)
        {
            return slug.Error;
        }

        if (await collections.NameInUseAsync(input.Name.Trim(), currentId, cancellationToken))
        {
            return CatalogErrors.CollectionNameInUse;
        }

        if (await collections.SlugInUseAsync(slug.Value, currentId, cancellationToken)
            || await slugHistory.BelongsToAnotherAsync(slug.Value, SlugEntityTypes.Collection, currentId?.Value ?? Guid.Empty, cancellationToken))
        {
            return CatalogErrors.SlugInUse;
        }

        return new CollectionContent(
            input.Name, slug.Value, input.Description ?? string.Empty, input.SeoTitle, input.SeoDescription,
            Enum.Parse<CardTheme>(input.CardTheme), input.Position);
    }
}
