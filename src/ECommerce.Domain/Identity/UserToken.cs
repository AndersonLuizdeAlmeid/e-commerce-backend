using ECommerce.Domain.Abstractions;

namespace ECommerce.Domain.Identity;

public enum UserTokenPurpose
{
    EmailConfirmation,
    PasswordReset,
}

/// <summary>Token de uso único enviado por e-mail; só o hash é guardado (arquivo 08 §4.6, arquivo 23 §5-6).</summary>
public sealed class UserToken : Entity<Guid>
{
    public static readonly TimeSpan EmailConfirmationLifetime = TimeSpan.FromHours(24);
    public static readonly TimeSpan PasswordResetLifetime = TimeSpan.FromHours(1);

    private UserToken()
    {
    }

    public UserId UserId { get; private set; }
    public UserTokenPurpose Purpose { get; private set; }
    public string TokenHash { get; private set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; private set; }
    public DateTime? UsedAtUtc { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    public static UserToken Issue(UserId userId, UserTokenPurpose purpose, string tokenHash, DateTime nowUtc) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Purpose = purpose,
            TokenHash = tokenHash,
            ExpiresAtUtc = nowUtc.Add(purpose == UserTokenPurpose.PasswordReset ? PasswordResetLifetime : EmailConfirmationLifetime),
            CreatedAtUtc = nowUtc,
        };

    public static UserToken Rehydrate(
        Guid id, UserId userId, UserTokenPurpose purpose, string tokenHash, DateTime expiresAtUtc, DateTime? usedAtUtc, DateTime createdAtUtc) =>
        new()
        {
            Id = id,
            UserId = userId,
            Purpose = purpose,
            TokenHash = tokenHash,
            ExpiresAtUtc = expiresAtUtc,
            UsedAtUtc = usedAtUtc,
            CreatedAtUtc = createdAtUtc,
        };

    public bool IsUsable(UserTokenPurpose purpose, DateTime nowUtc) =>
        Purpose == purpose && UsedAtUtc is null && ExpiresAtUtc > nowUtc;

    public void MarkUsed(DateTime nowUtc) => UsedAtUtc ??= nowUtc;
}
