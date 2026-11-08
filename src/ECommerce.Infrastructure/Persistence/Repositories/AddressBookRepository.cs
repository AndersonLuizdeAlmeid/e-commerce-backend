using Dapper;
using ECommerce.Application.Abstractions.Data;
using ECommerce.Domain.Customers;
using ECommerce.Domain.Identity;
using ECommerce.Domain.Shipping;

namespace ECommerce.Infrastructure.Persistence.Repositories;

internal sealed class AddressBookRepository(IUnitOfWork unitOfWork) : IAddressBookRepository
{
    public async Task<AddressBook?> GetForUpdateAsync(UserId customerId, CancellationToken cancellationToken)
    {
        var locked = await unitOfWork.Connection.QuerySingleOrDefaultAsync<Guid?>(Command(
            "SELECT id FROM customers.customers WHERE id = @Id FOR UPDATE",
            new { Id = customerId.Value },
            cancellationToken));
        if (locked is null)
        {
            return null;
        }

        var rows = await unitOfWork.Connection.QueryAsync<AddressRow>(Command(
            """
            SELECT id, recipient, postal_code, street, number, complement, district, city, state, is_default, created_at, updated_at
              FROM customers.addresses
             WHERE customer_id = @Id
             ORDER BY is_default DESC, created_at, id
            """,
            new { Id = customerId.Value },
            cancellationToken));

        return AddressBook.Rehydrate(
            customerId,
            rows.Select(row => Address.Rehydrate(
                row.Id,
                new AddressContent(
                    row.Recipient,
                    PostalCode.Rehydrate(row.PostalCode),
                    row.Street,
                    row.Number,
                    row.Complement,
                    row.District,
                    row.City,
                    row.State),
                row.IsDefault,
                row.CreatedAt,
                row.UpdatedAt)));
    }

    /// <summary>
    /// Regrava o caderno inteiro, como a sacola: são no máximo 10 linhas, e apagar antes de inserir evita que o índice
    /// de padrão único veja dois padrões no meio da troca.
    /// </summary>
    public async Task SaveAsync(AddressBook book, CancellationToken cancellationToken)
    {
        await unitOfWork.Connection.ExecuteAsync(Command(
            "DELETE FROM customers.addresses WHERE customer_id = @Id",
            new { Id = book.CustomerId.Value },
            cancellationToken));

        if (book.Addresses.Count == 0)
        {
            return;
        }

        await unitOfWork.Connection.ExecuteAsync(Command(
            """
            INSERT INTO customers.addresses
                (id, customer_id, recipient, postal_code, street, number, complement, district, city, state, is_default, created_at, updated_at)
            VALUES
                (@Id, @CustomerId, @Recipient, @PostalCode, @Street, @Number, @Complement, @District, @City, @State, @IsDefault, @CreatedAt, @UpdatedAt)
            """,
            book.Addresses.Select(address => new
            {
                address.Id,
                CustomerId = book.CustomerId.Value,
                address.Content.Recipient,
                PostalCode = address.Content.PostalCode.Value,
                address.Content.Street,
                address.Content.Number,
                address.Content.Complement,
                address.Content.District,
                address.Content.City,
                address.Content.State,
                address.IsDefault,
                CreatedAt = address.CreatedAtUtc,
                UpdatedAt = address.UpdatedAtUtc,
            }).ToList(),
            cancellationToken));
    }

    private CommandDefinition Command(string sql, object parameters, CancellationToken cancellationToken) =>
        new(sql, parameters, unitOfWork.Transaction, cancellationToken: cancellationToken);

    private sealed class AddressRow
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
        public DateTime CreatedAt { get; init; }
        public DateTime UpdatedAt { get; init; }
    }
}
