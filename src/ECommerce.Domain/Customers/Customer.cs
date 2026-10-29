using ECommerce.Domain.Abstractions;
using ECommerce.Domain.Identity;

namespace ECommerce.Domain.Customers;

/// <summary>Dados do cliente; o Id é o mesmo do <see cref="User"/> (arquivo 04 §4.2). Endereços entram na Fase 4.</summary>
public sealed class Customer : AggregateRoot<UserId>
{
    public const int FullNameMinLength = 3;
    public const int FullNameMaxLength = 120;

    private Customer()
    {
    }

    public string FullName { get; private set; } = string.Empty;
    public Phone? Phone { get; private set; }
    public Cpf? Cpf { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    public static Result<Customer> Create(UserId id, string fullName, Phone? phone, Cpf? cpf, DateTime nowUtc)
    {
        var name = NormalizeName(fullName);
        if (name is null)
        {
            return CustomerErrors.InvalidFullName;
        }

        return new Customer
        {
            Id = id,
            FullName = name,
            Phone = phone,
            Cpf = cpf,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
        };
    }

    public static Customer Rehydrate(
        UserId id, string fullName, Phone? phone, Cpf? cpf, DateTime createdAtUtc, DateTime updatedAtUtc) =>
        new()
        {
            Id = id,
            FullName = fullName,
            Phone = phone,
            Cpf = cpf,
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = updatedAtUtc,
        };

    /// <summary>RF-005: nome, telefone e CPF; o e-mail não muda no MVP.</summary>
    public Result UpdateProfile(string fullName, Phone? phone, Cpf? cpf, DateTime nowUtc)
    {
        var name = NormalizeName(fullName);
        if (name is null)
        {
            return Result.Failure(CustomerErrors.InvalidFullName);
        }

        FullName = name;
        Phone = phone;
        Cpf = cpf;
        UpdatedAtUtc = nowUtc;
        return Result.Success();
    }

    private static string? NormalizeName(string? fullName)
    {
        var name = string.Join(' ', (fullName ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return name.Length is >= FullNameMinLength and <= FullNameMaxLength ? name : null;
    }
}

public interface ICustomerRepository
{
    Task<Customer?> GetByIdAsync(UserId id, CancellationToken cancellationToken);
    Task<bool> CpfInUseByAnotherAsync(Cpf cpf, UserId? exceptCustomerId, CancellationToken cancellationToken);
    Task AddAsync(Customer customer, CancellationToken cancellationToken);
    Task UpdateAsync(Customer customer, CancellationToken cancellationToken);
}
