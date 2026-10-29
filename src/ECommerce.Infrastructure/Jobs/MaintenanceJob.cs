using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace ECommerce.Infrastructure.Jobs;

/// <summary>
/// Limpeza diária (arquivo 08 §7, arquivo 05 §6): tokens expirados ou revogados há mais de 30 dias e Outbox
/// processado há mais de 90. O advisory lock evita duas instâncias limpando ao mesmo tempo (arquivo 25 §2).
/// </summary>
internal sealed partial class MaintenanceJob(NpgsqlDataSource dataSource, TimeProvider time, ILogger<MaintenanceJob> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    private const string Sql = """
        DO $$
        BEGIN
            IF pg_try_advisory_xact_lock(hashtext('maintenance-job')) THEN
                DELETE FROM identity.refresh_tokens
                 WHERE coalesce(revoked_at, expires_at) < now() - interval '30 days';
                DELETE FROM identity.user_tokens
                 WHERE coalesce(used_at, expires_at) < now() - interval '30 days';
                DELETE FROM messaging.outbox_messages
                 WHERE processed_at < now() - interval '90 days';
            END IF;
        END
        $$;
        """;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var connection = await dataSource.OpenConnectionAsync(stoppingToken);
                await connection.ExecuteAsync(new CommandDefinition(Sql, cancellationToken: stoppingToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(logger, ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Falha na limpeza diária")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
