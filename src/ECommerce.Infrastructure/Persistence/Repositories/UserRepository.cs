using Dapper;
using ECommerce.Application.Abstractions.Data;
using ECommerce.Domain.Identity;

namespace ECommerce.Infrastructure.Persistence.Repositories;

internal sealed class UserRepository(IUnitOfWork unitOfWork) : IUserRepository
{
    private const string SelectColumns = """
        SELECT id, email::text AS email, password_hash, role, is_active, email_confirmed_at, failed_login_attempts,
               lockout_end_utc, security_stamp, version, created_at, updated_at
          FROM identity.users
        """;

    public Task<User?> GetByIdAsync(UserId id, CancellationToken cancellationToken) =>
        QuerySingleAsync($"{SelectColumns} WHERE id = @Id", new { Id = id.Value }, cancellationToken);

    public Task<User?> GetByEmailAsync(Email email, CancellationToken cancellationToken) =>
        QuerySingleAsync($"{SelectColumns} WHERE email = @Email", new { Email = email.Value }, cancellationToken);

    public Task<bool> ExistsByEmailAsync(Email email, CancellationToken cancellationToken) =>
        unitOfWork.Connection.ExecuteScalarAsync<bool>(Command(
            "SELECT EXISTS (SELECT 1 FROM identity.users WHERE email = @Email)", new { Email = email.Value }, cancellationToken));

    public Task<bool> AnyActiveAdminAsync(CancellationToken cancellationToken) =>
        unitOfWork.Connection.ExecuteScalarAsync<bool>(Command(
            "SELECT EXISTS (SELECT 1 FROM identity.users WHERE role = 'Admin' AND is_active)", null, cancellationToken));

    public async Task<bool> TryAddAsync(User user, CancellationToken cancellationToken)
    {
        // ON CONFLICT em vez de capturar 23505: um erro abortaria a transação inteira.
        var inserted = await unitOfWork.Connection.ExecuteAsync(Command(
            """
            INSERT INTO identity.users
              (id, email, password_hash, role, is_active, email_confirmed_at, failed_login_attempts, lockout_end_utc,
               security_stamp, version, created_at, updated_at)
            VALUES (@Id, @Email, @PasswordHash, @Role, @IsActive, @EmailConfirmedAt, @FailedLoginAttempts, @LockoutEnd,
                    @SecurityStamp, 0, @CreatedAt, @UpdatedAt)
            ON CONFLICT (email) DO NOTHING
            """,
            Parameters(user),
            cancellationToken));

        return inserted == 1;
    }

    public async Task<bool> UpdateAsync(User user, CancellationToken cancellationToken)
    {
        var updated = await unitOfWork.Connection.ExecuteAsync(Command(
            """
            UPDATE identity.users
               SET password_hash = @PasswordHash, role = @Role, is_active = @IsActive,
                   email_confirmed_at = @EmailConfirmedAt, failed_login_attempts = @FailedLoginAttempts,
                   lockout_end_utc = @LockoutEnd, security_stamp = @SecurityStamp,
                   version = version + 1, updated_at = @UpdatedAt
             WHERE id = @Id AND version = @Version
            """,
            Parameters(user),
            cancellationToken));

        return updated == 1;
    }

    private static object Parameters(User user) => new
    {
        Id = user.Id.Value,
        Email = user.Email.Value,
        user.PasswordHash,
        Role = user.Role.ToString(),
        user.IsActive,
        EmailConfirmedAt = user.EmailConfirmedAtUtc,
        user.FailedLoginAttempts,
        LockoutEnd = user.LockoutEndUtc,
        user.SecurityStamp,
        user.Version,
        CreatedAt = user.CreatedAtUtc,
        UpdatedAt = user.UpdatedAtUtc,
    };

    private async Task<User?> QuerySingleAsync(string sql, object parameters, CancellationToken cancellationToken)
    {
        var row = await unitOfWork.Connection.QuerySingleOrDefaultAsync<UserRow>(Command(sql, parameters, cancellationToken));
        return row is null
            ? null
            : User.Rehydrate(
                new UserId(row.Id),
                Email.Rehydrate(row.Email),
                row.PasswordHash,
                Enum.Parse<Role>(row.Role),
                row.IsActive,
                row.EmailConfirmedAt,
                row.FailedLoginAttempts,
                row.LockoutEndUtc,
                row.SecurityStamp,
                row.Version,
                row.CreatedAt,
                row.UpdatedAt);
    }

    private CommandDefinition Command(string sql, object? parameters, CancellationToken cancellationToken) =>
        new(sql, parameters, unitOfWork.Transaction, cancellationToken: cancellationToken);

    private sealed class UserRow
    {
        public Guid Id { get; init; }
        public string Email { get; init; } = string.Empty;
        public string PasswordHash { get; init; } = string.Empty;
        public string Role { get; init; } = string.Empty;
        public bool IsActive { get; init; }
        public DateTime? EmailConfirmedAt { get; init; }
        public int FailedLoginAttempts { get; init; }
        public DateTime? LockoutEndUtc { get; init; }
        public Guid SecurityStamp { get; init; }
        public int Version { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime UpdatedAt { get; init; }
    }
}
