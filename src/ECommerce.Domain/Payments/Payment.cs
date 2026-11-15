using ECommerce.Domain.Abstractions;

namespace ECommerce.Domain.Payments;

public static class PaymentErrors
{
    public static readonly Error OrderNotPayable =
        Error.BusinessRule("Payments.OrderNotPayable", "Este pedido não está mais aguardando pagamento.");

    public static readonly Error PaymentNotFound = Error.NotFound("Payments.PaymentNotFound", "Pagamento não encontrado.");

    public static readonly Error ProviderUnavailable = Error.Unavailable(
        "Payments.ProviderUnavailable", "Não foi possível falar com o Mercado Pago agora. Tente de novo em instantes.");

    public static readonly Error ProviderRejected =
        Error.BusinessRule("Payments.ProviderRejected", "O Mercado Pago recusou o pagamento. Confira os dados ou tente outro meio.");

    public static readonly Error InvalidSignature = Error.Unauthorized("Payments.InvalidSignature", "Assinatura inválida.");

    public static readonly Error MethodNotAvailable =
        Error.Validation("Payments.MethodNotAvailable", "Escolha PIX ou cartão de crédito.");

    public static readonly Error NothingToRefund =
        Error.BusinessRule("Payments.NothingToRefund", "Este pagamento não está esperando estorno.");

    public static readonly Error ManualOnly = Error.BusinessRule(
        "Payments.ManualOnly", "Só o pagamento confirmado à mão é marcado como devolvido pela loja. Os outros são conferidos no Mercado Pago.");

    public static readonly Error NotOnProvider =
        Error.BusinessRule("Payments.NotOnProvider", "Este pagamento foi confirmado à mão e não existe no Mercado Pago.");

    public static readonly Error SimulationNotAvailable =
        Error.NotFound("Payments.SimulationNotAvailable", "A simulação só existe no ambiente de desenvolvimento.");
}

public enum PaymentMethod
{
    Pix,
    CreditCard,

    /// <summary>Confirmado à mão pela loja, fora da plataforma (RF-053 CA1, entrega 5 §3 P1).</summary>
    Manual,
}

public enum PaymentStatus
{
    Pending,
    Approved,
    Rejected,
    Expired,
    Cancelled,
    Refunded,
    RefundFailed,
}

/// <summary>
/// Situação do pagamento na plataforma, já traduzida para os nossos status (entrega 4 §3, P6). <see cref="Disputed"/>:
/// contestação ou chargeback, que não mudam o pedido e só avisam a loja.
/// </summary>
public sealed record PaymentSnapshot(
    string ProviderPaymentId,
    PaymentStatus Status,
    string? StatusDetail,
    decimal Amount,
    string? ExternalReference,
    string? PixQrCode,
    string? PixQrCodeBase64,
    DateTime? ExpiresAtUtc,
    DateTime? ApprovedAtUtc,
    bool Disputed = false);

/// <summary>
/// Uma tentativa de pagamento (arquivo 21 §4). O valor é sempre o total do pedido (RN-100); a situação só muda pelo
/// que a plataforma diz, nunca pelo retorno do navegador (§2).
/// </summary>
public sealed class Payment
{
    private Payment(
        Guid id,
        Guid orderId,
        string provider,
        string? providerPaymentId,
        PaymentMethod method,
        PaymentStatus status,
        string? statusDetail,
        decimal amount,
        int? installments,
        string? pixQrCode,
        string? pixQrCodeBase64,
        DateTime? expiresAtUtc,
        DateTime? approvedAtUtc,
        DateTime createdAtUtc,
        DateTime updatedAtUtc,
        int version)
    {
        Id = id;
        OrderId = orderId;
        Provider = provider;
        ProviderPaymentId = providerPaymentId;
        Method = method;
        Status = status;
        StatusDetail = statusDetail;
        Amount = amount;
        Installments = installments;
        PixQrCode = pixQrCode;
        PixQrCodeBase64 = pixQrCodeBase64;
        ExpiresAtUtc = expiresAtUtc;
        ApprovedAtUtc = approvedAtUtc;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;
        Version = version;
    }

    public Guid Id { get; }

    public Guid OrderId { get; }

    public string Provider { get; }

    public string? ProviderPaymentId { get; private set; }

    public PaymentMethod Method { get; }

    public PaymentStatus Status { get; private set; }

    /// <summary>Motivo da plataforma (ex.: <c>cc_rejected_insufficient_amount</c>), para a mensagem de recusa.</summary>
    public string? StatusDetail { get; private set; }

    public decimal Amount { get; }

    public int? Installments { get; }

    public string? PixQrCode { get; private set; }

    public string? PixQrCodeBase64 { get; private set; }

    public DateTime? ExpiresAtUtc { get; private set; }

    public DateTime? ApprovedAtUtc { get; private set; }

    public DateTime CreatedAtUtc { get; }

    public DateTime UpdatedAtUtc { get; private set; }

    public int Version { get; }

    public bool IsFinal => Status is not PaymentStatus.Pending;

    /// <summary>Provedor do pagamento confirmado à mão: não existe na plataforma, não tem id externo nem estorno automático.</summary>
    public const string ManualProvider = "manual";

    public bool IsManual => Provider == ManualProvider;

    /// <summary>
    /// Pagamento conciliado fora do sistema (RF-053 CA1): nasce aprovado com o valor do pedido e o motivo, para o pedido,
    /// o dashboard e a regra de um aprovado por pedido enxergarem o mesmo dinheiro (entrega 5 §3, P1).
    /// </summary>
    public static Payment RecordManual(Guid orderId, decimal amount, string reason, DateTime nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new Payment(
            Guid.CreateVersion7(), orderId, ManualProvider, null, PaymentMethod.Manual, PaymentStatus.Approved, reason.Trim(), amount, null,
            null, null, null, nowUtc, nowUtc, nowUtc, 0);
    }

    /// <summary>
    /// A loja devolveu à mão o pagamento confirmado à mão (entrega 5 §3, P2). Só para <see cref="ManualProvider"/>: o
    /// que passou pela plataforma só vira estornado quando ela confirma.
    /// </summary>
    public bool MarkRefundedManually(string note, DateTime nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(note);
        if (!IsManual || Status != PaymentStatus.Approved)
        {
            return false;
        }

        Status = PaymentStatus.Refunded;
        StatusDetail = $"{StatusDetail} · Devolvido: {note.Trim()}";
        UpdatedAtUtc = nowUtc;
        return true;
    }

    public static Payment Start(Guid orderId, string provider, PaymentMethod method, decimal amount, int? installments, DateTime? expiresAtUtc, DateTime nowUtc) =>
        new(Guid.CreateVersion7(), orderId, provider, null, method, PaymentStatus.Pending, null, amount, installments, null, null, expiresAtUtc, null, nowUtc, nowUtc, 0);

    /// <summary>Uso exclusivo da Infrastructure.</summary>
    public static Payment Rehydrate(
        Guid id,
        Guid orderId,
        string provider,
        string? providerPaymentId,
        PaymentMethod method,
        PaymentStatus status,
        string? statusDetail,
        decimal amount,
        int? installments,
        string? pixQrCode,
        string? pixQrCodeBase64,
        DateTime? expiresAtUtc,
        DateTime? approvedAtUtc,
        DateTime createdAtUtc,
        DateTime updatedAtUtc,
        int version) =>
        new(id, orderId, provider, providerPaymentId, method, status, statusDetail, amount, installments, pixQrCode, pixQrCodeBase64,
            expiresAtUtc, approvedAtUtc, createdAtUtc, updatedAtUtc, version);

    /// <summary>
    /// Aplica o que a plataforma respondeu. Devolve falso quando nada muda (notificação repetida ou fora de ordem): um
    /// pagamento que já saiu de pendente só anda para estorno, nunca volta (arquivo 21 §8).
    /// </summary>
    public bool Apply(PaymentSnapshot snapshot, DateTime nowUtc)
    {
        ProviderPaymentId ??= snapshot.ProviderPaymentId;
        PixQrCode ??= snapshot.PixQrCode;
        PixQrCodeBase64 ??= snapshot.PixQrCodeBase64;
        ExpiresAtUtc = snapshot.ExpiresAtUtc ?? ExpiresAtUtc;

        var allowed = (Status, snapshot.Status) switch
        {
            (PaymentStatus.Pending, not PaymentStatus.Pending) => true,
            (PaymentStatus.Approved, PaymentStatus.Refunded) => true,
            (PaymentStatus.RefundFailed, PaymentStatus.Refunded) => true,
            _ => false,
        };

        if (!allowed)
        {
            UpdatedAtUtc = nowUtc;
            return false;
        }

        Status = snapshot.Status;
        StatusDetail = snapshot.StatusDetail;
        if (snapshot.Status == PaymentStatus.Approved)
        {
            ApprovedAtUtc = snapshot.ApprovedAtUtc ?? nowUtc;
        }

        UpdatedAtUtc = nowUtc;
        return true;
    }

    /// <summary>
    /// Estorno que a plataforma recusou: fica para a loja tratar à mão (arquivo 21 §5.4). Vale também para o pagamento
    /// que a plataforma aprovou e nós ainda víamos como pendente (RN-101, RN-102).
    /// </summary>
    public bool MarkRefundFailed(DateTime nowUtc)
    {
        if (Status is not (PaymentStatus.Approved or PaymentStatus.Pending))
        {
            return false;
        }

        Status = PaymentStatus.RefundFailed;
        UpdatedAtUtc = nowUtc;
        return true;
    }

    /// <summary>A plataforma aceitou o estorno.</summary>
    public bool MarkRefunded(DateTime nowUtc)
    {
        if (Status is not (PaymentStatus.Approved or PaymentStatus.RefundFailed or PaymentStatus.Pending))
        {
            return false;
        }

        Status = PaymentStatus.Refunded;
        UpdatedAtUtc = nowUtc;
        return true;
    }
}

public interface IPaymentRepository
{
    Task AddAsync(Payment payment, string? idempotencyKey, CancellationToken cancellationToken);

    Task<Payment?> FindByIdempotencyKeyAsync(Guid orderId, string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>Pagamento PIX ainda pendente e no prazo: pedir PIX de novo devolve o mesmo QR (entrega 4 §3, P5).</summary>
    Task<Payment?> FindOpenPixAsync(Guid orderId, DateTime nowUtc, CancellationToken cancellationToken);

    Task<Payment?> GetByProviderIdForUpdateAsync(string provider, string providerPaymentId, CancellationToken cancellationToken);

    Task<Payment?> GetForUpdateAsync(Guid paymentId, CancellationToken cancellationToken);

    Task<bool> HasApprovedAsync(Guid orderId, Guid exceptPaymentId, CancellationToken cancellationToken);

    Task UpdateAsync(Payment payment, CancellationToken cancellationToken);

    /// <summary>Grava a notificação; falso se o mesmo evento já tinha chegado.</summary>
    Task<bool> RecordEventAsync(string provider, string eventId, string providerPaymentId, string payloadJson, DateTime nowUtc, CancellationToken cancellationToken);

    /// <summary>Pendentes há mais de <paramref name="olderThan"/> com id na plataforma, para a reconciliação.</summary>
    Task<IReadOnlyList<(string Provider, string ProviderPaymentId)>> ListStalePendingAsync(DateTime olderThanUtc, int limit, CancellationToken cancellationToken);
}
