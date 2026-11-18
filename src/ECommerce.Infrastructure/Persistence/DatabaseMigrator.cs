using System.Reflection;
using System.Text.RegularExpressions;
using Dapper;
using DbUp;
using DbUp.Engine;
using Npgsql;

namespace ECommerce.Infrastructure.Persistence;

/// <summary>
/// Aplica as migrations SQL (DbUp) — usado pelo ECommerce.Migrator e pelos testes.
/// A API não migra sozinha (arquivo 05 §4).
/// </summary>
public static partial class DatabaseMigrator
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

    /// <summary>
    /// Privilégio mínimo (arquivo 17 §4, entrega 6 §3 P6): cria ou atualiza o papel com que a API se conecta, só com
    /// leitura e escrita de dados. Nada de DDL, de mexer no journal das migrations nem de criar papel. Roda depois de
    /// cada migração, então schema e tabela novos ganham as permissões na mesma subida.
    /// </summary>
    public static void ApplyAppRole(string ownerConnectionString, string role, string password)
    {
        if (!AppRoleName().IsMatch(role))
        {
            throw new ArgumentException("Nome do papel da aplicação inválido: use letras minúsculas, números e _.", nameof(role));
        }

        if (password.Length < 12)
        {
            throw new ArgumentException("A senha do papel da aplicação precisa de pelo menos 12 caracteres.", nameof(password));
        }

        using var connection = new NpgsqlConnection(ownerConnectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();

        var owner = connection.ExecuteScalar<string>("SELECT current_user", transaction: transaction);
        if (string.Equals(owner, role, StringComparison.Ordinal))
        {
            throw new ArgumentException("O papel da aplicação não pode ser o mesmo dono do banco.", nameof(role));
        }

        // CREATE/ALTER ROLE não aceitam parâmetro: o format() do próprio Postgres cita nome e senha com segurança.
        var exists = connection.ExecuteScalar<bool>("SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = @Role)", new { Role = role }, transaction);
        var statements = new List<string>
        {
            connection.ExecuteScalar<string>(
                exists
                    ? "SELECT format('ALTER ROLE %I WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD %L', @Role, @Password)"
                    : "SELECT format('CREATE ROLE %I WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD %L', @Role, @Password)",
                new { Role = role, Password = password },
                transaction)!,
            connection.ExecuteScalar<string>("SELECT format('GRANT CONNECT ON DATABASE %I TO %I', current_database(), @Role)", new { Role = role }, transaction)!,
            // public guarda só o journal das migrations e funções de apoio: a API lê (readiness), não escreve nem cria.
            "REVOKE CREATE ON SCHEMA public FROM PUBLIC",
            connection.ExecuteScalar<string>(
                "SELECT format('GRANT USAGE ON SCHEMA public TO %1$I; GRANT SELECT ON ALL TABLES IN SCHEMA public TO %1$I', @Role)",
                new { Role = role },
                transaction)!,
        };

        var schemas = connection.Query<string>(
            """
            SELECT nspname FROM pg_namespace
             WHERE nspname NOT LIKE 'pg\_%' AND nspname NOT IN ('information_schema', 'public')
             ORDER BY nspname
            """,
            transaction: transaction);
        foreach (var schema in schemas)
        {
            statements.Add(connection.ExecuteScalar<string>(
                """
                SELECT format('GRANT USAGE ON SCHEMA %1$I TO %2$I; '
                           || 'GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA %1$I TO %2$I; '
                           || 'GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA %1$I TO %2$I', @Schema, @Role)
                """,
                new { Schema = schema, Role = role },
                transaction)!);
        }

        foreach (var statement in statements)
        {
            connection.Execute(statement, transaction: transaction);
        }

        transaction.Commit();
    }

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex AppRoleName();

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
