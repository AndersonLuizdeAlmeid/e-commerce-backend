using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace ECommerce.Infrastructure.Messaging;

/// <summary>
/// Reserva mensagens do Outbox numa instrução curta (<c>FOR UPDATE SKIP LOCKED</c> + <c>locked_until</c>) e processa cada
/// uma <b>fora de transação</b>, em até <see cref="OutboxSettings.MaxParallelism"/> vagas. Assim que uma vaga libera, a
/// próxima mensagem é pega, sem esperar as outras: uma chamada lenta ocupa só a própria vaga. Em falha, tenta de novo
/// com backoff exponencial (arquivo 25 §1). Se a API cair no meio, a reserva vence e a mensagem volta para a fila: por
/// isso os handlers precisam ser idempotentes (entrega 6 §3, P1).
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
        var settings = options.Value;
        var slots = Math.Max(1, settings.MaxParallelism);
        var pollingInterval = TimeSpan.FromMilliseconds(settings.PollingIntervalMilliseconds);
        var inFlight = new List<Task>();

        while (!stoppingToken.IsCancellationRequested)
        {
            inFlight.RemoveAll(task => task.IsCompleted);
            var free = Math.Min(slots - inFlight.Count, settings.BatchSize);
            var claimed = 0;
            if (free > 0)
            {
                try
                {
                    var token = Guid.NewGuid();
                    var messages = await ClaimAsync(token, free, settings.LeaseSeconds, stoppingToken);
                    claimed = messages.Count;
                    inFlight.AddRange(messages.Select(message => ProcessAsync(message, token, settings.MaxAttempts, stoppingToken)));
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    LogBatchFailed(logger, ex);
                }
            }

            // Pegou todas as vagas livres: talvez haja mais esperando, então volta logo (se ainda sobrar vaga).
            if (claimed > 0 && claimed == free && inFlight.Count < slots)
            {
                continue;
            }

            // Espera o intervalo ou uma vaga liberar, o que vier primeiro.
            await Task.WhenAny([.. inFlight, Task.Delay(pollingInterval, time, stoppingToken)]);
        }

        // Parando: deixa terminar o que já começou; o que não terminar fica com a reserva e volta na próxima subida.
        await Task.WhenAll(inFlight);
    }

    /// <summary>
    /// Uma instrução só (commit automático): nenhuma linha fica travada enquanto as mensagens são processadas. Pega também
    /// mensagem de reserva vencida, de um processamento que não terminou.
    /// </summary>
    internal async Task<List<OutboxRow>> ClaimAsync(Guid token, int limit, int leaseSeconds, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return (await connection.QueryAsync<OutboxRow>(new CommandDefinition(
            """
            UPDATE messaging.outbox_messages m
               SET locked_until = @LockedUntil, lock_token = @Token
              FROM (SELECT id
                      FROM messaging.outbox_messages
                     WHERE processed_at IS NULL AND failed_at IS NULL AND next_attempt_at <= @Now
                       AND (locked_until IS NULL OR locked_until <= @Now)
                     ORDER BY next_attempt_at
                     LIMIT @Limit
                       FOR UPDATE SKIP LOCKED) picked
             WHERE m.id = picked.id
            RETURNING m.id, m.type, m.payload::text AS payload, m.attempts, m.occurred_at
            """,
            new { Now = now, LockedUntil = now.AddSeconds(leaseSeconds), Token = token, Limit = limit },
            cancellationToken: cancellationToken))).OrderBy(m => m.OccurredAt).ToList();
    }

    private async Task ProcessAsync(OutboxRow message, Guid token, int maxAttempts, CancellationToken cancellationToken)
    {
        // Sai da thread do laço: o handler começa sem segurar a próxima reserva.
        await Task.Yield();

        string? error = null;
        try
        {
            // Um escopo por mensagem: os handlers são scoped e agora rodam em paralelo.
            await using var scope = scopeFactory.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetServices<IOutboxMessageHandler>()
                .FirstOrDefault(h => string.Equals(h.Type, message.Type, StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"Nenhum handler para o tipo '{message.Type}'.");

            await handler.HandleAsync(message.Payload, cancellationToken);
        }
        // Timeout do HttpClient também é TaskCanceledException: só o token diz se o host está parando.
        // Sem isso, um provedor lento derrubaria o BackgroundService e, com ele, a API inteira.
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            error = ex.Message;
            LogMessageFailed(logger, message.Type, message.Id, message.Attempts + 1, ex);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // Host parando no meio: não marca nada; a reserva vence e a mensagem é processada na próxima subida.
            return;
        }

        try
        {
            await MarkAsync(message, token, error, maxAttempts, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Banco fora ao marcar: a reserva vence e a mensagem é processada de novo (o handler é idempotente).
            LogMarkFailed(logger, message.Type, message.Id, ex);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // Idem, parando.
        }
    }

    private async Task MarkAsync(OutboxRow message, Guid token, string? error, int maxAttempts, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var attempts = message.Attempts + 1;

        // Sucesso: apaga os segredos (tokens dos links) do payload; o resto fica para auditoria.
        const string Processed = """
            UPDATE messaging.outbox_messages
               SET processed_at = @Now, attempts = @Attempts, last_error = NULL, payload = payload - 'secrets',
                   locked_until = NULL, lock_token = NULL
             WHERE id = @Id AND lock_token = @Token
            """;
        const string Retry = """
            UPDATE messaging.outbox_messages
               SET attempts = @Attempts, last_error = @Error, next_attempt_at = @NextAttempt,
                   failed_at = CASE WHEN @Attempts >= @MaxAttempts THEN @Now END,
                   locked_until = NULL, lock_token = NULL
             WHERE id = @Id AND lock_token = @Token
            """;

        // Backoff: 30 s, 1 min, 2 min, 4 min...
        var nextAttempt = now.AddSeconds(30 * Math.Pow(2, attempts - 1));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var updated = await connection.ExecuteAsync(new CommandDefinition(
            error is null ? Processed : Retry,
            new { message.Id, Token = token, Now = now, Attempts = attempts, Error = Truncate(error), NextAttempt = nextAttempt, MaxAttempts = maxAttempts },
            cancellationToken: cancellationToken));

        if (updated == 0)
        {
            // A reserva venceu e outra rodada pegou a mensagem: o resultado dela é o que vale.
            LogLeaseLost(logger, message.Type, message.Id);
        }
    }

    private static string? Truncate(string? value) => value is { Length: > 2000 } ? value[..2000] : value;

    [LoggerMessage(Level = LogLevel.Error, Message = "Falha ao reservar mensagens do Outbox")]
    private static partial void LogBatchFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Falha na mensagem {Type} {MessageId} (tentativa {Attempt})")]
    private static partial void LogMessageFailed(ILogger logger, string type, Guid messageId, int attempt, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reserva vencida na mensagem {Type} {MessageId}: processada de novo por outra rodada")]
    private static partial void LogLeaseLost(ILogger logger, string type, Guid messageId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Falha ao marcar a mensagem {Type} {MessageId}; ela volta para a fila quando a reserva vencer")]
    private static partial void LogMarkFailed(ILogger logger, string type, Guid messageId, Exception exception);

    internal sealed class OutboxRow
    {
        public Guid Id { get; init; }
        public string Type { get; init; } = string.Empty;
        public string Payload { get; init; } = string.Empty;
        public int Attempts { get; init; }
        public DateTime OccurredAt { get; init; }
    }
}
