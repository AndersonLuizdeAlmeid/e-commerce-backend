using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace ECommerce.Infrastructure.Messaging;

/// <summary>
/// Lê lotes do Outbox com <c>FOR UPDATE SKIP LOCKED</c> (seguro com várias instâncias), despacha para o handler do
/// tipo e marca como processada; em falha, tenta de novo com backoff exponencial (arquivo 25 §1).
/// </summary>
internal sealed partial class OutboxProcessor(
    NpgsqlDataSource dataSource,
    IServiceScopeFactory scopeFactory,
    IOptions<OutboxSettings> options,
    TimeProvider time,
    ILogger<OutboxProcessor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(options.Value.PollingIntervalMilliseconds), time);
        do
        {
            try
            {
                while (await ProcessBatchAsync(stoppingToken) == options.Value.BatchSize)
                {
                    // Lote cheio: provavelmente há mais mensagens esperando.
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogBatchFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Processa um lote e devolve quantas mensagens pegou.</summary>
    internal async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var messages = (await connection.QueryAsync<OutboxRow>(new CommandDefinition(
            """
            SELECT id, type, payload::text AS payload, attempts
              FROM messaging.outbox_messages
             WHERE processed_at IS NULL AND failed_at IS NULL AND next_attempt_at <= @Now
             ORDER BY next_attempt_at
             LIMIT @BatchSize
             FOR UPDATE SKIP LOCKED
            """,
            new { Now = time.GetUtcNow().UtcDateTime, settings.BatchSize },
            transaction,
            cancellationToken: cancellationToken))).AsList();

        if (messages.Count == 0)
        {
            return 0;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var handlers = scope.ServiceProvider.GetServices<IOutboxMessageHandler>().ToDictionary(h => h.Type, StringComparer.Ordinal);

        foreach (var message in messages)
        {
            string? error = null;
            try
            {
                if (!handlers.TryGetValue(message.Type, out var handler))
                {
                    throw new InvalidOperationException($"Nenhum handler para o tipo '{message.Type}'.");
                }

                await handler.HandleAsync(message.Payload, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                error = ex.Message;
                LogMessageFailed(logger, message.Type, message.Id, message.Attempts + 1, ex);
            }

            await MarkAsync(connection, transaction, message, error, settings.MaxAttempts, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return messages.Count;
    }

    private async Task MarkAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, OutboxRow message, string? error, int maxAttempts, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var attempts = message.Attempts + 1;

        // Sucesso: apaga os segredos (tokens dos links) do payload; o resto fica para auditoria.
        const string Processed = """
            UPDATE messaging.outbox_messages
               SET processed_at = @Now, attempts = @Attempts, last_error = NULL, payload = payload - 'secrets'
             WHERE id = @Id
            """;
        const string Retry = """
            UPDATE messaging.outbox_messages
               SET attempts = @Attempts, last_error = @Error, next_attempt_at = @NextAttempt,
                   failed_at = CASE WHEN @Attempts >= @MaxAttempts THEN @Now END
             WHERE id = @Id
            """;

        // Backoff: 30 s, 1 min, 2 min, 4 min...
        var nextAttempt = now.AddSeconds(30 * Math.Pow(2, attempts - 1));
        await connection.ExecuteAsync(new CommandDefinition(
            error is null ? Processed : Retry,
            new { message.Id, Now = now, Attempts = attempts, Error = Truncate(error), NextAttempt = nextAttempt, MaxAttempts = maxAttempts },
            transaction,
            cancellationToken: cancellationToken));
    }

    private static string? Truncate(string? value) => value is { Length: > 2000 } ? value[..2000] : value;

    [LoggerMessage(Level = LogLevel.Error, Message = "Falha ao processar lote do Outbox")]
    private static partial void LogBatchFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Falha na mensagem {Type} {MessageId} (tentativa {Attempt})")]
    private static partial void LogMessageFailed(ILogger logger, string type, Guid messageId, int attempt, Exception exception);

    private sealed class OutboxRow
    {
        public Guid Id { get; init; }
        public string Type { get; init; } = string.Empty;
        public string Payload { get; init; } = string.Empty;
        public int Attempts { get; init; }
    }
}
