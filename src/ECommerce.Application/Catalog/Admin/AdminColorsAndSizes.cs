using Dapper;
using ECommerce.Application.Abstractions.Data;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Application.Abstractions.Outbox;
using ECommerce.Domain.Abstractions;
using ECommerce.Domain.Catalog;
using FluentValidation;

namespace ECommerce.Application.Catalog.Admin;

// RF-057 — os dois eixos da grade no painel: cores/acabamentos e tamanhos. Só exclui o que não está em uso.

public sealed record AdminColorResponse(Guid Id, string Name, string Slug, string Hex, int VariantCount);

public sealed record AdminSizeResponse(Guid Id, string Name, short SortOrder, int VariantCount);

public sealed record ListAdminColorsQuery : IQuery<IReadOnlyList<AdminColorResponse>>;

public sealed record ListAdminSizesQuery : IQuery<IReadOnlyList<AdminSizeResponse>>;

public sealed record CreateColorCommand(string Name, string Hex) : ICommand<Guid>;

public sealed record UpdateColorCommand(Guid Id, string Name, string Hex) : ICommand;

public sealed record DeleteColorCommand(Guid Id) : ICommand;

public sealed record CreateSizeCommand(string Name, short SortOrder) : ICommand<Guid>;

public sealed record UpdateSizeCommand(Guid Id, string Name, short SortOrder) : ICommand;

public sealed record DeleteSizeCommand(Guid Id) : ICommand;

internal sealed class ListAdminColorsHandler(IDbConnectionFactory connectionFactory)
    : IQueryHandler<ListAdminColorsQuery, IReadOnlyList<AdminColorResponse>>
{
    private const string Sql = """
        SELECT c.id, c.name, c.slug, c.hex,
               (SELECT count(*)::int FROM catalog.product_variants v WHERE v.color_id = c.id) AS variant_count
          FROM catalog.colors c
         ORDER BY c.name
        """;

    public async Task<Result<IReadOnlyList<AdminColorResponse>>> Handle(ListAdminColorsQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<Row>(new CommandDefinition(Sql, cancellationToken: cancellationToken));
        return rows.Select(r => new AdminColorResponse(r.Id, r.Name, r.Slug, r.Hex, r.VariantCount)).ToList();
    }

    private sealed class Row
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Slug { get; init; } = string.Empty;
        public string Hex { get; init; } = string.Empty;
        public int VariantCount { get; init; }
    }
}

internal sealed class ListAdminSizesHandler(IDbConnectionFactory connectionFactory)
    : IQueryHandler<ListAdminSizesQuery, IReadOnlyList<AdminSizeResponse>>
{
    private const string Sql = """
        SELECT s.id, s.name, s.sort_order,
               (SELECT count(*)::int FROM catalog.product_variants v WHERE v.size_id = s.id) AS variant_count
          FROM catalog.sizes s
         ORDER BY s.sort_order, s.name
        """;

    public async Task<Result<IReadOnlyList<AdminSizeResponse>>> Handle(ListAdminSizesQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<Row>(new CommandDefinition(Sql, cancellationToken: cancellationToken));
        return rows.Select(r => new AdminSizeResponse(r.Id, r.Name, r.SortOrder, r.VariantCount)).ToList();
    }

    private sealed class Row
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public short SortOrder { get; init; }
        public int VariantCount { get; init; }
    }
}

internal sealed class CreateColorValidator : AbstractValidator<CreateColorCommand>
{
    public CreateColorValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(Color.NameMaxLength);
        RuleFor(x => x.Hex).NotEmpty().Matches("^#[0-9A-Fa-f]{6}$").WithMessage(CatalogDomainErrors.InvalidHex.Message);
    }
}

internal sealed class UpdateColorValidator : AbstractValidator<UpdateColorCommand>
{
    public UpdateColorValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(Color.NameMaxLength);
        RuleFor(x => x.Hex).NotEmpty().Matches("^#[0-9A-Fa-f]{6}$").WithMessage(CatalogDomainErrors.InvalidHex.Message);
    }
}

internal sealed class CreateSizeValidator : AbstractValidator<CreateSizeCommand>
{
    public CreateSizeValidator() => RuleFor(x => x.Name).NotEmpty().MaximumLength(Size.NameMaxLength);
}

internal sealed class UpdateSizeValidator : AbstractValidator<UpdateSizeCommand>
{
    public UpdateSizeValidator() => RuleFor(x => x.Name).NotEmpty().MaximumLength(Size.NameMaxLength);
}

internal sealed class CreateColorHandler(IColorRepository colors, IOutbox outbox) : ICommandHandler<CreateColorCommand, Guid>
{
    public async Task<Result<Guid>> Handle(CreateColorCommand command, CancellationToken cancellationToken)
    {
        var color = Color.Create(command.Name, command.Hex);
        if (color.IsFailure)
        {
            return color.Error;
        }

        if (await colors.NameOrSlugInUseAsync(color.Value.Name, color.Value.Slug, null, cancellationToken))
        {
            return CatalogErrors.ColorNameInUse;
        }

        await colors.AddAsync(color.Value, cancellationToken);
        await CatalogChanged.ProductsAsync(outbox, cancellationToken);
        return color.Value.Id.Value;
    }
}

internal sealed class UpdateColorHandler(IColorRepository colors, IOutbox outbox) : ICommandHandler<UpdateColorCommand>
{
    public async Task<Result> Handle(UpdateColorCommand command, CancellationToken cancellationToken)
    {
        var id = new ColorId(command.Id);
        var color = await colors.GetByIdAsync(id, cancellationToken);
        if (color is null)
        {
            return Result.Failure(CatalogErrors.ColorNotFound);
        }

        var updated = color.Update(command.Name, command.Hex);
        if (updated.IsFailure)
        {
            return updated;
        }

        if (await colors.NameOrSlugInUseAsync(color.Name, color.Slug, id, cancellationToken))
        {
            return Result.Failure(CatalogErrors.ColorNameInUse);
        }

        await colors.UpdateAsync(color, cancellationToken);
        await CatalogChanged.ProductsAsync(outbox, cancellationToken);
        return Result.Success();
    }
}

internal sealed class DeleteColorHandler(IColorRepository colors) : ICommandHandler<DeleteColorCommand>
{
    public async Task<Result> Handle(DeleteColorCommand command, CancellationToken cancellationToken)
    {
        var id = new ColorId(command.Id);
        if (await colors.GetByIdAsync(id, cancellationToken) is null)
        {
            return Result.Failure(CatalogErrors.ColorNotFound);
        }

        if (await colors.IsUsedAsync(id, cancellationToken))
        {
            return Result.Failure(CatalogErrors.ColorInUse);
        }

        // Sem uso, nada na loja aponta para ela: não há o que revalidar.
        await colors.DeleteAsync(id, cancellationToken);
        return Result.Success();
    }
}

internal sealed class CreateSizeHandler(ISizeRepository sizes, IOutbox outbox) : ICommandHandler<CreateSizeCommand, Guid>
{
    public async Task<Result<Guid>> Handle(CreateSizeCommand command, CancellationToken cancellationToken)
    {
        var size = Size.Create(command.Name, command.SortOrder);
        if (size.IsFailure)
        {
            return size.Error;
        }

        if (await sizes.NameInUseAsync(size.Value.Name, null, cancellationToken))
        {
            return CatalogErrors.SizeNameInUse;
        }

        await sizes.AddAsync(size.Value, cancellationToken);
        await CatalogChanged.ProductsAsync(outbox, cancellationToken);
        return size.Value.Id.Value;
    }
}

internal sealed class UpdateSizeHandler(ISizeRepository sizes, IOutbox outbox) : ICommandHandler<UpdateSizeCommand>
{
    public async Task<Result> Handle(UpdateSizeCommand command, CancellationToken cancellationToken)
    {
        var id = new SizeId(command.Id);
        var size = await sizes.GetByIdAsync(id, cancellationToken);
        if (size is null)
        {
            return Result.Failure(CatalogErrors.SizeNotFound);
        }

        var updated = size.Update(command.Name, command.SortOrder);
        if (updated.IsFailure)
        {
            return updated;
        }

        if (await sizes.NameInUseAsync(size.Name, id, cancellationToken))
        {
            return Result.Failure(CatalogErrors.SizeNameInUse);
        }

        await sizes.UpdateAsync(size, cancellationToken);
        await CatalogChanged.ProductsAsync(outbox, cancellationToken);
        return Result.Success();
    }
}

internal sealed class DeleteSizeHandler(ISizeRepository sizes) : ICommandHandler<DeleteSizeCommand>
{
    public async Task<Result> Handle(DeleteSizeCommand command, CancellationToken cancellationToken)
    {
        var id = new SizeId(command.Id);
        if (await sizes.GetByIdAsync(id, cancellationToken) is null)
        {
            return Result.Failure(CatalogErrors.SizeNotFound);
        }

        if (await sizes.IsUsedAsync(id, cancellationToken))
        {
            return Result.Failure(CatalogErrors.SizeInUse);
        }

        await sizes.DeleteAsync(id, cancellationToken);
        return Result.Success();
    }
}
