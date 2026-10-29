using Dapper;
using ECommerce.Application.Abstractions.Data;
using ECommerce.Domain.Customers;
using ECommerce.Domain.Identity;

namespace ECommerce.Infrastructure.Persistence.Repositories;

internal sealed class CustomerRepository(IUnitOfWork unitOfWork) : ICustomerRepository
{
    public async Task<Customer?> GetByIdAsync(UserId id, CancellationToken cancellationToken)
    {
        var row = await unitOfWork.Connection.QuerySingleOrDefaultAsync<CustomerRow>(Command(
            "SELECT id, full_name, phone, cpf, created_at, updated_at FROM customers.customers WHERE id = @Id",
            new { Id = id.Value },
            cancellationToken));

        return row is null
            ? null
            : Customer.Rehydrate(
                new UserId(row.Id),
                row.FullName,
                row.Phone is null ? null : Phone.Rehydrate(row.Phone),
                row.Cpf is null ? null : Cpf.Rehydrate(row.Cpf),
                row.CreatedAt,
                row.UpdatedAt);
    }

    public Task<bool> CpfInUseByAnotherAsync(Cpf cpf, UserId? exceptCustomerId, CancellationToken cancellationToken) =>
        unitOfWork.Connection.ExecuteScalarAsync<bool>(Command(
            "SELECT EXISTS (SELECT 1 FROM customers.customers WHERE cpf = @Cpf AND (@ExceptId::uuid IS NULL OR id <> @ExceptId))",
            new { Cpf = cpf.Value, ExceptId = exceptCustomerId?.Value },
            cancellationToken));

    public Task AddAsync(Customer customer, CancellationToken cancellationToken) =>
        unitOfWork.Connection.ExecuteAsync(Command(
            """
            INSERT INTO customers.customers (id, full_name, phone, cpf, created_at, updated_at)
            VALUES (@Id, @FullName, @Phone, @Cpf, @CreatedAt, @UpdatedAt)
            """,
            Parameters(customer),
            cancellationToken));

    public Task UpdateAsync(Customer customer, CancellationToken cancellationToken) =>
        unitOfWork.Connection.ExecuteAsync(Command(
            """
            UPDATE customers.customers
               SET full_name = @FullName, phone = @Phone, cpf = @Cpf, updated_at = @UpdatedAt
             WHERE id = @Id
            """,
            Parameters(customer),
            cancellationToken));

    private static object Parameters(Customer customer) => new
    {
        Id = customer.Id.Value,
        customer.FullName,
        Phone = customer.Phone?.Value,
        Cpf = customer.Cpf?.Value,
        CreatedAt = customer.CreatedAtUtc,
        UpdatedAt = customer.UpdatedAtUtc,
    };

    private CommandDefinition Command(string sql, object parameters, CancellationToken cancellationToken) =>
        new(sql, parameters, unitOfWork.Transaction, cancellationToken: cancellationToken);

    private sealed class CustomerRow
    {
        public Guid Id { get; init; }
        public string FullName { get; init; } = string.Empty;
        public string? Phone { get; init; }
        public string? Cpf { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime UpdatedAt { get; init; }
    }
}
