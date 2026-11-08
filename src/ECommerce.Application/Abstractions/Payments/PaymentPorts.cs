using ECommerce.Domain.Abstractions;
using ECommerce.Domain.Payments;

namespace ECommerce.Application.Abstractions.Payments;

/// <summary>Quem paga: a plataforma exige e-mail; o CPF vai só se a cliente tiver cadastrado (entrega 4 §3, P10).</summary>
public sealed record PaymentPayer(string Email, string FirstName, string LastName, string? Cpf);

/// <summary>
/// Pedido de cobrança. <see cref="IdempotencyKey"/> repete a mesma chave numa nova tentativa da cliente, então a
/// plataforma não cobra duas vezes nem se a primeira resposta se perdeu (entrega 4 §3, P8). <see cref="CardToken"/> só
/// existe no cartão, gerado pelo Brick no navegador (SAQ-A).
/// </summary>
public sealed record CreatePaymentRequest(
    string IdempotencyKey,
    Guid OrderId,
    string OrderCode,
    decimal Amount,
    PaymentMethod Method,
    PaymentPayer Payer,
    DateTime? ExpiresAtUtc,
    string? CardToken = null,
    string? PaymentMethodId = null,
    string? IssuerId = null,
    int? Installments = null);

/// <summary>Notificação já conferida: o id do evento (para deduplicar) e o id do pagamento na plataforma.</summary>
public sealed record PaymentNotification(string EventId, string ProviderPaymentId);

/// <summary>Plataforma de pagamento (arquivo 21 §4, ADR-010). O domínio não sabe qual é.</summary>
public interface IPaymentGateway
{
    /// <summary>Nome gravado em <c>payments.provider</c> e usado na rota do webhook.</summary>
    string Name { get; }

    /// <summary>Simulação de aprovação só existe no provedor falso, em desenvolvimento (entrega 4 §3, P3).</summary>
    bool CanSimulate { get; }

    Task<Result<PaymentSnapshot>> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken);

    Task<Result<PaymentSnapshot>> GetPaymentAsync(string providerPaymentId, CancellationToken cancellationToken);

    /// <summary>Cancela um PIX pendente (pedido cancelado). Falha não impede o cancelamento do pedido.</summary>
    Task<Result> CancelAsync(string providerPaymentId, CancellationToken cancellationToken);

    /// <summary>Estorno total; idempotente pela chave.</summary>
    Task<Result> RefundAsync(string providerPaymentId, Guid idempotencyKey, CancellationToken cancellationToken);

    /// <summary>Simula a aprovação (só no provedor falso).</summary>
    Task<Result> SimulateApprovalAsync(string providerPaymentId, CancellationToken cancellationToken);
}

/// <summary>
/// Resultado da conferência: assinatura inválida (401), notificação válida de outro assunto (200 e nada a fazer) ou
/// notificação de pagamento.
/// </summary>
public sealed record PaymentWebhookCheck(bool SignatureValid, PaymentNotification? Notification)
{
    public static readonly PaymentWebhookCheck Invalid = new(false, null);
    public static readonly PaymentWebhookCheck Ignored = new(true, null);
}

/// <summary>Confere a assinatura da notificação (entrega 4 §3, P7).</summary>
public interface IPaymentWebhookVerifier
{
    PaymentWebhookCheck Verify(IReadOnlyDictionary<string, string> headers, IReadOnlyDictionary<string, string> query, string body);
}
