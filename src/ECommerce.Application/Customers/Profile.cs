using Dapper;
using ECommerce.Application.Abstractions.Data;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Domain.Abstractions;
using ECommerce.Domain.Customers;
using ECommerce.Domain.Identity;
using FluentValidation;

namespace ECommerce.Application.Customers;

/// <summary>RF-005 — perfil do próprio cliente. O CPF volta mascarado (arquivo 17 §5).</summary>
public sealed record GetProfileQuery(UserId CustomerId) : IQuery<ProfileResponse>;

public sealed record ProfileResponse(string FullName, string Email, string? Phone, string? CpfMasked, bool HasCpf, bool EmailConfirmed);

internal sealed class GetProfileHandler(IDbConnectionFactory connectionFactory) : IQueryHandler<GetProfileQuery, ProfileResponse>
{
    private const string Sql = """
        SELECT c.full_name, u.email::text AS email, c.phone, c.cpf, u.email_confirmed_at IS NOT NULL AS email_confirmed
          FROM customers.customers c
          JOIN identity.users u ON u.id = c.id
         WHERE c.id = @Id AND u.is_active
        """;

    public async Task<Result<ProfileResponse>> Handle(GetProfileQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<ProfileRow>(
            new CommandDefinition(Sql, new { Id = query.CustomerId.Value }, cancellationToken: cancellationToken));

        if (row is null)
        {
            return CustomerErrors.CustomerNotFound;
        }

        var cpf = row.Cpf is null ? null : Cpf.Rehydrate(row.Cpf);
        return new ProfileResponse(row.FullName, row.Email, row.Phone, cpf?.Masked(), cpf is not null, row.EmailConfirmed);
    }

    private sealed class ProfileRow
    {
        public string FullName { get; init; } = string.Empty;
        public string Email { get; init; } = string.Empty;
        public string? Phone { get; init; }
        public string? Cpf { get; init; }
        public bool EmailConfirmed { get; init; }
    }
}

/// <summary>
/// RF-005 — atualiza nome, telefone e CPF. <paramref name="Cpf"/> nulo mantém o atual (a tela só mostra o CPF
/// mascarado, então não tem como reenviá-lo); para trocar, envie o novo.
/// </summary>
public sealed record UpdateProfileCommand(UserId CustomerId, string FullName, string Phone, string? Cpf) : ICommand;

internal sealed class UpdateProfileValidator : AbstractValidator<UpdateProfileCommand>
{
    public UpdateProfileValidator()
    {
        RuleFor(x => x.FullName)
            .NotEmpty().WithMessage("Informe o nome completo.")
            .Length(Customer.FullNameMinLength, Customer.FullNameMaxLength);
        RuleFor(x => x.Phone)
            .Must(phone => Domain.Customers.Phone.Create(phone).IsSuccess).WithMessage(CustomerErrors.InvalidPhone.Message);
        RuleFor(x => x.Cpf)
            .Must(cpf => Domain.Customers.Cpf.Create(cpf).IsSuccess).WithMessage(CustomerErrors.InvalidCpf.Message)
            .When(x => !string.IsNullOrWhiteSpace(x.Cpf));
    }
}

internal sealed class UpdateProfileHandler(ICustomerRepository customers, TimeProvider time)
    : ICommandHandler<UpdateProfileCommand>
{
    public async Task<Result> Handle(UpdateProfileCommand command, CancellationToken cancellationToken)
    {
        var customer = await customers.GetByIdAsync(command.CustomerId, cancellationToken);
        if (customer is null)
        {
            return Result.Failure(CustomerErrors.CustomerNotFound);
        }

        var phone = Phone.Create(command.Phone);
        if (phone.IsFailure)
        {
            return Result.Failure(phone.Error);
        }

        var cpf = customer.Cpf;
        if (!string.IsNullOrWhiteSpace(command.Cpf))
        {
            var newCpf = Cpf.Create(command.Cpf);
            if (newCpf.IsFailure)
            {
                return Result.Failure(newCpf.Error);
            }

            if (await customers.CpfInUseByAnotherAsync(newCpf.Value, customer.Id, cancellationToken))
            {
                return Result.Failure(CustomerErrors.CpfAlreadyInUse);
            }

            cpf = newCpf.Value;
        }

        var update = customer.UpdateProfile(command.FullName, phone.Value, cpf, time.GetUtcNow().UtcDateTime);
        if (update.IsFailure)
        {
            return update;
        }

        await customers.UpdateAsync(customer, cancellationToken);
        return Result.Success();
    }
}
