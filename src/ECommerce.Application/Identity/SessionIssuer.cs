using ECommerce.Application.Abstractions.Authentication;
using ECommerce.Domain.Abstractions;
using ECommerce.Domain.Identity;

namespace ECommerce.Application.Identity;

/// <summary>Emite o par access token + refresh token: família nova no login, rotação no refresh (arquivo 08 §4).</summary>
internal sealed class SessionIssuer(
    IAccessTokenIssuer accessTokens,
    ISecureTokenFactory secureTokens,
    IRefreshTokenRepository refreshTokens,
    AuthLifetimes lifetimes,
    TimeProvider time)
{
    private const int RefreshTokenBytes = 64;

    public async Task<AuthSession> StartAsync(User user, string? ipAddress, CancellationToken cancellationToken)
    {
        var raw = secureTokens.Create(RefreshTokenBytes);
        var refreshToken = RefreshToken.StartFamily(
            user.Id, raw.Hash, Now, lifetimes.RefreshTokenSliding, lifetimes.RefreshTokenAbsolute, ipAddress);

        await refreshTokens.AddAsync(refreshToken, cancellationToken);
        return Build(user, raw.Raw, refreshToken);
    }

    /// <summary>Falha quando outro request já usou o mesmo token (renovações simultâneas).</summary>
    public async Task<Result<AuthSession>> RotateAsync(
        RefreshToken current, User user, string? ipAddress, CancellationToken cancellationToken)
    {
        var raw = secureTokens.Create(RefreshTokenBytes);
        var next = current.Rotate(raw.Hash, Now, lifetimes.RefreshTokenSliding, ipAddress);

        if (!await refreshTokens.TryRevokeAsync(current, cancellationToken))
        {
            return IdentityErrors.InvalidSession;
        }

        await refreshTokens.AddAsync(next, cancellationToken);
        return Build(user, raw.Raw, next);
    }

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    private AuthSession Build(User user, string rawRefreshToken, RefreshToken refreshToken)
    {
        var access = accessTokens.Issue(user);
        return new AuthSession(
            access.Value,
            access.ExpiresAtUtc,
            rawRefreshToken,
            refreshToken.ExpiresAtUtc,
            new SessionUserResponse(user.Id.Value, user.Email.Value, user.Role.ToString(), user.IsEmailConfirmed));
    }
}
