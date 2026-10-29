using Dapper;
using ECommerce.Application.Abstractions.Caching;
using ECommerce.Application.Abstractions.Data;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Domain.Abstractions;
using ECommerce.Domain.Identity;

namespace ECommerce.Application.Identity.GetSecurityStamp;

/// <summary>
/// Security stamp atual do usuário ativo, cacheado por 1 min (arquivo 08 §2, arquivo 12 §3). <c>null</c> quando o
/// usuário não existe ou foi desativado. Usado nas rotas sensíveis para derrubar tokens antigos sem esperar expirar.
/// </summary>
public sealed record GetSecurityStampQuery(UserId UserId) : IQuery<Guid?>;

internal sealed class GetSecurityStampHandler(IDbConnectionFactory connectionFactory, ICacheService cache)
    : IQueryHandler<GetSecurityStampQuery, Guid?>
{
    private static readonly TimeSpan Expiration = TimeSpan.FromMinutes(1);

    public async Task<Result<Guid?>> Handle(GetSecurityStampQuery query, CancellationToken cancellationToken)
    {
        var id = query.UserId.Value;
        var stamp = await cache.GetOrCreateAsync(
            $"identity:sstamp:{id}",
            async ct =>
            {
                await using var connection = await connectionFactory.OpenConnectionAsync(ct);
                return await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(
                    "SELECT security_stamp FROM identity.users WHERE id = @Id AND is_active",
                    new { Id = id },
                    cancellationToken: ct));
            },
            new CachePolicy(Expiration, [$"user:{id}"]),
            cancellationToken);

        return Result.Success(stamp);
    }
}
