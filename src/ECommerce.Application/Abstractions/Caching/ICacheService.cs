namespace ECommerce.Application.Abstractions.Caching;

/// <summary>Cache de leitura do catálogo (arquivo 12). A implementação usa HybridCache.</summary>
public interface ICacheService
{
    ValueTask<T> GetOrCreateAsync<T>(
        string key, Func<CancellationToken, ValueTask<T>> factory, CachePolicy policy, CancellationToken cancellationToken);

    /// <summary>Invalida as entradas da tag; chamado pelos comandos do admin depois do commit (arquivo 12 §4).</summary>
    ValueTask RemoveByTagAsync(string tag, CancellationToken cancellationToken);
}

public sealed record CachePolicy(TimeSpan Expiration, IReadOnlyCollection<string> Tags);
