using ECommerce.Domain.Identity;

namespace ECommerce.Application.Abstractions.Authentication;

public enum PasswordCheck
{
    Failed,
    Success,
    SuccessRehashNeeded,
}

/// <summary>PBKDF2 (arquivo 20, T03) — implementado com <c>PasswordHasher&lt;T&gt;</c> na Infrastructure.</summary>
public interface IPasswordHasher
{
    string Hash(string password);
    PasswordCheck Verify(string passwordHash, string password);

    /// <summary>Gasta o mesmo tempo de uma verificação real quando o usuário não existe (arquivo 08 §4.2, anti-enumeração).</summary>
    void SimulateVerification(string password);
}

public sealed record AccessToken(string Value, DateTime ExpiresAtUtc);

/// <summary>JWT de curta duração com sub, email, role e sstamp (arquivo 08 §2).</summary>
public interface IAccessTokenIssuer
{
    AccessToken Issue(User user);
}

/// <summary>Token aleatório para URL e o hash SHA-256 que vai para o banco.</summary>
public sealed record SecureToken(string Raw, string Hash);

public interface ISecureTokenFactory
{
    /// <summary>32 bytes para links de e-mail; 64 para refresh token (arquivo 08 §1).</summary>
    SecureToken Create(int byteCount = 32);
    string Hash(string raw);
}

/// <summary>Durações configuradas em <c>Jwt:*</c> (arquivo 08 §6).</summary>
public sealed record AuthLifetimes(TimeSpan AccessToken, TimeSpan RefreshTokenSliding, TimeSpan RefreshTokenAbsolute);

/// <summary>Usuário do request (arquivo 09 §6). O Id de quem é dono do recurso vem sempre daqui, nunca do corpo.</summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }

    /// <summary>Lança se não houver usuário autenticado.</summary>
    UserId UserId { get; }

    string? Role { get; }
}
