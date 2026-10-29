using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Domain.Abstractions;
using ECommerce.Domain.Customers;
using ECommerce.Domain.Identity;
using FluentValidation;

namespace ECommerce.Application.Identity.ForgotPassword;

/// <summary>RF-008 — pede o link de redefinição. Sempre sucesso (202), exista ou não a conta (CA1).</summary>
public sealed record ForgotPasswordCommand(string Email) : ICommand;

internal sealed class ForgotPasswordValidator : AbstractValidator<ForgotPasswordCommand>
{
    public ForgotPasswordValidator() =>
        RuleFor(x => x.Email).NotEmpty().WithMessage("Informe o e-mail.").MaximumLength(Email.MaxLength);
}

internal sealed class ForgotPasswordHandler(
    IUserRepository users,
    ICustomerRepository customers,
    AccountEmails emails) : ICommandHandler<ForgotPasswordCommand>
{
    public async Task<Result> Handle(ForgotPasswordCommand command, CancellationToken cancellationToken)
    {
        var email = Email.Create(command.Email);
        var user = email.IsSuccess ? await users.GetByEmailAsync(email.Value, cancellationToken) : null;

        if (user is { IsActive: true })
        {
            await emails.SendPasswordResetAsync(user, await customers.GreetingNameAsync(user, cancellationToken), cancellationToken);
        }

        return Result.Success();
    }
}
