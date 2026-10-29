using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace ECommerce.Infrastructure.Persistence;

/// <summary>Readiness: PostgreSQL acessível e migrations aplicadas (arquivo 14 §4).</summary>
internal sealed class DatabaseHealthCheck(NpgsqlDataSource dataSource, IConfiguration configuration) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var command = dataSource.CreateCommand("SELECT 1");
            await command.ExecuteScalarAsync(cancellationToken);
        }
        catch (NpgsqlException ex)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL inacessível.", ex);
        }

        // NpgsqlDataSource.ConnectionString omite a senha; o DbUp precisa da original.
        var connectionString = configuration.GetConnectionString("Database")!;

        return DatabaseMigrator.HasPendingMigrations(connectionString)
            ? HealthCheckResult.Unhealthy("Há migrations pendentes.")
            : HealthCheckResult.Healthy();
    }
}
