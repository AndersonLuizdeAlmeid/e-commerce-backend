using Dapper;
using ECommerce.Api.FunctionalTests.Infrastructure;
using ECommerce.Infrastructure.Persistence;
using Npgsql;

namespace ECommerce.Api.FunctionalTests.Platform;

/// <summary>Privilégio mínimo do papel da API (arquivo 17 §4, entrega 6 §3 P6, critério B2).</summary>
public sealed class DatabasePrivilegeTests(ApiFactory factory) : FunctionalTest(factory)
{
    private async Task<PostgresException> DeniedAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(Factory.AppConnectionString);
        var error = await Should.ThrowAsync<PostgresException>(() => connection.ExecuteAsync(sql));
        return error;
    }

    [Theory, Trait("Spec", "ENT-6-B2")]
    [InlineData("CREATE TABLE catalog.invasora (id int)")]
    [InlineData("CREATE TABLE public.invasora (id int)")]
    [InlineData("DROP TABLE ordering.order_items")]
    [InlineData("ALTER TABLE identity.users ADD COLUMN extra text")]
    [InlineData("TRUNCATE payments.payments")]
    [InlineData("CREATE SCHEMA invasor")]
    [InlineData("CREATE ROLE invasor")]
    [InlineData("DELETE FROM public.schemaversions")]
    public async Task AppRole_CannotChangeTheSchemaNorTheMigrationJournal(string sql)
    {
        var error = await DeniedAsync(sql);

        error.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact, Trait("Spec", "ENT-6-B2")]
    public async Task AppRole_ReadsAndWritesData_AndReadsTheJournal()
    {
        await using var connection = new NpgsqlConnection(Factory.AppConnectionString);

        await connection.ExecuteAsync("INSERT INTO ops.job_marks (key, last_at) VALUES ('teste', now())");
        await connection.ExecuteAsync("UPDATE ops.job_marks SET last_at = now() WHERE key = 'teste'");
        (await connection.ExecuteScalarAsync<long>("SELECT nextval('ordering.order_number_seq')")).ShouldBeGreaterThan(0);
        await connection.ExecuteAsync("DELETE FROM ops.job_marks WHERE key = 'teste'");
        (await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM public.schemaversions")).ShouldBeGreaterThan(0);
        (await connection.ExecuteScalarAsync<bool>("SELECT rolsuper OR rolcreaterole OR rolcreatedb FROM pg_roles WHERE rolname = current_user")).ShouldBeFalse();
    }

    [Fact, Trait("Spec", "ENT-6-B2")]
    public async Task ApplyAppRole_IsIdempotent_AndRejectsTheOwnerOrBadNames()
    {
        // Rodar de novo (próximo deploy) só reaplica as permissões.
        DatabaseMigrator.ApplyAppRole(Factory.ConnectionString, ApiFactory.AppRole, "mo-app-testes-funcionais");

        var owner = new NpgsqlConnectionStringBuilder(Factory.ConnectionString).Username!;
        Should.Throw<ArgumentException>(() => DatabaseMigrator.ApplyAppRole(Factory.ConnectionString, owner, "senha-longa-o-bastante"));
        Should.Throw<ArgumentException>(() => DatabaseMigrator.ApplyAppRole(Factory.ConnectionString, "mo_app; DROP", "senha-longa-o-bastante"));
        Should.Throw<ArgumentException>(() => DatabaseMigrator.ApplyAppRole(Factory.ConnectionString, "mo_app", "curta"));
    }
}
