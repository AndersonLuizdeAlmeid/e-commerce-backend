using Dapper;
using ECommerce.Application.Abstractions.Data;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Domain.Abstractions;
using ECommerce.Domain.Customers;
using ECommerce.Domain.Identity;
using ECommerce.Domain.Shipping;
using FluentValidation;

namespace ECommerce.Application.Customers;

// RF-007 — caderno de endereços do cliente, usado no checkout (Fase 4.2).

public sealed record AddressResponse(
    Guid Id,
    string Recipient,
    string PostalCode,
    string Street,
    string Number,
    string? Complement,
    string District,
    string City,
    string State,
    bool IsDefault)
{
    internal static AddressResponse From(Address address) => new(
        address.Id,
        address.Content.Recipient,
        address.Content.PostalCode.Value,
        address.Content.Street,
        address.Content.Number,
        address.Content.Complement,
        address.Content.District,
        address.Content.City,
        address.Content.State,
        address.IsDefault);
}

/// <summary>Campos que o cliente digita; os comandos são planos para que os erros saiam com o nome do campo da tela.</summary>
public interface IAddressFields
{
    string? Recipient { get; }
    string? PostalCode { get; }
    string? Street { get; }
    string? Number { get; }
    string? Complement { get; }
    string? District { get; }
    string? City { get; }
    string? State { get; }
}

public sealed record ListAddressesQuery(UserId CustomerId) : IQuery<IReadOnlyList<AddressResponse>>;

/// <summary><see cref="IsDefault"/> falso não tira o posto de ninguém; o primeiro endereço é sempre o padrão.</summary>
public sealed record CreateAddressCommand(
    UserId CustomerId,
    string? Recipient,
    string? PostalCode,
    string? Street,
    string? Number,
    string? Complement,
    string? District,
    string? City,
    string? State,
    bool IsDefault) : ICommand<AddressResponse>, IAddressFields;

public sealed record UpdateAddressCommand(
    UserId CustomerId,
    Guid AddressId,
    string? Recipient,
    string? PostalCode,
    string? Street,
    string? Number,
    string? Complement,
    string? District,
    string? City,
    string? State) : ICommand, IAddressFields;

public sealed record DeleteAddressCommand(UserId CustomerId, Guid AddressId) : ICommand;

public sealed record SetDefaultAddressCommand(UserId CustomerId, Guid AddressId) : ICommand;

/// <summary>Mesmas regras de <see cref="AddressContent.Create"/>, campo a campo, para a tela marcar cada um.</summary>
internal static class AddressFieldsRules
{
    public static void AddAddressRules<T>(this AbstractValidator<T> validator)
        where T : IAddressFields
    {
        validator.RuleFor(x => x.Recipient)
            .Must(value => Length(value) is >= Address.RecipientMinLength and <= Address.RecipientMaxLength)
            .WithMessage(AddressErrors.InvalidRecipient.Message);
        validator.RuleFor(x => x.PostalCode)
            .Must(value => Domain.Shipping.PostalCode.Create(value).IsSuccess)
            .WithMessage(ShippingErrors.InvalidPostalCode.Message);
        validator.RuleFor(x => x.Street)
            .Must(value => Length(value) is > 0 and <= Address.StreetMaxLength)
            .WithMessage(AddressErrors.InvalidStreet.Message);
        validator.RuleFor(x => x.Number)
            .Must(value => Length(value) is > 0 and <= Address.NumberMaxLength)
            .WithMessage(AddressErrors.InvalidNumber.Message);
        validator.RuleFor(x => x.Complement)
            .Must(value => Length(value) <= Address.ComplementMaxLength)
            .WithMessage(AddressErrors.InvalidComplement.Message);
        validator.RuleFor(x => x.District)
            .Must(value => Length(value) is > 0 and <= Address.DistrictMaxLength)
            .WithMessage(AddressErrors.InvalidDistrict.Message);
        validator.RuleFor(x => x.City)
            .Must(value => Length(value) is > 0 and <= Address.CityMaxLength)
            .WithMessage(AddressErrors.InvalidCity.Message);
        validator.RuleFor(x => x.State)
            .Must(value => BrazilianStates.IsValid((value ?? string.Empty).Trim().ToUpperInvariant()))
            .WithMessage(AddressErrors.InvalidState.Message);
        validator.RuleFor(x => x.State)
            .Must((fields, state) => StateMatches(fields.PostalCode, state))
            .WithMessage(AddressErrors.StateDoesNotMatchPostalCode.Message)
            .When(fields => Domain.Shipping.PostalCode.Create(fields.PostalCode).IsSuccess
                && BrazilianStates.IsValid((fields.State ?? string.Empty).Trim().ToUpperInvariant()));
    }

    private static int Length(string? value) => (value ?? string.Empty).Trim().Length;

    private static bool StateMatches(string? postalCode, string? state) =>
        BrazilianStates.FromPostalCode(Domain.Shipping.PostalCode.Create(postalCode).Value) is not { } expected
        || string.Equals(expected, (state ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);
}

internal sealed class CreateAddressValidator : AbstractValidator<CreateAddressCommand>
{
    public CreateAddressValidator() => this.AddAddressRules();
}

internal sealed class UpdateAddressValidator : AbstractValidator<UpdateAddressCommand>
{
    public UpdateAddressValidator() => this.AddAddressRules();
}

internal static class AddressFields
{
    public static Result<AddressContent> ToContent(IAddressFields fields) =>
        AddressContent.Create(
            fields.Recipient, fields.PostalCode, fields.Street, fields.Number, fields.Complement, fields.District, fields.City, fields.State);
}

internal sealed class ListAddressesHandler(IDbConnectionFactory connectionFactory)
    : IQueryHandler<ListAddressesQuery, IReadOnlyList<AddressResponse>>
{
    private const string Sql = """
        SELECT id, recipient, postal_code, street, number, complement, district, city, state, is_default
          FROM customers.addresses
         WHERE customer_id = @CustomerId
         ORDER BY is_default DESC, created_at, id
        """;

    public async Task<Result<IReadOnlyList<AddressResponse>>> Handle(ListAddressesQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<Row>(
            new CommandDefinition(Sql, new { CustomerId = query.CustomerId.Value }, cancellationToken: cancellationToken));
        return rows
            .Select(row => new AddressResponse(
                row.Id, row.Recipient, row.PostalCode, row.Street, row.Number, row.Complement, row.District, row.City, row.State, row.IsDefault))
            .ToList();
    }

    private sealed class Row
    {
        public Guid Id { get; init; }
        public string Recipient { get; init; } = string.Empty;
        public string PostalCode { get; init; } = string.Empty;
        public string Street { get; init; } = string.Empty;
        public string Number { get; init; } = string.Empty;
        public string? Complement { get; init; }
        public string District { get; init; } = string.Empty;
        public string City { get; init; } = string.Empty;
        public string State { get; init; } = string.Empty;
        public bool IsDefault { get; init; }
    }
}

internal sealed class CreateAddressHandler(IAddressBookRepository books, TimeProvider time)
    : ICommandHandler<CreateAddressCommand, AddressResponse>
{
    public async Task<Result<AddressResponse>> Handle(CreateAddressCommand command, CancellationToken cancellationToken)
    {
        var content = AddressFields.ToContent(command);
        if (content.IsFailure)
        {
            return content.Error;
        }

        var book = await books.GetForUpdateAsync(command.CustomerId, cancellationToken);
        if (book is null)
        {
            return CustomerErrors.CustomerNotFound;
        }

        var added = book.Add(content.Value, command.IsDefault, time.GetUtcNow().UtcDateTime);
        if (added.IsFailure)
        {
            return added.Error;
        }

        await books.SaveAsync(book, cancellationToken);
        return AddressResponse.From(added.Value);
    }
}

internal sealed class UpdateAddressHandler(IAddressBookRepository books, TimeProvider time) : ICommandHandler<UpdateAddressCommand>
{
    public async Task<Result> Handle(UpdateAddressCommand command, CancellationToken cancellationToken)
    {
        var content = AddressFields.ToContent(command);
        if (content.IsFailure)
        {
            return Result.Failure(content.Error);
        }

        var book = await books.GetForUpdateAsync(command.CustomerId, cancellationToken);
        if (book is null)
        {
            return Result.Failure(AddressErrors.AddressNotFound);
        }

        var updated = book.Update(command.AddressId, content.Value, time.GetUtcNow().UtcDateTime);
        if (updated.IsFailure)
        {
            return updated;
        }

        await books.SaveAsync(book, cancellationToken);
        return Result.Success();
    }
}

internal sealed class DeleteAddressHandler(IAddressBookRepository books) : ICommandHandler<DeleteAddressCommand>
{
    public async Task<Result> Handle(DeleteAddressCommand command, CancellationToken cancellationToken)
    {
        var book = await books.GetForUpdateAsync(command.CustomerId, cancellationToken);
        if (book is null)
        {
            return Result.Failure(AddressErrors.AddressNotFound);
        }

        var removed = book.Remove(command.AddressId);
        if (removed.IsFailure)
        {
            return removed;
        }

        await books.SaveAsync(book, cancellationToken);
        return Result.Success();
    }
}

internal sealed class SetDefaultAddressHandler(IAddressBookRepository books) : ICommandHandler<SetDefaultAddressCommand>
{
    public async Task<Result> Handle(SetDefaultAddressCommand command, CancellationToken cancellationToken)
    {
        var book = await books.GetForUpdateAsync(command.CustomerId, cancellationToken);
        if (book is null)
        {
            return Result.Failure(AddressErrors.AddressNotFound);
        }

        var changed = book.SetDefault(command.AddressId);
        if (changed.IsFailure)
        {
            return changed;
        }

        await books.SaveAsync(book, cancellationToken);
        return Result.Success();
    }
}
