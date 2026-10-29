namespace ECommerce.Domain.Identity;

/// <summary>Repositórios do contexto Identity (arquivo 06 §3); implementados na Infrastructure com Dapper.</summary>
public interface IUserRepository
{
    Task<User?> GetByIdAsync(UserId id, CancellationToken cancellationToken);
    Task<User?> GetByEmailAsync(Email email, CancellationToken cancellationToken);
    Task<bool> ExistsByEmailAsync(Email email, CancellationToken cancellationToken);
    Task<bool> AnyActiveAdminAsync(CancellationToken cancellationToken);
    /// <summary><c>false</c> quando outro cadastro com o mesmo e-mail venceu a corrida (UNIQUE no banco).</summary>
    Task<bool> TryAddAsync(User user, CancellationToken cancellationToken);

    /// <summary>Concorrência otimista: <c>false</c> quando a versão mudou desde a leitura.</summary>
    Task<bool> UpdateAsync(User user, CancellationToken cancellationToken);
}

public interface IRefreshTokenRepository
{
    Task<RefreshToken?> GetByHashAsync(string tokenHash, CancellationToken cancellationToken);
    Task AddAsync(RefreshToken token, CancellationToken cancellationToken);
    /// <summary><c>false</c> quando outro request já revogou o token (duas renovações simultâneas).</summary>
    Task<bool> TryRevokeAsync(RefreshToken token, CancellationToken cancellationToken);

    /// <summary>RF-003 CA2: reuso de token revogado derruba a família inteira.</summary>
    Task RevokeFamilyAsync(Guid familyId, DateTime nowUtc, CancellationToken cancellationToken);

    /// <summary>Troca/redefinição de senha encerra todas as sessões (arquivo 08 §4.5).</summary>
    Task RevokeAllForUserAsync(UserId userId, DateTime nowUtc, CancellationToken cancellationToken);
}

public interface IUserTokenRepository
{
    Task<UserToken?> GetByHashAsync(string tokenHash, CancellationToken cancellationToken);
    Task AddAsync(UserToken token, CancellationToken cancellationToken);
    Task MarkUsedAsync(UserToken token, CancellationToken cancellationToken);

    /// <summary>Ao emitir um link novo, os anteriores do mesmo propósito deixam de valer.</summary>
    Task InvalidateAllAsync(UserId userId, UserTokenPurpose purpose, DateTime nowUtc, CancellationToken cancellationToken);
}
