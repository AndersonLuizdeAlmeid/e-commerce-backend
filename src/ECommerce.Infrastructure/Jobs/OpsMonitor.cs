using System.Globalization;
using System.Text.Json;
using Dapper;
using ECommerce.Application.Notifications;
using ECommerce.Infrastructure.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace ECommerce.Infrastructure.Jobs;

/// <summary>Seção <c>Ops</c> (entrega 6 §3, P3 e P4).</summary>
public sealed class OpsSettings
{
    public const string SectionName = "Ops";

    /// <summary>Quem recebe o EM-14. Vazio: o alerta só vai para o log (desenvolvimento). Obrigatório em Production.</summary>
    public string AlertEmail { get; init; } = string.Empty;

    /// <summary>Intervalo mínimo entre dois alertas da mesma condição.</summary>
    public int AlertIntervalHours { get; init; } = 6;

    /// <summary>Mensagem pendente há mais que isso conta como fila atrasada (arquivo 25 §3).</summary>
    public int OutboxDelayMinutes { get; init; } = 15;

    /// <summary>Liga o EM-11 e o vigia. Os testes funcionais desligam e chamam cada um quando precisam.</summary>
    public bool ScheduledJobsEnabled { get; init; } = true;
}

/// <summary>
/// Vigia de operação (entrega 6 §3, P3; arquivo 14 §6; arquivo 25 §3). Confere sinais que só o banco enxerga e manda o
/// EM-14 <b>direto pelo provedor</b>, sem Outbox: um alerta de Outbox quebrado não pode depender do Outbox. Cada condição
/// alerta no máximo uma vez a cada <see cref="OpsSettings.AlertIntervalHours"/> horas; a marca fica em
/// <c>ops.job_marks</c> e o advisory lock impede duas instâncias de avisarem a mesma coisa.
/// </summary>
internal sealed partial class OpsMonitor(
    NpgsqlDataSource dataSource,
    IEmailSender sender,
    EmailTemplateRenderer renderer,
    IOptions<OpsSettings> options,
    TimeProvider time,
    ILogger<OpsMonitor> logger)
{
    private const int MaxLines = 5;
    private const string LockKey = "ops-monitor";

    /// <param name="Accumulates">
    /// <c>true</c>: conta só o que aconteceu desde o último alerta (falha, frete de contingência). <c>false</c>: é um estado
    /// que vale agora (fila atrasada) e se repete enquanto durar.
    /// </param>
    /// <param name="Lag">Só considera o que aconteceu antes de agora menos isto (dá tempo de a notificação chegar).</param>
    internal sealed record Condition(string Key, string Title, string Summary, string Sql, bool Accumulates, TimeSpan Lag);

    internal static readonly IReadOnlyList<Condition> Conditions =
    [
        new(
            "outbox-failed",
            "mensagens que desistiram",
            "{0} mensagem(ns) do Outbox desistiram depois de todas as tentativas. E-mail, estorno ou revalidação não aconteceram.",
            """
            SELECT count(*) FROM messaging.outbox_messages WHERE failed_at > @Since AND failed_at <= @Until;
            SELECT type || ' em ' || to_char(failed_at AT TIME ZONE 'America/Sao_Paulo', 'DD/MM HH24:MI') || ': ' || coalesce(left(last_error, 200), 'sem detalhe')
              FROM messaging.outbox_messages WHERE failed_at > @Since AND failed_at <= @Until
             ORDER BY failed_at DESC LIMIT @MaxLines;
            """,
            Accumulates: true,
            Lag: TimeSpan.Zero),
        new(
            "outbox-delayed",
            "fila de mensagens atrasada",
            "{0} mensagem(ns) do Outbox esperam há mais tempo que o normal. E-mails e estornos estão atrasando.",
            """
            SELECT count(*) FROM messaging.outbox_messages
             WHERE processed_at IS NULL AND failed_at IS NULL AND occurred_at < @DelayCutoff;
            SELECT type || ' desde ' || to_char(occurred_at AT TIME ZONE 'America/Sao_Paulo', 'DD/MM HH24:MI') || ', '
                   || attempts || ' tentativa(s)' || coalesce(': ' || left(last_error, 200), '')
              FROM messaging.outbox_messages
             WHERE processed_at IS NULL AND failed_at IS NULL AND occurred_at < @DelayCutoff
             ORDER BY occurred_at LIMIT @MaxLines;
            """,
            Accumulates: false,
            Lag: TimeSpan.Zero),
        new(
            "pix-without-webhook",
            "PIX aprovado sem notificação",
            "{0} PIX foram aprovados sem nenhuma notificação do Mercado Pago: quem confirmou foi a reconciliação. Confira a URL e a assinatura do webhook.",
            """
            SELECT count(*) FROM payments.payments p
             WHERE p.method = 'Pix' AND p.provider = 'MercadoPago' AND p.approved_at > @Since AND p.approved_at <= @Until
               AND NOT EXISTS (SELECT 1 FROM payments.payment_events e
                                WHERE e.provider = p.provider AND e.provider_payment_id = p.provider_payment_id);
            SELECT 'PED-' || lpad(o.number::text, 6, '0') || ': aprovado em '
                   || to_char(p.approved_at AT TIME ZONE 'America/Sao_Paulo', 'DD/MM HH24:MI')
              FROM payments.payments p JOIN ordering.orders o ON o.id = p.order_id
             WHERE p.method = 'Pix' AND p.provider = 'MercadoPago' AND p.approved_at > @Since AND p.approved_at <= @Until
               AND NOT EXISTS (SELECT 1 FROM payments.payment_events e
                                WHERE e.provider = p.provider AND e.provider_payment_id = p.provider_payment_id)
             ORDER BY p.approved_at DESC LIMIT @MaxLines;
            """,
            Accumulates: true,
            Lag: TimeSpan.FromMinutes(10)),
        new(
            "shipping-fallback",
            "frete de contingência em uso",
            "{0} pedido(s) usaram o frete de contingência: a cotação do Melhor Envio falhou no checkout.",
            """
            SELECT count(*) FROM ordering.orders WHERE shipping_is_fallback AND placed_at > @Since AND placed_at <= @Until;
            SELECT 'PED-' || lpad(number::text, 6, '0') || ' em ' || to_char(placed_at AT TIME ZONE 'America/Sao_Paulo', 'DD/MM HH24:MI')
                   || ': ' || shipping_service_name
              FROM ordering.orders WHERE shipping_is_fallback AND placed_at > @Since AND placed_at <= @Until
             ORDER BY placed_at DESC LIMIT @MaxLines;
            """,
            Accumulates: true,
            Lag: TimeSpan.Zero),
    ];

    /// <summary>Confere todas as condições e devolve as chaves que alertaram nesta rodada.</summary>
    public async Task<IReadOnlyList<string>> CheckAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var now = time.GetUtcNow().UtcDateTime;
        var interval = TimeSpan.FromHours(settings.AlertIntervalHours);
        var alerted = new List<string>();

        // Advisory lock de sessão, não de transação: o envio do e-mail acontece fora de qualquer transação.
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        if (!await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT pg_try_advisory_lock(hashtext(@LockKey))", new { LockKey }, cancellationToken: cancellationToken)))
        {
            return alerted;
        }

        try
        {
            var marks = (await connection.QueryAsync<(string Key, DateTime LastAt)>(new CommandDefinition(
                    "SELECT key, last_at FROM ops.job_marks WHERE key LIKE 'alert:%'", cancellationToken: cancellationToken)))
                .ToDictionary(m => m.Key, m => DateTime.SpecifyKind(m.LastAt, DateTimeKind.Utc), StringComparer.Ordinal);

            foreach (var condition in Conditions)
            {
                var markKey = $"alert:{condition.Key}";
                DateTime? lastAt = marks.TryGetValue(markKey, out var mark) ? mark : null;
                if (lastAt is { } last && now - last < interval)
                {
                    continue;
                }

                // Desde o último alerta (ou 24 h na primeira vez), menos o atraso da condição; o próximo alerta continua
                // exatamente de onde este parou, sem repetir nem pular nada.
                var since = (lastAt ?? now.AddHours(-24)) - condition.Lag;
                var until = now - condition.Lag;
                using var grid = await connection.QueryMultipleAsync(new CommandDefinition(
                    condition.Sql,
                    new { Since = since, Until = until, DelayCutoff = now.AddMinutes(-settings.OutboxDelayMinutes), MaxLines },
                    cancellationToken: cancellationToken));
                var count = await grid.ReadSingleAsync<int>();
                var lines = (await grid.ReadAsync<string>()).ToList();
                if (count == 0)
                {
                    continue;
                }

                try
                {
                    await AlertAsync(condition, count, lines, cancellationToken);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    // Sem marca: a próxima rodada (5 min) tenta de novo. As outras condições seguem.
                    LogAlertFailed(logger, condition.Key, ex);
                    continue;
                }

                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO ops.job_marks (key, last_at) VALUES (@Key, @Now)
                    ON CONFLICT (key) DO UPDATE SET last_at = EXCLUDED.last_at
                    """,
                    new { Key = markKey, Now = now },
                    cancellationToken: cancellationToken));
                alerted.Add(condition.Key);
            }
        }
        finally
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "SELECT pg_advisory_unlock(hashtext(@LockKey))", new { LockKey }, cancellationToken: CancellationToken.None));
        }

        return alerted;
    }

    private async Task AlertAsync(Condition condition, int count, List<string> lines, CancellationToken cancellationToken)
    {
        var summary = string.Format(CultureInfo.GetCultureInfo("pt-BR"), condition.Summary, count);

        // Sempre no log: é o que o Grafana Cloud enxerga (P3b), mesmo se o e-mail falhar.
        LogAlert(logger, condition.Key, count, summary);

        var to = options.Value.AlertEmail;
        if (string.IsNullOrWhiteSpace(to))
        {
            return;
        }

        var shown = count > lines.Count
            ? [.. lines, string.Create(CultureInfo.InvariantCulture, $"…e mais {count - lines.Count}.")]
            : lines;
        var message = renderer.Render(new SendEmail(
            EmailTemplates.OperationalAlert,
            to,
            null,
            new Dictionary<string, string>
            {
                ["title"] = condition.Title,
                ["summary"] = summary,
                ["lines"] = JsonSerializer.Serialize(shown),
            },
            new Dictionary<string, string>()));

        await sender.SendAsync(message, cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Alerta de operação {AlertKey} ({Count}): {Summary}")]
    private static partial void LogAlert(ILogger logger, string alertKey, int count, string summary);

    [LoggerMessage(Level = LogLevel.Error, Message = "Falha ao enviar o alerta de operação {AlertKey}")]
    private static partial void LogAlertFailed(ILogger logger, string alertKey, Exception exception);
}
