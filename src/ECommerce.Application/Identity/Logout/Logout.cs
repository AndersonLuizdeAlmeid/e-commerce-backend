using ECommerce.Application.Abstractions.Authentication;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Domain.Abstractions;
using ECommerce.Domain.Identity;

namespace ECommerce.Application.Identity.Logout;

/// <summary>
/// RF-004 — revoga o refresh token do cookie. Funciona mesmo com o access token já expirado; sem cookie,
/// não há o que revogar e o resultado continua sendo sucesso.
/// </summary>
public sealed record LogoutCommand(string? RefreshToken) : ICommand;

internal sealed class LogoutHandler(
    IRefreshTokenRepository refreshTokens,
    ISecureTokenFactory secureTokens,
    TimeProvider time) : ICommandHandler<LogoutCommand>
{
    public async Task<Result> Handle(LogoutCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.RefreshToken))
        {
            return Result.Success();
        }

        var token = await refreshTokens.GetByHashAsync(secureTokens.Hash(command.RefreshToken), cancellationToken);
        if (token is { IsRevoked: false })
        {
            token.Revoke(time.GetUtcNow().UtcDateTime);
            await refreshTokens.TryRevokeAsync(token, cancellationToken);
        }

        return Result.Success();
    }
}
