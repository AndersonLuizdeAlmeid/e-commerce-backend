using ECommerce.Domain.Abstractions;

namespace ECommerce.Domain.Identity;

public readonly record struct UserId(Guid Value)
{
    public static UserId New() => new(Guid.CreateVersion7());

    public override string ToString() => Value.ToString();
}

public enum Role
{
    Customer,
    Admin,
}

/// <summary>Credenciais, perfil e proteção contra força bruta (arquivo 04 §4.1, arquivo 08).</summary>
public sealed class User : AggregateRoot<UserId>
{
    public const int MaxFailedLoginAttempts = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private User()
    {
    }

    public Email Email { get; private set; } = null!;
    public string PasswordHash { get; private set; } = string.Empty;
    public Role Role { get; private set; }
    public bool IsActive { get; private set; }
    public DateTime? EmailConfirmedAtUtc { get; private set; }
    public int FailedLoginAttempts { get; private set; }
    public DateTime? LockoutEndUtc { get; private set; }
    public Guid SecurityStamp { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    public bool IsEmailConfirmed => EmailConfirmedAtUtc.HasValue;

    /// <summary>RF-001 CA5: o cadastro público sempre cria <see cref="Role.Customer"/>.</summary>
    public static User RegisterCustomer(Email email, string passwordHash, DateTime nowUtc) =>
        Create(email, passwordHash, Role.Customer, emailConfirmedAtUtc: null, nowUtc);

    /// <summary>RF-060: admin inicial, criado pela configuração e já com e-mail confirmado.</summary>
    public static User CreateAdmin(Email email, string passwordHash, DateTime nowUtc) =>
        Create(email, passwordHash, Role.Admin, emailConfirmedAtUtc: nowUtc, nowUtc);

    private static User Create(Email email, string passwordHash, Role role, DateTime? emailConfirmedAtUtc, DateTime nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);

        return new User
        {
            Id = UserId.New(),
            Email = email,
            PasswordHash = passwordHash,
            Role = role,
            IsActive = true,
            EmailConfirmedAtUtc = emailConfirmedAtUtc,
            SecurityStamp = Guid.NewGuid(),
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
        };
    }

    public static User Rehydrate(
        UserId id, Email email, string passwordHash, Role role, bool isActive, DateTime? emailConfirmedAtUtc,
        int failedLoginAttempts, DateTime? lockoutEndUtc, Guid securityStamp, int version, DateTime createdAtUtc, DateTime updatedAtUtc) =>
        new()
        {
            Id = id,
            Email = email,
            PasswordHash = passwordHash,
            Role = role,
            IsActive = isActive,
            EmailConfirmedAtUtc = emailConfirmedAtUtc,
            FailedLoginAttempts = failedLoginAttempts,
            LockoutEndUtc = lockoutEndUtc,
            SecurityStamp = securityStamp,
            Version = version,
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = updatedAtUtc,
        };

    public bool IsLockedOut(DateTime nowUtc) => LockoutEndUtc > nowUtc;

    /// <summary>Inativo ou bloqueado não entra (RF-002 CA3/CA4), sempre com o mesmo erro genérico.</summary>
    public bool CanSignIn(DateTime nowUtc) => IsActive && !IsLockedOut(nowUtc);

    /// <summary>RN-002: na 5ª falha seguida a conta fica bloqueada por 15 minutos e o contador recomeça.</summary>
    public void RegisterFailedSignIn(DateTime nowUtc)
    {
        FailedLoginAttempts++;
        if (FailedLoginAttempts >= MaxFailedLoginAttempts)
        {
            LockoutEndUtc = nowUtc.Add(LockoutDuration);
            FailedLoginAttempts = 0;
        }

        UpdatedAtUtc = nowUtc;
    }

    public void RegisterSuccessfulSignIn(DateTime nowUtc)
    {
        if (FailedLoginAttempts == 0 && LockoutEndUtc is null)
        {
            return;
        }

        FailedLoginAttempts = 0;
        LockoutEndUtc = null;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Re-hash transparente quando o algoritmo/iterações mudam (arquivo 08 §3).</summary>
    public void UpgradePasswordHash(string passwordHash, DateTime nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        PasswordHash = passwordHash;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>RN-003: nova senha → novo security stamp (invalida tokens) e desbloqueio.</summary>
    public void ChangePassword(string passwordHash, DateTime nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        PasswordHash = passwordHash;
        SecurityStamp = Guid.NewGuid();
        FailedLoginAttempts = 0;
        LockoutEndUtc = null;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Idempotente: confirmar de novo não muda a data original.</summary>
    public void ConfirmEmail(DateTime nowUtc)
    {
        if (IsEmailConfirmed)
        {
            return;
        }

        EmailConfirmedAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
    }
}
