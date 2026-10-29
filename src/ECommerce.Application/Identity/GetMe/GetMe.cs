using Dapper;
using ECommerce.Application.Abstractions.Data;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Domain.Abstractions;
using ECommerce.Domain.Identity;

namespace ECommerce.Application.Identity.GetMe;

/// <summary><c>GET /auth/me</c> — quem está logado (arquivo 07 §2).</summary>
public sealed record GetMeQuery(UserId UserId) : IQuery<MeResponse>;

public sealed record MeResponse(Guid Id, string Email, string Role, bool EmailConfirmed, string? FullName);

internal sealed class GetMeHandler(IDbConnectionFactory connectionFactory) : IQueryHandler<GetMeQuery, MeResponse>
{
    private const string Sql = """
        SELECT u.id, u.email::text AS email, u.role, u.email_confirmed_at IS NOT NULL AS email_confirmed, c.full_name
          FROM identity.users u
          LEFT JOIN customers.customers c ON c.id = u.id
         WHERE u.id = @Id AND u.is_active
        """;

    public async Task<Result<MeResponse>> Handle(GetMeQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<MeRow>(
            new CommandDefinition(Sql, new { Id = query.UserId.Value }, cancellationToken: cancellationToken));

        return row is null
            ? IdentityErrors.UserNotFound
            : new MeResponse(row.Id, row.Email, row.Role, row.EmailConfirmed, row.FullName);
    }

    private sealed class MeRow
    {
        public Guid Id { get; init; }
        public string Email { get; init; } = string.Empty;
        public string Role { get; init; } = string.Empty;
        public bool EmailConfirmed { get; init; }
        public string? FullName { get; init; }
    }
}
