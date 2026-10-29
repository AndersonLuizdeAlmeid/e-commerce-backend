using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using ECommerce.Application.Abstractions.Authentication;
using ECommerce.Domain.Identity;
using Microsoft.AspNetCore.Identity;

namespace ECommerce.Infrastructure.Authentication;

/// <summary>PBKDF2 do <c>PasswordHasher&lt;T&gt;</c> (HMAC-SHA512, 100 mil iterações, salt por usuário), sem EF (arquivo 08 §3).</summary>
internal sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    private readonly PasswordHasher<User> _hasher = new();
    private readonly Lazy<string> _dummyHash;

    public Pbkdf2PasswordHasher() =>
        _dummyHash = new Lazy<string>(() => _hasher.HashPassword(null!, Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))));

    public string Hash(string password) => _hasher.HashPassword(null!, password);

    public PasswordCheck Verify(string passwordHash, string password) =>
        _hasher.VerifyHashedPassword(null!, passwordHash, password) switch
        {
            PasswordVerificationResult.Success => PasswordCheck.Success,
            PasswordVerificationResult.SuccessRehashNeeded => PasswordCheck.SuccessRehashNeeded,
            _ => PasswordCheck.Failed,
        };

    public void SimulateVerification(string password) =>
        _hasher.VerifyHashedPassword(null!, _dummyHash.Value, password);
}

/// <summary>Tokens aleatórios em base64url; no banco vai só o SHA-256 em hexadecimal (arquivo 05 §2).</summary>
internal sealed class SecureTokenFactory : ISecureTokenFactory
{
    public SecureToken Create(int byteCount = 32)
    {
        var raw = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(byteCount));
        return new SecureToken(raw, Hash(raw));
    }

    public string Hash(string raw) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
}
