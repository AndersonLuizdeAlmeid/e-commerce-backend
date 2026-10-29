namespace ECommerce.Application.Identity;

/// <summary>
/// Resultado de cadastro, login, renovação e troca de senha. O endpoint devolve o access token no corpo e o
/// refresh token só em cookie HttpOnly (arquivo 08 §1).
/// </summary>
public sealed record AuthSession(
    string AccessToken,
    DateTime AccessTokenExpiresAtUtc,
    string RefreshToken,
    DateTime RefreshTokenExpiresAtUtc,
    SessionUserResponse User);

public sealed record SessionUserResponse(Guid Id, string Email, string Role, bool EmailConfirmed);

/// <summary>Corpo da resposta de sessão (sem o refresh token, que vai no cookie).</summary>
public sealed record SessionResponse(string AccessToken, DateTime ExpiresAt, SessionUserResponse User)
{
    public static SessionResponse From(AuthSession session) =>
        new(session.AccessToken, session.AccessTokenExpiresAtUtc, session.User);
}
