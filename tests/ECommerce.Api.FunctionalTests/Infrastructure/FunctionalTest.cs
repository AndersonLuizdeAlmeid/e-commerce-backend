using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;

namespace ECommerce.Api.FunctionalTests.Infrastructure;

/// <summary>Base dos testes funcionais: banco limpo (Respawn) e cache vazio antes de cada teste.</summary>
[Collection(ApiTestGroup.Name)]
public abstract class FunctionalTest(ApiFactory factory) : IAsyncLifetime
{
    protected ApiFactory Factory { get; } = factory;
    protected HttpClient Client { get; } = factory.CreateClient();
    protected CatalogSeeder Catalog { get; } = new(factory.ConnectionString);

    protected static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await Factory.ResetDatabaseAsync();
        // "*" invalida todas as entradas do HybridCache; sem isso um teste leria o catálogo cacheado do anterior.
        await Factory.Services.GetRequiredService<HybridCache>().RemoveByTagAsync("*", CancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        Client.Dispose();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
