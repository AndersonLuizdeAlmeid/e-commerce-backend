using ECommerce.Domain.Abstractions;
using ECommerce.Domain.Identity;
using ECommerce.Domain.Shipping;

namespace ECommerce.Domain.Customers;

public static class AddressErrors
{
    public static readonly Error InvalidRecipient = Error.Validation(
        "Customers.InvalidRecipient", $"Informe quem vai receber ({Address.RecipientMinLength} a {Address.RecipientMaxLength} caracteres).");

    public static readonly Error InvalidStreet = Error.Validation(
        "Customers.InvalidStreet", $"Informe a rua (até {Address.StreetMaxLength} caracteres).");

    public static readonly Error InvalidNumber = Error.Validation(
        "Customers.InvalidNumber", $"Informe o número, ou \"S/N\" (até {Address.NumberMaxLength} caracteres).");

    public static readonly Error InvalidComplement = Error.Validation(
        "Customers.InvalidComplement", $"O complemento tem até {Address.ComplementMaxLength} caracteres.");

    public static readonly Error InvalidDistrict = Error.Validation(
        "Customers.InvalidDistrict", $"Informe o bairro (até {Address.DistrictMaxLength} caracteres).");

    public static readonly Error InvalidCity = Error.Validation(
        "Customers.InvalidCity", $"Informe a cidade (até {Address.CityMaxLength} caracteres).");

    public static readonly Error InvalidState = Error.Validation("Customers.InvalidState", "Informe a UF, como SP ou MG.");

    public static readonly Error StateDoesNotMatchPostalCode =
        Error.Validation("Customers.StateDoesNotMatchPostalCode", "A UF não confere com o CEP. Confira os dois.");

    public static readonly Error TooManyAddresses = Error.BusinessRule(
        "Customers.TooManyAddresses", $"Você pode guardar até {AddressBook.MaxAddresses} endereços. Apague um para incluir outro.");

    public static readonly Error AddressNotFound = Error.NotFound("Customers.AddressNotFound", "Endereço não encontrado.");
}

/// <summary>Os campos de um endereço, já limpos: espaços repetidos saem, a UF fica em maiúsculas e o complemento vazio vira nulo.</summary>
public sealed record AddressContent(
    string Recipient,
    PostalCode PostalCode,
    string Street,
    string Number,
    string? Complement,
    string District,
    string City,
    string State)
{
    /// <summary>
    /// Valida sem consultar serviço externo: a UF precisa bater com a faixa do CEP (tabela dos Correios). O preenchimento
    /// pelo ViaCEP é da tela (RF-007 CA2); salvar não depende dele estar no ar.
    /// </summary>
    public static Result<AddressContent> Create(
        string? recipient, string? postalCode, string? street, string? number, string? complement, string? district, string? city, string? state)
    {
        var cep = PostalCode.Create(postalCode);
        if (cep.IsFailure)
        {
            return cep.Error;
        }

        var cleanRecipient = Clean(recipient);
        if (cleanRecipient.Length is < Address.RecipientMinLength or > Address.RecipientMaxLength)
        {
            return AddressErrors.InvalidRecipient;
        }

        var cleanStreet = Clean(street);
        if (cleanStreet.Length is 0 or > Address.StreetMaxLength)
        {
            return AddressErrors.InvalidStreet;
        }

        var cleanNumber = Clean(number);
        if (cleanNumber.Length is 0 or > Address.NumberMaxLength)
        {
            return AddressErrors.InvalidNumber;
        }

        var cleanComplement = Clean(complement);
        if (cleanComplement.Length > Address.ComplementMaxLength)
        {
            return AddressErrors.InvalidComplement;
        }

        var cleanDistrict = Clean(district);
        if (cleanDistrict.Length is 0 or > Address.DistrictMaxLength)
        {
            return AddressErrors.InvalidDistrict;
        }

        var cleanCity = Clean(city);
        if (cleanCity.Length is 0 or > Address.CityMaxLength)
        {
            return AddressErrors.InvalidCity;
        }

        var uf = Clean(state).ToUpperInvariant();
        if (!BrazilianStates.IsValid(uf))
        {
            return AddressErrors.InvalidState;
        }

        if (BrazilianStates.FromPostalCode(cep.Value) is { } expected && expected != uf)
        {
            return AddressErrors.StateDoesNotMatchPostalCode;
        }

        return new AddressContent(
            cleanRecipient, cep.Value, cleanStreet, cleanNumber, cleanComplement.Length == 0 ? null : cleanComplement, cleanDistrict, cleanCity, uf);
    }

    private static string Clean(string? value) =>
        string.Join(' ', (value ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}

public sealed class Address
{
    public const int RecipientMinLength = 3;
    public const int RecipientMaxLength = 120;
    public const int StreetMaxLength = 120;
    public const int NumberMaxLength = 20;
    public const int ComplementMaxLength = 60;
    public const int DistrictMaxLength = 80;
    public const int CityMaxLength = 80;

    private Address(Guid id, AddressContent content, bool isDefault, DateTime createdAtUtc, DateTime updatedAtUtc)
    {
        Id = id;
        Content = content;
        IsDefault = isDefault;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;
    }

    public Guid Id { get; }

    public AddressContent Content { get; private set; }

    public bool IsDefault { get; internal set; }

    public DateTime CreatedAtUtc { get; }

    public DateTime UpdatedAtUtc { get; private set; }

    /// <summary>Uso exclusivo da Infrastructure.</summary>
    public static Address Rehydrate(Guid id, AddressContent content, bool isDefault, DateTime createdAtUtc, DateTime updatedAtUtc) =>
        new(id, content, isDefault, createdAtUtc, updatedAtUtc);

    internal static Address Create(AddressContent content, DateTime nowUtc) => new(Guid.CreateVersion7(), content, false, nowUtc, nowUtc);

    internal void Update(AddressContent content, DateTime nowUtc)
    {
        Content = content;
        UpdatedAtUtc = nowUtc;
    }
}

/// <summary>
/// Endereços de um cliente (arquivo 04: <c>Customer</c> contém <c>Address</c>; RF-007). Separado do perfil para que
/// editar um endereço não carregue nem trave o cadastro. Regras: até <see cref="MaxAddresses"/>, sempre exatamente um
/// padrão quando há algum, e o primeiro endereço já nasce padrão.
/// </summary>
public sealed class AddressBook
{
    public const int MaxAddresses = 10;

    private readonly List<Address> _addresses;

    private AddressBook(UserId customerId, List<Address> addresses)
    {
        CustomerId = customerId;
        _addresses = addresses;
    }

    public UserId CustomerId { get; }

    /// <summary>Padrão primeiro, depois do mais antigo para o mais novo.</summary>
    public IReadOnlyList<Address> Addresses => _addresses;

    /// <summary>Uso exclusivo da Infrastructure.</summary>
    public static AddressBook Rehydrate(UserId customerId, IEnumerable<Address> addresses) => new(customerId, addresses.ToList());

    public Result<Address> Add(AddressContent content, bool makeDefault, DateTime nowUtc)
    {
        if (_addresses.Count >= MaxAddresses)
        {
            return AddressErrors.TooManyAddresses;
        }

        var address = Address.Create(content, nowUtc);
        _addresses.Add(address);
        if (makeDefault || _addresses.Count == 1)
        {
            MakeDefault(address);
        }

        return address;
    }

    public Result Update(Guid addressId, AddressContent content, DateTime nowUtc)
    {
        var address = Find(addressId);
        if (address is null)
        {
            return Result.Failure(AddressErrors.AddressNotFound);
        }

        address.Update(content, nowUtc);
        return Result.Success();
    }

    public Result SetDefault(Guid addressId)
    {
        var address = Find(addressId);
        if (address is null)
        {
            return Result.Failure(AddressErrors.AddressNotFound);
        }

        MakeDefault(address);
        return Result.Success();
    }

    /// <summary>Apagar o padrão passa o posto para o endereço mais recente que sobrou.</summary>
    public Result Remove(Guid addressId)
    {
        var address = Find(addressId);
        if (address is null)
        {
            return Result.Failure(AddressErrors.AddressNotFound);
        }

        _addresses.Remove(address);
        if (address.IsDefault && _addresses.Count > 0)
        {
            MakeDefault(_addresses.MaxBy(other => other.CreatedAtUtc)!);
        }

        return Result.Success();
    }

    private Address? Find(Guid addressId) => _addresses.Find(address => address.Id == addressId);

    private void MakeDefault(Address address)
    {
        foreach (var other in _addresses)
        {
            other.IsDefault = ReferenceEquals(other, address);
        }
    }
}

public interface IAddressBookRepository
{
    /// <summary>Trava o cliente até o fim da transação: duas abas mexendo nos endereços não quebram o padrão único.</summary>
    Task<AddressBook?> GetForUpdateAsync(UserId customerId, CancellationToken cancellationToken);

    Task SaveAsync(AddressBook book, CancellationToken cancellationToken);
}
