using ECommerce.Infrastructure.Notifications;
using ECommerce.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Respawn;
using Testcontainers.PostgreSql;

namespace ECommerce.Api.FunctionalTests.Infrastructure;

/// <summary>
/// API real + PostgreSQL real (Testcontainers), migrado pelo mesmo DbUp de produção (arquivo 15 §3).
/// E-mails vão para o <see cref="FakeEmailSender"/>; limites de requisição ficam altos, exceto nos testes de rate limit.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string AllowedOrigin = "https://loja.teste";
    public const string JwtTestKey = "DEV-ONLY-chave-dos-testes-funcionais-0123456789abcdef";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .Build();

    private Respawner _respawner = null!;

    public string ConnectionString => _postgres.GetConnectionString();

    public FakeEmailSender Emails { get; } = new();

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();

        var migration = DatabaseMigrator.Migrate(ConnectionString, includeDevelopmentSeed: false);
        migration.Successful.ShouldBeTrue(migration.Error?.ToString());

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        _respawner = await Respawner.CreateAsync(connection, new RespawnerOptions
        {
            DbAdapter = DbAdapter.Postgres,
            SchemasToInclude = ["catalog", "inventory", "identity", "customers", "messaging", "notifications"],
        });
    }

    public async Task ResetDatabaseAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await _respawner.ResetAsync(connection);
        Emails.Clear();
    }

    /// <summary>Altera o banco por fora da API (simula escrita do admin ou de outra instância).</summary>
    public async Task ExecuteSqlAsync(string sql, object? parameters = null)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await Dapper.SqlMapper.ExecuteAsync(connection, sql, parameters);
    }

    public async Task<T?> QuerySingleAsync<T>(string sql, object? parameters = null)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        return await Dapper.SqlMapper.QuerySingleOrDefaultAsync<T>(connection, sql, parameters);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Database", ConnectionString);
        builder.UseSetting("Cors:AllowedOrigins:0", AllowedOrigin);
        builder.UseSetting("Jwt:SigningKey", JwtTestKey);
        builder.UseSetting("Auth:RefreshCookie:Secure", "false"); // TestServer é http
        builder.UseSetting("Outbox:PollingIntervalMilliseconds", "200");
        builder.UseSetting("Email:FrontendBaseUrl", "https://loja.teste");
        builder.UseSetting("RateLimiting:Global:TokenLimit", "100000");
        builder.UseSetting("RateLimiting:Global:TokensPerPeriod", "100000");
        builder.UseSetting("RateLimiting:AuthStrict:PermitLimit", "100000");
        builder.UseSetting("RateLimiting:AuthRefresh:PermitLimit", "100000");
        builder.UseSetting("RateLimiting:PublicRead:PermitLimit", "100000");
        builder.UseSetting("RateLimiting:CustomerWrite:TokenLimit", "100000");

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(Emails);
        });
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class ApiTestGroup : ICollectionFixture<ApiFactory>
{
    public const string Name = "Api";
}
