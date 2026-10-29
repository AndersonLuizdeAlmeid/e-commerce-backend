using ECommerce.Application.Abstractions.Authentication;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Domain.Abstractions;
using ECommerce.Domain.Customers;
using ECommerce.Domain.Identity;
using FluentValidation;

namespace ECommerce.Application.Identity.ConfirmEmail;

/// <summary>RF-009 — confirma o e-mail pelo link (válido por 24 h, uso único).</summary>
public sealed record ConfirmEmailCommand(string Token) : ICommand;

internal sealed class ConfirmEmailValidator : AbstractValidator<ConfirmEmailCommand>
{
    public ConfirmEmailValidator() =>
        RuleFor(x => x.Token).NotEmpty().WithMessage(IdentityErrors.InvalidToken.Message).MaximumLength(200);
}

internal sealed class ConfirmEmailHandler(
    IUserTokenRepository userTokens,
    IUserRepository users,
    ISecureTokenFactory secureTokens,
    TimeProvider time) : ICommandHandler<ConfirmEmailCommand>
{
    public async Task<Result> Handle(ConfirmEmailCommand command, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var token = await userTokens.GetByHashAsync(secureTokens.Hash(command.Token), cancellationToken);
        if (token is null || !token.IsUsable(UserTokenPurpose.EmailConfirmation, now))
        {
            return Result.Failure(IdentityErrors.InvalidToken);
        }

        var user = await users.GetByIdAsync(token.UserId, cancellationToken);
        if (user is null || !user.IsActive)
        {
            return Result.Failure(IdentityErrors.InvalidToken);
        }

        user.ConfirmEmail(now);
        if (!await users.UpdateAsync(user, cancellationToken))
        {
            return Result.Failure(IdentityErrors.ConcurrencyConflict);
        }

        token.MarkUsed(now);
        await userTokens.MarkUsedAsync(token, cancellationToken);
        return Result.Success();
    }
}

/// <summary>RF-009 CA1 — reenvio do link para o próprio usuário logado.</summary>
public sealed record ResendEmailConfirmationCommand(UserId UserId) : ICommand;

internal sealed class ResendEmailConfirmationHandler(
    IUserRepository users,
    ICustomerRepository customers,
    AccountEmails emails) : ICommandHandler<ResendEmailConfirmationCommand>
{
    public async Task<Result> Handle(ResendEmailConfirmationCommand command, CancellationToken cancellationToken)
    {
        var user = await users.GetByIdAsync(command.UserId, cancellationToken);
        if (user is null || !user.IsActive)
        {
            return Result.Failure(IdentityErrors.UserNotFound);
        }

        if (user.IsEmailConfirmed)
        {
            return Result.Failure(IdentityErrors.EmailAlreadyConfirmed);
        }

        var name = await customers.GreetingNameAsync(user, cancellationToken);
        await emails.SendEmailConfirmationAsync(user, name, welcome: false, cancellationToken);
        return Result.Success();
    }
}
