using ECommerce.Application.Abstractions.Authentication;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Domain.Abstractions;
using ECommerce.Domain.Identity;

namespace ECommerce.Application.Identity.RefreshSession;

/// <summary>
/// RF-003 — renova a sessão com rotação. Reuso de token revogado derruba a família inteira e essa revogação
/// precisa ser gravada mesmo com o 401 (<see cref="IPersistOnFailure"/>).
/// </summary>
public sealed record RefreshSessionCommand(string? RefreshToken, string? IpAddress) : ICommand<AuthSession>, IPersistOnFailure;

internal sealed class RefreshSessionHandler(
    IRefreshTokenRepository refreshTokens,
    IUserRepository users,
    ISecureTokenFactory secureTokens,
    SessionIssuer sessions,
    TimeProvider time) : ICommandHandler<RefreshSessionCommand, AuthSession>
{
    public async Task<Result<AuthSession>> Handle(RefreshSessionCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.RefreshToken))
        {
            return IdentityErrors.InvalidSession;
        }

        var now = time.GetUtcNow().UtcDateTime;
        var current = await refreshTokens.GetByHashAsync(secureTokens.Hash(command.RefreshToken), cancellationToken);
        if (current is null)
        {
            return IdentityErrors.InvalidSession;
        }

        // CA2: token já revogado sendo usado de novo = provável roubo. Encerra a família toda.
        if (current.IsRevoked)
        {
            await refreshTokens.RevokeFamilyAsync(current.FamilyId, now, cancellationToken);
            return IdentityErrors.InvalidSession;
        }

        if (!current.IsActive(now))
        {
            return IdentityErrors.InvalidSession;
        }

        var user = await users.GetByIdAsync(current.UserId, cancellationToken);
        if (user is null || !user.IsActive)
        {
            await refreshTokens.RevokeFamilyAsync(current.FamilyId, now, cancellationToken);
            return IdentityErrors.InvalidSession;
        }

        return await sessions.RotateAsync(current, user, command.IpAddress, cancellationToken);
    }
}
