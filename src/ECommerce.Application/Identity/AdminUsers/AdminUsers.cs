using Dapper;
using ECommerce.Application.Abstractions.Data;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Application.Abstractions.Paging;
using ECommerce.Application.Ordering;
using ECommerce.Domain.Abstractions;
using ECommerce.Domain.Identity;
using FluentValidation;

namespace ECommerce.Application.Identity.AdminUsers;

// RF-054 — usuários no painel. Entrega 5, fatia 5.3.

/// <summary><see cref="IsSelf"/>: a linha de quem está olhando, sem ações (CA1).</summary>
public sealed record AdminUserResponse(
    Guid Id,
    string Email,
    string? Name,
    string Role,
    bool IsActive,
    bool EmailConfirmed,
    bool IsLockedOut,
    DateTime CreatedAt,
    int OrderCount,
    DateTime? LastOrderAt,
    bool IsSelf);

/// <summary><see cref="Search"/>: nome ou e-mail.</summary>
public sealed record ListAdminUsersQuery(
    UserId ViewerId,
    int Page = 1,
    int PageSize = ListAdminUsersQuery.DefaultPageSize,
    string? Role = null,
    bool? IsActive = null,
    string? Search = null) : IQuery<PagedResponse<AdminUserResponse>>
{
    public const int DefaultPageSize = 30;
    public const int MaxPageSize = 100;
}

public sealed record GetAdminUserQuery(UserId ViewerId, Guid UserId) : IQuery<AdminUserResponse>;

public sealed record SetUserActiveCommand(Guid UserId, bool IsActive, UserId AdminId) : ICommand;

public sealed record SetUserRoleCommand(Guid UserId, string Role, UserId AdminId) : ICommand;

internal sealed class ListAdminUsersValidator : AbstractValidator<ListAdminUsersQuery>
{
    public ListAdminUsersValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, ListAdminUsersQuery.MaxPageSize);
        RuleFor(x => x.Role)
            .Must(role => Enum.TryParse<Role>(role, ignoreCase: false, out _))
            .When(x => x.Role is not null)
            .WithMessage("Perfil desconhecido.");
        RuleFor(x => x.Search).MaximumLength(120);
    }
}

internal sealed class SetUserRoleValidator : AbstractValidator<SetUserRoleCommand>
{
    public SetUserRoleValidator() =>
        RuleFor(x => x.Role)
            .Must(role => Enum.TryParse<Role>(role, ignoreCase: false, out _))
            .WithMessage("Escolha Customer ou Admin.");
}

internal static class AdminUserSql
{
    public const string Select = """
        SELECT u.id, u.email::text AS email, c.full_name AS name, u.role, u.is_active,
               u.email_confirmed_at IS NOT NULL AS email_confirmed,
               coalesce(u.lockout_end_utc > @Now, false) AS is_locked_out,
               u.created_at,
               (SELECT count(*) FROM ordering.orders o WHERE o.customer_id = u.id)::int AS order_count,
               (SELECT max(o.placed_at) FROM ordering.orders o WHERE o.customer_id = u.id) AS last_order_at
          FROM identity.users u
          LEFT JOIN customers.customers c ON c.id = u.id
        """;

    public static AdminUserResponse Map(Row row, UserId viewer) => new(
        row.Id,
        row.Email,
        row.Name,
        row.Role,
        row.IsActive,
        row.EmailConfirmed,
        row.IsLockedOut,
        DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc),
        row.OrderCount,
        row.LastOrderAt is { } last ? DateTime.SpecifyKind(last, DateTimeKind.Utc) : null,
        row.Id == viewer.Value);

    internal sealed class Row
    {
        public Guid Id { get; init; }
        public string Email { get; init; } = string.Empty;
        public string? Name { get; init; }
        public string Role { get; init; } = string.Empty;
        public bool IsActive { get; init; }
        public bool EmailConfirmed { get; init; }
        public bool IsLockedOut { get; init; }
        public DateTime CreatedAt { get; init; }
        public int OrderCount { get; init; }
        public DateTime? LastOrderAt { get; init; }
    }
}

internal sealed class ListAdminUsersHandler(IDbConnectionFactory connectionFactory, TimeProvider time)
    : IQueryHandler<ListAdminUsersQuery, PagedResponse<AdminUserResponse>>
{
    private const string Where = """
         WHERE (@Role::text IS NULL OR u.role = @Role)
           AND (@IsActive::boolean IS NULL OR u.is_active = @IsActive)
           AND (@Search::text IS NULL OR u.email::text ILIKE '%' || @Search || '%' OR c.full_name ILIKE '%' || @Search || '%')
        """;

    private const string Sql = $"""
        SELECT count(*)::int FROM identity.users u LEFT JOIN customers.customers c ON c.id = u.id {Where};

        {AdminUserSql.Select}
        {Where}
         ORDER BY u.created_at DESC, u.id DESC
         LIMIT @PageSize OFFSET @Offset;
        """;

    public async Task<Result<PagedResponse<AdminUserResponse>>> Handle(ListAdminUsersQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        using var grid = await connection.QueryMultipleAsync(new CommandDefinition(
            Sql,
            new
            {
                query.Role,
                query.IsActive,
                Search = string.IsNullOrWhiteSpace(query.Search) ? null : SqlLike.Escape(query.Search.Trim()),
                Now = time.GetUtcNow().UtcDateTime,
                query.PageSize,
                Offset = (query.Page - 1) * query.PageSize,
            },
            cancellationToken: cancellationToken));

        var total = await grid.ReadSingleAsync<int>();
        var items = (await grid.ReadAsync<AdminUserSql.Row>()).Select(row => AdminUserSql.Map(row, query.ViewerId)).ToList();
        return PagedResponse.Create<AdminUserResponse>(items, query.Page, query.PageSize, total);
    }
}

internal sealed class GetAdminUserHandler(IDbConnectionFactory connectionFactory, TimeProvider time) : IQueryHandler<GetAdminUserQuery, AdminUserResponse>
{
    public async Task<Result<AdminUserResponse>> Handle(GetAdminUserQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<AdminUserSql.Row>(new CommandDefinition(
            $"{AdminUserSql.Select} WHERE u.id = @Id",
            new { Id = query.UserId, Now = time.GetUtcNow().UtcDateTime },
            cancellationToken: cancellationToken));

        return row is null ? IdentityErrors.UserNotFound : AdminUserSql.Map(row, query.ViewerId);
    }
}

/// <summary>
/// Regras comuns às duas mudanças: ninguém mexe em si mesma (CA1), quem age ainda é administradora ativa, e a loja nunca
/// fica sem administradora ativa (CA2), contando com as linhas travadas (entrega 5 §3, P8).
/// </summary>
internal static class AdminUserChange
{
    private static readonly Error NotAnAdmin = Error.Forbidden("Identity.NotAnAdmin", "Sua conta não é mais administradora.");

    public static async Task<Result<User>> LoadAsync(
        IUserRepository users, Guid targetId, UserId adminId, bool removesAdmin, CancellationToken cancellationToken)
    {
        if (targetId == adminId.Value)
        {
            return IdentityErrors.CannotChangeSelf;
        }

        var activeAdmins = await users.LockActiveAdminsAsync(cancellationToken);
        if (!activeAdmins.Contains(adminId))
        {
            return NotAnAdmin;
        }

        var user = await users.GetByIdAsync(new UserId(targetId), cancellationToken);
        if (user is null)
        {
            return IdentityErrors.UserNotFound;
        }

        if (removesAdmin && user.Role == Role.Admin && user.IsActive && activeAdmins.All(id => id == user.Id))
        {
            return IdentityErrors.LastAdmin;
        }

        return user;
    }
}

/// <summary>Ativar ou desativar (RF-054). Desativar encerra todas as sessões (entrega 5 §3, P7).</summary>
internal sealed class SetUserActiveHandler(IUserRepository users, IRefreshTokenRepository refreshTokens, TimeProvider time)
    : ICommandHandler<SetUserActiveCommand>
{
    public async Task<Result> Handle(SetUserActiveCommand command, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var loaded = await AdminUserChange.LoadAsync(users, command.UserId, command.AdminId, removesAdmin: !command.IsActive, cancellationToken);
        if (loaded.IsFailure)
        {
            return Result.Failure(loaded.Error);
        }

        var user = loaded.Value;
        if (!user.SetActive(command.IsActive, now))
        {
            return Result.Success();
        }

        if (!await users.UpdateAsync(user, cancellationToken))
        {
            return Result.Failure(IdentityErrors.ConcurrencyConflict);
        }

        await refreshTokens.RevokeAllForUserAsync(user.Id, now, cancellationToken);
        return Result.Success();
    }
}

/// <summary>
/// Promover ou rebaixar (RF-054). As sessões caem para o papel novo valer já no próximo login. Rebaixada sem cadastro de
/// cliente (a do seed) ganha um, com o nome tirado do e-mail (entrega 5 §3, P9).
/// </summary>
internal sealed class SetUserRoleHandler(IUserRepository users, IRefreshTokenRepository refreshTokens, IUnitOfWork unitOfWork, TimeProvider time)
    : ICommandHandler<SetUserRoleCommand>
{
    public async Task<Result> Handle(SetUserRoleCommand command, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var role = Enum.Parse<Role>(command.Role);
        var loaded = await AdminUserChange.LoadAsync(users, command.UserId, command.AdminId, removesAdmin: role != Role.Admin, cancellationToken);
        if (loaded.IsFailure)
        {
            return Result.Failure(loaded.Error);
        }

        var user = loaded.Value;
        if (!user.ChangeRole(role, now))
        {
            return Result.Success();
        }

        if (!await users.UpdateAsync(user, cancellationToken))
        {
            return Result.Failure(IdentityErrors.ConcurrencyConflict);
        }

        await refreshTokens.RevokeAllForUserAsync(user.Id, now, cancellationToken);

        if (role == Role.Customer)
        {
            await unitOfWork.Connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO customers.customers (id, full_name, created_at, updated_at)
                SELECT u.id, split_part(u.email::text, '@', 1), @Now, @Now FROM identity.users u WHERE u.id = @Id
                ON CONFLICT (id) DO NOTHING
                """,
                new { Id = user.Id.Value, Now = now },
                unitOfWork.Transaction,
                cancellationToken: cancellationToken));
        }

        return Result.Success();
    }
}
