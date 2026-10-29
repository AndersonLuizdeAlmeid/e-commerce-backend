using System.Reflection;
using DbUp;
using DbUp.Engine;

namespace ECommerce.Infrastructure.Persistence;

/// <summary>
/// Aplica as migrations SQL (DbUp) — usado pelo ECommerce.Migrator e pelos testes.
/// A API não migra sozinha (arquivo 05 §4).
/// </summary>
public static class DatabaseMigrator
{
    private const string MigrationsPrefix = "ECommerce.Infrastructure.Persistence.Migrations.";
    private const string DevelopmentSeedsPrefix = "ECommerce.Infrastructure.Persistence.Seeds.Development.";

    private static readonly Assembly ScriptsAssembly = typeof(DatabaseMigrator).Assembly;

    public static DatabaseUpgradeResult Migrate(string connectionString, bool includeDevelopmentSeed)
    {
        var migrations = BuildUpgrader(connectionString, MigrationsPrefix, "schemaversions", logToConsole: true)
            .PerformUpgrade();

        if (!migrations.Successful || !includeDevelopmentSeed)
        {
            return migrations;
        }

        return BuildUpgrader(connectionString, DevelopmentSeedsPrefix, "seedversions", logToConsole: true)
            .PerformUpgrade();
    }

    /// <summary>Usado pelo health check de readiness: há migrations pendentes?</summary>
    public static bool HasPendingMigrations(string connectionString) =>
        BuildUpgrader(connectionString, MigrationsPrefix, "schemaversions", logToConsole: false)
            .IsUpgradeRequired();

    private static UpgradeEngine BuildUpgrader(
        string connectionString, string resourcePrefix, string journalTable, bool logToConsole)
    {
        var builder = DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(ScriptsAssembly, name => name.StartsWith(resourcePrefix, StringComparison.Ordinal))
            .JournalToPostgresqlTable("public", journalTable)
            .WithTransactionPerScript();

        builder = logToConsole ? builder.LogToConsole() : builder.LogToNowhere();

        return builder.Build();
    }
}
