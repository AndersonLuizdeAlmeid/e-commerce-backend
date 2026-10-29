using ECommerce.Application.Abstractions.Authentication;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Domain.Abstractions;
using ECommerce.Domain.Customers;
using ECommerce.Domain.Identity;
using FluentValidation;

namespace ECommerce.Application.Identity.ResetPassword;

/// <summary>RF-008 — troca a senha pelo link do e-mail, gasta o token, encerra as sessões e envia EM-03.</summary>
public sealed record ResetPasswordCommand(string Token, string NewPassword) : ICommand;

internal sealed class ResetPasswordValidator : AbstractValidator<ResetPasswordCommand>
{
    public ResetPasswordValidator()
    {
        RuleFor(x => x.Token).NotEmpty().WithMessage(IdentityErrors.InvalidToken.Message).MaximumLength(200);
        RuleFor(x => x.NewPassword).StrongPassword();
    }
}

internal sealed class ResetPasswordHandler(
    IUserTokenRepository userTokens,
    IUserRepository users,
    ICustomerRepository customers,
    IRefreshTokenRepository refreshTokens,
    ISecureTokenFactory secureTokens,
    IPasswordHasher passwordHasher,
    AccountEmails emails,
    TimeProvider time) : ICommandHandler<ResetPasswordCommand>
{
    public async Task<Result> Handle(ResetPasswordCommand command, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var token = await userTokens.GetByHashAsync(secureTokens.Hash(command.Token), cancellationToken);
        if (token is null || !token.IsUsable(UserTokenPurpose.PasswordReset, now))
        {
            return Result.Failure(IdentityErrors.InvalidToken);
        }

        var user = await users.GetByIdAsync(token.UserId, cancellationToken);
        if (user is null || !user.IsActive)
        {
            return Result.Failure(IdentityErrors.InvalidToken);
        }

        user.ChangePassword(passwordHasher.Hash(command.NewPassword), now);
        if (!await users.UpdateAsync(user, cancellationToken))
        {
            return Result.Failure(IdentityErrors.ConcurrencyConflict);
        }

        token.MarkUsed(now);
        await userTokens.MarkUsedAsync(token, cancellationToken);
        await userTokens.InvalidateAllAsync(user.Id, UserTokenPurpose.PasswordReset, now, cancellationToken);
        await refreshTokens.RevokeAllForUserAsync(user.Id, now, cancellationToken);
        await emails.SendPasswordChangedAsync(user, await customers.GreetingNameAsync(user, cancellationToken), cancellationToken);

        return Result.Success();
    }
}
