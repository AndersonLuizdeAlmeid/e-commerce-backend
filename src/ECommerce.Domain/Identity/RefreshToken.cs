using ECommerce.Domain.Abstractions;

namespace ECommerce.Domain.Identity;

/// <summary>
/// Refresh token opaco e rotativo (arquivo 08 §4.3). Guarda só o hash; a família inteira morre em
/// <see cref="AbsoluteExpiresAtUtc"/> (máx. 30 dias), mesmo com uso contínuo.
/// </summary>
public sealed class RefreshToken : Entity<Guid>
{
    private RefreshToken()
    {
    }

    public UserId UserId { get; private set; }
    public string TokenHash { get; private set; } = string.Empty;
    public Guid FamilyId { get; private set; }
    public DateTime ExpiresAtUtc { get; private set; }
    public DateTime AbsoluteExpiresAtUtc { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public string? CreatedByIp { get; private set; }
    public DateTime? RevokedAtUtc { get; private set; }
    public Guid? ReplacedById { get; private set; }

    public bool IsRevoked => RevokedAtUtc.HasValue;

    public bool IsActive(DateTime nowUtc) => !IsRevoked && ExpiresAtUtc > nowUtc;

    /// <summary>Login ou cadastro: começa uma família nova.</summary>
    public static RefreshToken StartFamily(
        UserId userId, string tokenHash, DateTime nowUtc, TimeSpan slidingLifetime, TimeSpan absoluteLifetime, string? ip)
    {
        var absoluteExpiresAt = nowUtc.Add(absoluteLifetime);
        return Create(userId, tokenHash, Guid.CreateVersion7(), nowUtc, slidingLifetime, absoluteExpiresAt, ip);
    }

    /// <summary>Revoga este token e devolve o sucessor na mesma família (RF-003 CA1).</summary>
    public RefreshToken Rotate(string newTokenHash, DateTime nowUtc, TimeSpan slidingLifetime, string? ip)
    {
        var next = Create(UserId, newTokenHash, FamilyId, nowUtc, slidingLifetime, AbsoluteExpiresAtUtc, ip);
        RevokedAtUtc = nowUtc;
        ReplacedById = next.Id;
        return next;
    }

    public void Revoke(DateTime nowUtc) => RevokedAtUtc ??= nowUtc;

    private static RefreshToken Create(
        UserId userId, string tokenHash, Guid familyId, DateTime nowUtc, TimeSpan slidingLifetime, DateTime absoluteExpiresAt, string? ip)
    {
        var slidingExpiresAt = nowUtc.Add(slidingLifetime);
        return new RefreshToken
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            TokenHash = tokenHash,
            FamilyId = familyId,
            ExpiresAtUtc = slidingExpiresAt < absoluteExpiresAt ? slidingExpiresAt : absoluteExpiresAt,
            AbsoluteExpiresAtUtc = absoluteExpiresAt,
            CreatedAtUtc = nowUtc,
            CreatedByIp = ip,
        };
    }

    public static RefreshToken Rehydrate(
        Guid id, UserId userId, string tokenHash, Guid familyId, DateTime expiresAtUtc, DateTime absoluteExpiresAtUtc,
        DateTime createdAtUtc, string? createdByIp, DateTime? revokedAtUtc, Guid? replacedById) =>
        new()
        {
            Id = id,
            UserId = userId,
            TokenHash = tokenHash,
            FamilyId = familyId,
            ExpiresAtUtc = expiresAtUtc,
            AbsoluteExpiresAtUtc = absoluteExpiresAtUtc,
            CreatedAtUtc = createdAtUtc,
            CreatedByIp = createdByIp,
            RevokedAtUtc = revokedAtUtc,
            ReplacedById = replacedById,
        };
}
