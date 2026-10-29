namespace ECommerce.Application.Abstractions.Outbox;

/// <summary>
/// Grava uma mensagem no Outbox na mesma transação do caso de uso (arquivo 25 §1): só sai se o commit acontecer.
/// </summary>
public interface IOutbox
{
    Task EnqueueAsync<TPayload>(string type, TPayload payload, CancellationToken cancellationToken)
        where TPayload : notnull;
}
