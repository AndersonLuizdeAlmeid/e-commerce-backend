using ECommerce.Application.Abstractions.Authentication;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Domain.Abstractions;
using ECommerce.Domain.Customers;
using ECommerce.Domain.Identity;
using FluentValidation;

namespace ECommerce.Application.Identity.ChangePassword;

/// <summary>
/// RF-006 — exige a senha atual, encerra todas as sessões (CA1), envia EM-03 e devolve uma sessão nova para
/// o aparelho em que a troca foi feita.
/// </summary>
public sealed record ChangePasswordCommand(UserId UserId, string CurrentPassword, string NewPassword, string? IpAddress)
    : ICommand<AuthSession>;

internal sealed class ChangePasswordValidator : AbstractValidator<ChangePasswordCommand>
{
    public ChangePasswordValidator()
    {
        RuleFor(x => x.CurrentPassword).NotEmpty().WithMessage("Informe a senha atual.");
        RuleFor(x => x.NewPassword).StrongPassword();
        RuleFor(x => x.NewPassword)
            .NotEqual(x => x.CurrentPassword).WithMessage("A nova senha precisa ser diferente da atual.");
    }
}

internal sealed class ChangePasswordHandler(
    IUserRepository users,
    ICustomerRepository customers,
    IRefreshTokenRepository refreshTokens,
    IPasswordHasher passwordHasher,
    SessionIssuer sessions,
    AccountEmails emails,
    TimeProvider time) : ICommandHandler<ChangePasswordCommand, AuthSession>
{
    public async Task<Result<AuthSession>> Handle(ChangePasswordCommand command, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var user = await users.GetByIdAsync(command.UserId, cancellationToken);
        if (user is null || !user.IsActive)
        {
            return IdentityErrors.UserNotFound;
        }

        if (passwordHasher.Verify(user.PasswordHash, command.CurrentPassword) == PasswordCheck.Failed)
        {
            return IdentityErrors.WrongCurrentPassword;
        }

        user.ChangePassword(passwordHasher.Hash(command.NewPassword), now);
        if (!await users.UpdateAsync(user, cancellationToken))
        {
            return IdentityErrors.ConcurrencyConflict;
        }

        await refreshTokens.RevokeAllForUserAsync(user.Id, now, cancellationToken);
        await emails.SendPasswordChangedAsync(user, await customers.GreetingNameAsync(user, cancellationToken), cancellationToken);

        return await sessions.StartAsync(user, command.IpAddress, cancellationToken);
    }
}
