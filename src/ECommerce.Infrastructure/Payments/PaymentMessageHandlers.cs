using System.Text.Json;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Application.Abstractions.Payments;
using ECommerce.Application.Payments;
using ECommerce.Infrastructure.Messaging;
using Microsoft.Extensions.Logging;

namespace ECommerce.Infrastructure.Payments;

/// <summary>
/// <see cref="SyncPaymentMessage"/>: consulta a plataforma e aplica, numa transação própria. Falha (plataforma fora, ou
/// a notificação chegou antes do commit de quem criou o pagamento) volta para o Outbox, que tenta de novo com espera.
/// </summary>
internal sealed class SyncPaymentHandler(ICommandHandler<SyncPaymentCommand> sync) : IOutboxMessageHandler
{
    public string Type => SyncPaymentMessage.Type;

    public async Task HandleAsync(string payloadJson, CancellationToken cancellationToken)
    {
        var message = JsonSerializer.Deserialize<SyncPaymentMessage>(payloadJson, OutboxWriter.SerializerOptions)
            ?? throw new InvalidOperationException("Payload de sincronização vazio.");
        var result = await sync.Handle(new SyncPaymentCommand(message.Provider, message.ProviderPaymentId), cancellationToken);
        if (result.IsFailure)
        {
            throw new InvalidOperationException($"Sincronização do pagamento {message.ProviderPaymentId} falhou: {result.Error.Code}");
        }
    }
}

/// <summary><see cref="CancelProviderPaymentMessage"/>: cancela o PIX de um pedido cancelado. É cortesia: se falhar, o PIX vence sozinho.</summary>
internal sealed partial class CancelProviderPaymentHandler(IPaymentGateway gateway, ILogger<CancelProviderPaymentHandler> logger) : IOutboxMessageHandler
{
    public string Type => CancelProviderPaymentMessage.Type;

    public async Task HandleAsync(string payloadJson, CancellationToken cancellationToken)
    {
        var message = JsonSerializer.Deserialize<CancelProviderPaymentMessage>(payloadJson, OutboxWriter.SerializerOptions)
            ?? throw new InvalidOperationException("Payload de cancelamento vazio.");
        if (!string.Equals(message.Provider, gateway.Name, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var result = await gateway.CancelAsync(message.ProviderPaymentId, cancellationToken);
        if (result.IsFailure)
        {
            LogCancelFailed(logger, message.ProviderPaymentId, result.Error.Code);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "PIX {PaymentId} não foi cancelado na plataforma ({Code}); ele vence sozinho")]
    private static partial void LogCancelFailed(ILogger logger, string paymentId, string code);
}
