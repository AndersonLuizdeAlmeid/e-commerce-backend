using ECommerce.Application.Abstractions.Caching;
using Microsoft.Extensions.Caching.Hybrid;

namespace ECommerce.Infrastructure.Caching;

/// <summary>HybridCache: L1 em memória com proteção contra stampede e invalidação por tag (arquivo 12 §2).</summary>
internal sealed class HybridCacheService(HybridCache cache) : ICacheService
{
    public ValueTask<T> GetOrCreateAsync<T>(
        string key, Func<CancellationToken, ValueTask<T>> factory, CachePolicy policy, CancellationToken cancellationToken) =>
        cache.GetOrCreateAsync(
            key,
            factory,
            static (state, ct) => state(ct),
            new HybridCacheEntryOptions { Expiration = policy.Expiration, LocalCacheExpiration = policy.Expiration },
            policy.Tags,
            cancellationToken);

    public ValueTask RemoveByTagAsync(string tag, CancellationToken cancellationToken) =>
        cache.RemoveByTagAsync(tag, cancellationToken);
}
