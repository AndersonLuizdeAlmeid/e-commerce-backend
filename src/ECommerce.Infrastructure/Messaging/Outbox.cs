using System.Text.Json;
using Dapper;
using ECommerce.Application.Abstractions.Data;
using ECommerce.Application.Abstractions.Outbox;

namespace ECommerce.Infrastructure.Messaging;

/// <summary>Grava no Outbox com a conexão e a transação do caso de uso (arquivo 06 §8, arquivo 25 §1).</summary>
internal sealed class OutboxWriter(IUnitOfWork unitOfWork, TimeProvider time) : IOutbox
{
    internal static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public Task EnqueueAsync<TPayload>(string type, TPayload payload, CancellationToken cancellationToken)
        where TPayload : notnull
    {
        var now = time.GetUtcNow().UtcDateTime;
        return unitOfWork.Connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO messaging.outbox_messages (id, type, payload, occurred_at, attempts, next_attempt_at)
            VALUES (@Id, @Type, @Payload::jsonb, @Now, 0, @Now)
            """,
            new { Id = Guid.CreateVersion7(), Type = type, Payload = JsonSerializer.Serialize(payload, SerializerOptions), Now = now },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));
    }
}

/// <summary>Processa um tipo de mensagem do Outbox. Precisa ser idempotente: pode rodar mais de uma vez (arquivo 25 §1).</summary>
public interface IOutboxMessageHandler
{
    string Type { get; }

    Task HandleAsync(string payloadJson, CancellationToken cancellationToken);
}

/// <summary>Seção <c>Outbox</c>.</summary>
public sealed class OutboxSettings
{
    public const string SectionName = "Outbox";

    public int PollingIntervalMilliseconds { get; init; } = 5000;
    public int BatchSize { get; init; } = 20;

    /// <summary>Depois disso a mensagem fica com <c>failed_at</c> e vira alerta (arquivo 23 §4).</summary>
    public int MaxAttempts { get; init; } = 5;
}
