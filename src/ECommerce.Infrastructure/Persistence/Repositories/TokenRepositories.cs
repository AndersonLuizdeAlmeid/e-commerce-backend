using Dapper;
using ECommerce.Application.Abstractions.Data;
using ECommerce.Domain.Identity;

namespace ECommerce.Infrastructure.Persistence.Repositories;

internal sealed class RefreshTokenRepository(IUnitOfWork unitOfWork) : IRefreshTokenRepository
{
    public async Task<RefreshToken?> GetByHashAsync(string tokenHash, CancellationToken cancellationToken)
    {
        var row = await unitOfWork.Connection.QuerySingleOrDefaultAsync<RefreshTokenRow>(Command(
            """
            SELECT id, user_id, token_hash, family_id, expires_at, absolute_expires_at, created_at,
                   host(created_by_ip) AS created_by_ip, revoked_at, replaced_by_id
              FROM identity.refresh_tokens
             WHERE token_hash = @TokenHash
            """,
            new { TokenHash = tokenHash },
            cancellationToken));

        return row is null
            ? null
            : RefreshToken.Rehydrate(
                row.Id, new UserId(row.UserId), row.TokenHash, row.FamilyId, row.ExpiresAt, row.AbsoluteExpiresAt,
                row.CreatedAt, row.CreatedByIp, row.RevokedAt, row.ReplacedById);
    }

    public Task AddAsync(RefreshToken token, CancellationToken cancellationToken) =>
        unitOfWork.Connection.ExecuteAsync(Command(
            """
            INSERT INTO identity.refresh_tokens
              (id, user_id, token_hash, family_id, expires_at, absolute_expires_at, created_at, created_by_ip)
            VALUES (@Id, @UserId, @TokenHash, @FamilyId, @ExpiresAt, @AbsoluteExpiresAt, @CreatedAt, @CreatedByIp::inet)
            """,
            new
            {
                token.Id,
                UserId = token.UserId.Value,
                token.TokenHash,
                token.FamilyId,
                ExpiresAt = token.ExpiresAtUtc,
                AbsoluteExpiresAt = token.AbsoluteExpiresAtUtc,
                CreatedAt = token.CreatedAtUtc,
                token.CreatedByIp,
            },
            cancellationToken));

    public async Task<bool> TryRevokeAsync(RefreshToken token, CancellationToken cancellationToken)
    {
        var updated = await unitOfWork.Connection.ExecuteAsync(Command(
            """
            UPDATE identity.refresh_tokens
               SET revoked_at = @RevokedAt, replaced_by_id = @ReplacedById
             WHERE id = @Id AND revoked_at IS NULL
            """,
            new { token.Id, RevokedAt = token.RevokedAtUtc, token.ReplacedById },
            cancellationToken));

        return updated == 1;
    }

    public Task RevokeFamilyAsync(Guid familyId, DateTime nowUtc, CancellationToken cancellationToken) =>
        unitOfWork.Connection.ExecuteAsync(Command(
            "UPDATE identity.refresh_tokens SET revoked_at = @Now WHERE family_id = @FamilyId AND revoked_at IS NULL",
            new { FamilyId = familyId, Now = nowUtc },
            cancellationToken));

    public Task RevokeAllForUserAsync(UserId userId, DateTime nowUtc, CancellationToken cancellationToken) =>
        unitOfWork.Connection.ExecuteAsync(Command(
            "UPDATE identity.refresh_tokens SET revoked_at = @Now WHERE user_id = @UserId AND revoked_at IS NULL",
            new { UserId = userId.Value, Now = nowUtc },
            cancellationToken));

    private CommandDefinition Command(string sql, object parameters, CancellationToken cancellationToken) =>
        new(sql, parameters, unitOfWork.Transaction, cancellationToken: cancellationToken);

    private sealed class RefreshTokenRow
    {
        public Guid Id { get; init; }
        public Guid UserId { get; init; }
        public string TokenHash { get; init; } = string.Empty;
        public Guid FamilyId { get; init; }
        public DateTime ExpiresAt { get; init; }
        public DateTime AbsoluteExpiresAt { get; init; }
        public DateTime CreatedAt { get; init; }
        public string? CreatedByIp { get; init; }
        public DateTime? RevokedAt { get; init; }
        public Guid? ReplacedById { get; init; }
    }
}

internal sealed class UserTokenRepository(IUnitOfWork unitOfWork) : IUserTokenRepository
{
    public async Task<UserToken?> GetByHashAsync(string tokenHash, CancellationToken cancellationToken)
    {
        var row = await unitOfWork.Connection.QuerySingleOrDefaultAsync<UserTokenRow>(Command(
            """
            SELECT id, user_id, purpose, token_hash, expires_at, used_at, created_at
              FROM identity.user_tokens
             WHERE token_hash = @TokenHash
            """,
            new { TokenHash = tokenHash },
            cancellationToken));

        return row is null
            ? null
            : UserToken.Rehydrate(
                row.Id, new UserId(row.UserId), Enum.Parse<UserTokenPurpose>(row.Purpose), row.TokenHash,
                row.ExpiresAt, row.UsedAt, row.CreatedAt);
    }

    public Task AddAsync(UserToken token, CancellationToken cancellationToken) =>
        unitOfWork.Connection.ExecuteAsync(Command(
            """
            INSERT INTO identity.user_tokens (id, user_id, purpose, token_hash, expires_at, used_at, created_at)
            VALUES (@Id, @UserId, @Purpose, @TokenHash, @ExpiresAt, NULL, @CreatedAt)
            """,
            new
            {
                token.Id,
                UserId = token.UserId.Value,
                Purpose = token.Purpose.ToString(),
                token.TokenHash,
                ExpiresAt = token.ExpiresAtUtc,
                CreatedAt = token.CreatedAtUtc,
            },
            cancellationToken));

    public Task MarkUsedAsync(UserToken token, CancellationToken cancellationToken) =>
        unitOfWork.Connection.ExecuteAsync(Command(
            "UPDATE identity.user_tokens SET used_at = @UsedAt WHERE id = @Id AND used_at IS NULL",
            new { token.Id, UsedAt = token.UsedAtUtc },
            cancellationToken));

    public Task InvalidateAllAsync(UserId userId, UserTokenPurpose purpose, DateTime nowUtc, CancellationToken cancellationToken) =>
        unitOfWork.Connection.ExecuteAsync(Command(
            """
            UPDATE identity.user_tokens SET used_at = @Now
             WHERE user_id = @UserId AND purpose = @Purpose AND used_at IS NULL
            """,
            new { UserId = userId.Value, Purpose = purpose.ToString(), Now = nowUtc },
            cancellationToken));

    private CommandDefinition Command(string sql, object parameters, CancellationToken cancellationToken) =>
        new(sql, parameters, unitOfWork.Transaction, cancellationToken: cancellationToken);

    private sealed class UserTokenRow
    {
        public Guid Id { get; init; }
        public Guid UserId { get; init; }
        public string Purpose { get; init; } = string.Empty;
        public string TokenHash { get; init; } = string.Empty;
        public DateTime ExpiresAt { get; init; }
        public DateTime? UsedAt { get; init; }
        public DateTime CreatedAt { get; init; }
    }
}
