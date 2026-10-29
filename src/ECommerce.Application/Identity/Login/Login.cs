using ECommerce.Application.Abstractions.Authentication;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Domain.Abstractions;
using ECommerce.Domain.Identity;
using FluentValidation;

namespace ECommerce.Application.Identity.Login;

/// <summary>RF-002 — login. Falha conta tentativa mesmo com o resultado de erro (<see cref="IPersistOnFailure"/>).</summary>
public sealed record LoginCommand(string Email, string Password, string? IpAddress) : ICommand<AuthSession>, IPersistOnFailure;

internal sealed class LoginValidator : AbstractValidator<LoginCommand>
{
    public LoginValidator()
    {
        RuleFor(x => x.Email).NotEmpty().WithMessage("Informe o e-mail.").MaximumLength(Email.MaxLength);
        RuleFor(x => x.Password).NotEmpty().WithMessage("Informe a senha.").MaximumLength(PasswordRules.MaxLength);
    }
}

internal sealed class LoginHandler(
    IUserRepository users,
    IPasswordHasher passwordHasher,
    SessionIssuer sessions,
    TimeProvider time) : ICommandHandler<LoginCommand, AuthSession>
{
    public async Task<Result<AuthSession>> Handle(LoginCommand command, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var email = Email.Create(command.Email);
        var user = email.IsSuccess ? await users.GetByEmailAsync(email.Value, cancellationToken) : null;

        // Conta inexistente, inativa ou bloqueada: mesmo erro e mesmo custo de CPU (arquivo 08 §4.2).
        if (user is null || !user.CanSignIn(now))
        {
            passwordHasher.SimulateVerification(command.Password);
            return IdentityErrors.InvalidCredentials;
        }

        var check = passwordHasher.Verify(user.PasswordHash, command.Password);
        if (check == PasswordCheck.Failed)
        {
            user.RegisterFailedSignIn(now);
            await users.UpdateAsync(user, cancellationToken);
            return IdentityErrors.InvalidCredentials;
        }

        var needsUpdate = user.FailedLoginAttempts > 0 || user.LockoutEndUtc is not null || check == PasswordCheck.SuccessRehashNeeded;
        if (check == PasswordCheck.SuccessRehashNeeded)
        {
            user.UpgradePasswordHash(passwordHasher.Hash(command.Password), now);
        }

        user.RegisterSuccessfulSignIn(now);
        if (needsUpdate)
        {
            await users.UpdateAsync(user, cancellationToken);
        }

        return await sessions.StartAsync(user, command.IpAddress, cancellationToken);
    }
}
