using Dapper;
using ECommerce.Application.Abstractions.Data;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Application.Abstractions.Outbox;
using ECommerce.Domain.Abstractions;
using ECommerce.Domain.Catalog;
using FluentValidation;

namespace ECommerce.Application.Catalog.Admin;

// RF-050 — categorias no painel: listar todas (ativas e inativas), criar, editar, ativar/desativar e excluir.

public sealed record AdminCategoryResponse(
    Guid Id,
    Guid? ParentId,
    string Name,
    string Slug,
    string Description,
    string? SeoTitle,
    string? SeoDescription,
    string? ColorLabel,
    string? SizeLabel,
    bool IsActive,
    int ProductCount,
    int Version);

public sealed record ListAdminCategoriesQuery : IQuery<IReadOnlyList<AdminCategoryResponse>>;

internal sealed class ListAdminCategoriesHandler(IDbConnectionFactory connectionFactory)
    : IQueryHandler<ListAdminCategoriesQuery, IReadOnlyList<AdminCategoryResponse>>
{
    // Mães primeiro e, logo abaixo de cada uma, as filhas: a ordem em que o painel desenha a árvore.
    private const string Sql = """
        SELECT c.id, c.parent_id, c.name, c.slug, c.description, c.seo_title, c.seo_description,
               c.color_label, c.size_label, c.is_active, c.version,
               (SELECT count(*)::int FROM catalog.products p WHERE p.category_id = c.id) AS product_count
          FROM catalog.categories c
          LEFT JOIN catalog.categories parent ON parent.id = c.parent_id
         ORDER BY coalesce(parent.name, c.name), c.parent_id NULLS FIRST, c.name
        """;

    public async Task<Result<IReadOnlyList<AdminCategoryResponse>>> Handle(ListAdminCategoriesQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<Row>(new CommandDefinition(Sql, cancellationToken: cancellationToken));
        return rows
            .Select(r => new AdminCategoryResponse(
                r.Id, r.ParentId, r.Name, r.Slug, r.Description, r.SeoTitle, r.SeoDescription, r.ColorLabel, r.SizeLabel, r.IsActive, r.ProductCount, r.Version))
            .ToList();
    }

    private sealed class Row
    {
        public Guid Id { get; init; }
        public Guid? ParentId { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Slug { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string? SeoTitle { get; init; }
        public string? SeoDescription { get; init; }
        public string? ColorLabel { get; init; }
        public string? SizeLabel { get; init; }
        public bool IsActive { get; init; }
        public int ProductCount { get; init; }
        public int Version { get; init; }
    }
}

/// <summary>Campos do formulário de categoria. <see cref="Slug"/> vazio gera a partir do nome.</summary>
public sealed record CategoryInput(
    Guid? ParentId,
    string Name,
    string? Slug,
    string? Description,
    string? SeoTitle,
    string? SeoDescription,
    string? ColorLabel,
    string? SizeLabel);

public sealed record CreateCategoryCommand(CategoryInput Input) : ICommand<Guid>;

public sealed record UpdateCategoryCommand(Guid Id, int Version, CategoryInput Input) : ICommand;

public sealed record SetCategoryStatusCommand(Guid Id, bool IsActive) : ICommand;

public sealed record DeleteCategoryCommand(Guid Id) : ICommand;

internal sealed class CategoryInputValidator : AbstractValidator<CategoryInput>
{
    public const int DescriptionMaxLength = 2000;

    public CategoryInputValidator()
    {
        RuleFor(x => x.Name).NotEmpty().Length(Category.NameMinLength, Category.NameMaxLength);
        RuleFor(x => x.Slug).MaximumLength(Domain.Catalog.Slug.MaxLength);
        RuleFor(x => x.Description).MaximumLength(DescriptionMaxLength);
        RuleFor(x => x.SeoTitle).MaximumLength(SeoDefaults.TitleMaxLength);
        RuleFor(x => x.SeoDescription).MaximumLength(SeoDefaults.DescriptionMaxLength);
        RuleFor(x => x.ColorLabel).MaximumLength(Category.LabelMaxLength);
        RuleFor(x => x.SizeLabel).MaximumLength(Category.LabelMaxLength);
    }
}

internal sealed class CreateCategoryValidator : AbstractValidator<CreateCategoryCommand>
{
    public CreateCategoryValidator() => RuleFor(x => x.Input).SetValidator(new CategoryInputValidator());
}

internal sealed class UpdateCategoryValidator : AbstractValidator<UpdateCategoryCommand>
{
    public UpdateCategoryValidator() => RuleFor(x => x.Input).SetValidator(new CategoryInputValidator());
}

internal sealed class CreateCategoryHandler(
    ICategoryRepository categories, ISlugHistory slugHistory, IOutbox outbox, TimeProvider time)
    : ICommandHandler<CreateCategoryCommand, Guid>
{
    public async Task<Result<Guid>> Handle(CreateCategoryCommand command, CancellationToken cancellationToken)
    {
        var content = await CategoryContentFactory.BuildAsync(
            command.Input, null, categories, slugHistory, cancellationToken);
        if (content.IsFailure)
        {
            return content.Error;
        }

        var parent = await CategoryContentFactory.LoadParentAsync(command.Input.ParentId, categories, cancellationToken);
        if (parent.IsFailure)
        {
            return parent.Error;
        }

        var category = Category.Create(content.Value, parent.Value, time.GetUtcNow().UtcDateTime);
        if (category.IsFailure)
        {
            return category.Error;
        }

        await categories.AddAsync(category.Value, cancellationToken);
        await CatalogChanged.CategoriesAsync(outbox, cancellationToken);
        return category.Value.Id.Value;
    }
}

internal sealed class UpdateCategoryHandler(
    ICategoryRepository categories, ISlugHistory slugHistory, IOutbox outbox, TimeProvider time)
    : ICommandHandler<UpdateCategoryCommand>
{
    public async Task<Result> Handle(UpdateCategoryCommand command, CancellationToken cancellationToken)
    {
        var id = new CategoryId(command.Id);
        var category = await categories.GetByIdAsync(id, cancellationToken);
        if (category is null)
        {
            return Result.Failure(CatalogErrors.CategoryNotFound);
        }

        if (category.Version != command.Version)
        {
            return Result.Failure(CatalogErrors.ConcurrentChange);
        }

        var content = await CategoryContentFactory.BuildAsync(command.Input, id, categories, slugHistory, cancellationToken);
        if (content.IsFailure)
        {
            return Result.Failure(content.Error);
        }

        var parent = await CategoryContentFactory.LoadParentAsync(command.Input.ParentId, categories, cancellationToken);
        if (parent.IsFailure)
        {
            return Result.Failure(parent.Error);
        }

        var now = time.GetUtcNow().UtcDateTime;
        var hasChildren = parent.Value is not null && await categories.HasChildrenAsync(id, cancellationToken);
        var previousSlug = category.Update(content.Value, parent.Value, hasChildren, now);
        if (previousSlug.IsFailure)
        {
            return Result.Failure(previousSlug.Error);
        }

        if (!await categories.UpdateAsync(category, cancellationToken))
        {
            return Result.Failure(CatalogErrors.ConcurrentChange);
        }

        if (previousSlug.Value is not null)
        {
            await slugHistory.RecordChangeAsync(SlugEntityTypes.Category, id.Value, previousSlug.Value, category.Slug, now, cancellationToken);
        }

        await CatalogChanged.CategoriesAsync(outbox, cancellationToken);
        return Result.Success();
    }
}

internal sealed class SetCategoryStatusHandler(ICategoryRepository categories, IOutbox outbox, TimeProvider time)
    : ICommandHandler<SetCategoryStatusCommand>
{
    public async Task<Result> Handle(SetCategoryStatusCommand command, CancellationToken cancellationToken)
    {
        var category = await categories.GetByIdAsync(new CategoryId(command.Id), cancellationToken);
        if (category is null)
        {
            return Result.Failure(CatalogErrors.CategoryNotFound);
        }

        category.SetActive(command.IsActive, time.GetUtcNow().UtcDateTime);
        if (!await categories.UpdateAsync(category, cancellationToken))
        {
            return Result.Failure(CatalogErrors.ConcurrentChange);
        }

        await CatalogChanged.CategoriesAsync(outbox, cancellationToken);
        return Result.Success();
    }
}

/// <summary>RF-050: só exclui categoria sem produtos (e sem subcategorias); com produtos, a saída é desativar.</summary>
internal sealed class DeleteCategoryHandler(ICategoryRepository categories, IOutbox outbox) : ICommandHandler<DeleteCategoryCommand>
{
    public async Task<Result> Handle(DeleteCategoryCommand command, CancellationToken cancellationToken)
    {
        var id = new CategoryId(command.Id);
        if (await categories.GetByIdAsync(id, cancellationToken) is null)
        {
            return Result.Failure(CatalogErrors.CategoryNotFound);
        }

        if (await categories.HasProductsAsync(id, cancellationToken) || await categories.HasChildrenAsync(id, cancellationToken))
        {
            return Result.Failure(CatalogErrors.CategoryInUse);
        }

        await categories.DeleteAsync(id, cancellationToken);
        await CatalogChanged.CategoriesAsync(outbox, cancellationToken);
        return Result.Success();
    }
}

internal static class CategoryContentFactory
{
    /// <summary>Monta o conteúdo do domínio conferindo slug (formato, uso e histórico) e nome únicos.</summary>
    public static async Task<Result<CategoryContent>> BuildAsync(
        CategoryInput input, CategoryId? currentId, ICategoryRepository categories, ISlugHistory slugHistory, CancellationToken cancellationToken)
    {
        var slug = string.IsNullOrWhiteSpace(input.Slug) ? Slug.FromText(input.Name) : Slug.Create(input.Slug);
        if (slug.IsFailure)
        {
            return slug.Error;
        }

        if (await categories.NameInUseAsync(input.Name.Trim(), currentId, cancellationToken))
        {
            return CatalogErrors.CategoryNameInUse;
        }

        if (await categories.SlugInUseAsync(slug.Value, currentId, cancellationToken)
            || await slugHistory.BelongsToAnotherAsync(slug.Value, SlugEntityTypes.Category, currentId?.Value ?? Guid.Empty, cancellationToken))
        {
            return CatalogErrors.SlugInUse;
        }

        return new CategoryContent(
            input.Name, slug.Value, input.Description ?? string.Empty, input.SeoTitle, input.SeoDescription, input.ColorLabel, input.SizeLabel);
    }

    public static async Task<Result<Category?>> LoadParentAsync(Guid? parentId, ICategoryRepository categories, CancellationToken cancellationToken)
    {
        if (parentId is null)
        {
            return Result.Success<Category?>(null);
        }

        var parent = await categories.GetByIdAsync(new CategoryId(parentId.Value), cancellationToken);
        return parent is null ? CatalogErrors.ParentCategoryNotFound : Result.Success<Category?>(parent);
    }
}
