using ECommerce.Application.Abstractions.Authentication;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Domain.Abstractions;
using ECommerce.Domain.Customers;
using ECommerce.Domain.Identity;
using FluentValidation;

namespace ECommerce.Application.Identity.Register;

/// <summary>RF-001 — cadastro de cliente; já devolve a sessão iniciada.</summary>
public sealed record RegisterCommand(
    string FullName,
    string Email,
    string Password,
    string Phone,
    string? Cpf,
    string? IpAddress) : ICommand<AuthSession>;

internal sealed class RegisterValidator : AbstractValidator<RegisterCommand>
{
    public RegisterValidator()
    {
        RuleFor(x => x.FullName)
            .NotEmpty().WithMessage("Informe o nome completo.")
            .Length(Customer.FullNameMinLength, Customer.FullNameMaxLength);
        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("Informe o e-mail.")
            .Must(email => Domain.Identity.Email.Create(email).IsSuccess).WithMessage(IdentityErrors.InvalidEmail.Message);
        RuleFor(x => x.Password).StrongPassword();
        RuleFor(x => x.Phone)
            .Must(phone => Domain.Customers.Phone.Create(phone).IsSuccess).WithMessage(CustomerErrors.InvalidPhone.Message);
        RuleFor(x => x.Cpf)
            .Must(cpf => Domain.Customers.Cpf.Create(cpf).IsSuccess).WithMessage(CustomerErrors.InvalidCpf.Message)
            .When(x => !string.IsNullOrWhiteSpace(x.Cpf));
    }
}

internal sealed class RegisterHandler(
    IUserRepository users,
    ICustomerRepository customers,
    IPasswordHasher passwordHasher,
    SessionIssuer sessions,
    AccountEmails emails,
    TimeProvider time) : ICommandHandler<RegisterCommand, AuthSession>
{
    public async Task<Result<AuthSession>> Handle(RegisterCommand command, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;

        var email = Email.Create(command.Email);
        var phone = Phone.Create(command.Phone);
        var cpf = string.IsNullOrWhiteSpace(command.Cpf) ? null : Cpf.Create(command.Cpf);
        if (email.IsFailure || phone.IsFailure || cpf is { IsFailure: true })
        {
            return email.IsFailure ? email.Error : phone.IsFailure ? phone.Error : cpf!.Error;
        }

        if (await users.ExistsByEmailAsync(email.Value, cancellationToken))
        {
            return IdentityErrors.EmailAlreadyInUse;
        }

        if (cpf is not null && await customers.CpfInUseByAnotherAsync(cpf.Value, null, cancellationToken))
        {
            return CustomerErrors.CpfAlreadyInUse;
        }

        // RF-001 CA5: este endpoint só cria Customer.
        var user = User.RegisterCustomer(email.Value, passwordHasher.Hash(command.Password), now);
        var customer = Customer.Create(user.Id, command.FullName, phone.Value, cpf?.Value, now);
        if (customer.IsFailure)
        {
            return customer.Error;
        }

        if (!await users.TryAddAsync(user, cancellationToken))
        {
            return IdentityErrors.EmailAlreadyInUse;
        }

        await customers.AddAsync(customer.Value, cancellationToken);
        await emails.SendEmailConfirmationAsync(user, customer.Value.FullName, welcome: true, cancellationToken);

        return await sessions.StartAsync(user, command.IpAddress, cancellationToken);
    }
}
