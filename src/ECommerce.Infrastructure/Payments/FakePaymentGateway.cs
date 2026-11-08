using System.Collections.Concurrent;
using ECommerce.Application.Abstractions.Payments;
using ECommerce.Domain.Abstractions;
using ECommerce.Domain.Payments;

namespace ECommerce.Infrastructure.Payments;

/// <summary>
/// Plataforma de mentira para desenvolvimento e testes (entrega 4 §3, P3): guarda os pagamentos em memória, gera um
/// PIX que não paga nada e aprova pela simulação. No cartão, o token decide: <c>fake-approved</c> aprova na hora,
/// <c>fake-rejected</c> recusa, qualquer outro fica pendente. Nunca é registrado fora de Development e Testing.
/// </summary>
public sealed class FakePaymentGateway : IPaymentGateway
{
    public const string ApprovedCardToken = "fake-approved";
    public const string RejectedCardToken = "fake-rejected";

    /// <summary>PNG de 1×1 cinza: a tela tem o que mostrar no lugar do QR.</summary>
    private const string PlaceholderQrBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=";

    private readonly ConcurrentDictionary<string, PaymentSnapshot> _payments = new();
    private readonly ConcurrentDictionary<string, string> _byIdempotencyKey = new();
    private int _sequence;

    public string Name => PaymentSettings.FakeProvider;

    public bool CanSimulate => true;

    /// <summary>Faz a próxima chamada responder "plataforma fora" (testes).</summary>
    public int FailNext { get; set; }

    /// <summary>A plataforma recusa os estornos (testes do <c>RefundFailed</c>).</summary>
    public bool RejectRefunds { get; set; }

    public IReadOnlyCollection<string> Refunded => _refunded.Keys.ToList();

    private readonly ConcurrentDictionary<string, bool> _refunded = new();

    public Task<Result<PaymentSnapshot>> CreatePaymentAsync(CreatePaymentRequest request, CancellationToken cancellationToken)
    {
        if (TryFail(out var failure))
        {
            return Task.FromResult(Result.Failure<PaymentSnapshot>(failure));
        }

        if (_byIdempotencyKey.TryGetValue(request.IdempotencyKey, out var existingId))
        {
            return Task.FromResult(Result.Success(_payments[existingId]));
        }

        var id = $"fake-{Interlocked.Increment(ref _sequence)}-{Guid.NewGuid():N}"[..24];
        var status = request.Method == PaymentMethod.Pix
            ? PaymentStatus.Pending
            : request.CardToken switch
            {
                ApprovedCardToken => PaymentStatus.Approved,
                RejectedCardToken => PaymentStatus.Rejected,
                _ => PaymentStatus.Pending,
            };
        var snapshot = new PaymentSnapshot(
            id,
            status,
            status == PaymentStatus.Rejected ? "cc_rejected_insufficient_amount" : null,
            request.Amount,
            request.OrderId.ToString(),
            request.Method == PaymentMethod.Pix ? $"00020126580014BR.GOV.BCB.PIX0136{id}5204000053039865802BR5913MO ACESSORIOS6009SAO PAULO" : null,
            request.Method == PaymentMethod.Pix ? PlaceholderQrBase64 : null,
            request.ExpiresAtUtc,
            status == PaymentStatus.Approved ? DateTime.UtcNow : null);

        _payments[id] = snapshot;
        _byIdempotencyKey[request.IdempotencyKey] = id;
        return Task.FromResult(Result.Success(snapshot));
    }

    public Task<Result<PaymentSnapshot>> GetPaymentAsync(string providerPaymentId, CancellationToken cancellationToken)
    {
        if (TryFail(out var failure))
        {
            return Task.FromResult(Result.Failure<PaymentSnapshot>(failure));
        }

        return Task.FromResult(_payments.TryGetValue(providerPaymentId, out var snapshot)
            ? Result.Success(snapshot)
            : Result.Failure<PaymentSnapshot>(PaymentErrors.PaymentNotFound));
    }

    public Task<Result> CancelAsync(string providerPaymentId, CancellationToken cancellationToken)
    {
        if (_payments.TryGetValue(providerPaymentId, out var snapshot) && snapshot.Status == PaymentStatus.Pending)
        {
            _payments[providerPaymentId] = snapshot with { Status = PaymentStatus.Cancelled };
        }

        return Task.FromResult(Result.Success());
    }

    public Task<Result> RefundAsync(string providerPaymentId, Guid idempotencyKey, CancellationToken cancellationToken)
    {
        if (TryFail(out var failure))
        {
            return Task.FromResult(Result.Failure(failure));
        }

        if (RejectRefunds)
        {
            return Task.FromResult(Result.Failure(PaymentErrors.ProviderRejected));
        }

        if (_payments.TryGetValue(providerPaymentId, out var snapshot))
        {
            _payments[providerPaymentId] = snapshot with { Status = PaymentStatus.Refunded };
        }

        _refunded[providerPaymentId] = true;
        return Task.FromResult(Result.Success());
    }

    public Task<Result> SimulateApprovalAsync(string providerPaymentId, CancellationToken cancellationToken)
    {
        if (!_payments.TryGetValue(providerPaymentId, out var snapshot))
        {
            return Task.FromResult(Result.Failure(PaymentErrors.PaymentNotFound));
        }

        _payments[providerPaymentId] = snapshot with { Status = PaymentStatus.Approved, ApprovedAtUtc = DateTime.UtcNow };
        return Task.FromResult(Result.Success());
    }

    public void Reset()
    {
        _payments.Clear();
        _byIdempotencyKey.Clear();
        _refunded.Clear();
        FailNext = 0;
        RejectRefunds = false;
    }

    private bool TryFail(out Error error)
    {
        error = PaymentErrors.ProviderUnavailable;
        if (FailNext <= 0)
        {
            return false;
        }

        FailNext--;
        return true;
    }
}

/// <summary>O provedor falso não recebe notificação: a simulação dispara a mesma sincronização.</summary>
internal sealed class RejectingWebhookVerifier : IPaymentWebhookVerifier
{
    public PaymentWebhookCheck Verify(IReadOnlyDictionary<string, string> headers, IReadOnlyDictionary<string, string> query, string body) =>
        PaymentWebhookCheck.Invalid;
}
