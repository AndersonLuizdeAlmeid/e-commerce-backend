using Dapper;
using ECommerce.Application.Abstractions.Data;
using ECommerce.Domain.Payments;

namespace ECommerce.Infrastructure.Persistence.Repositories;

internal sealed class PaymentRepository(IUnitOfWork unitOfWork) : IPaymentRepository
{
    private const string Columns = """
        id, order_id, provider, provider_payment_id, method, status, status_detail, amount, installments,
        pix_qr_code, pix_qr_code_base64, expires_at, approved_at, created_at, updated_at, version
        """;

    public Task AddAsync(Payment payment, string? idempotencyKey, CancellationToken cancellationToken) =>
        unitOfWork.Connection.ExecuteAsync(Command(
            """
            INSERT INTO payments.payments (
                id, order_id, provider, provider_payment_id, method, status, status_detail, amount, installments,
                pix_qr_code, pix_qr_code_base64, expires_at, approved_at, idempotency_key, created_at, updated_at)
            VALUES (
                @Id, @OrderId, @Provider, @ProviderPaymentId, @Method, @Status, @StatusDetail, @Amount, @Installments,
                @PixQrCode, @PixQrCodeBase64, @ExpiresAt, @ApprovedAt, @IdempotencyKey, @CreatedAt, @UpdatedAt)
            """,
            new
            {
                payment.Id,
                payment.OrderId,
                payment.Provider,
                payment.ProviderPaymentId,
                Method = payment.Method.ToString(),
                Status = payment.Status.ToString(),
                payment.StatusDetail,
                payment.Amount,
                Installments = (short?)payment.Installments,
                payment.PixQrCode,
                payment.PixQrCodeBase64,
                ExpiresAt = payment.ExpiresAtUtc,
                ApprovedAt = payment.ApprovedAtUtc,
                IdempotencyKey = idempotencyKey,
                CreatedAt = payment.CreatedAtUtc,
                UpdatedAt = payment.UpdatedAtUtc,
            },
            cancellationToken));

    public Task<Payment?> FindByIdempotencyKeyAsync(Guid orderId, string idempotencyKey, CancellationToken cancellationToken) =>
        SingleAsync($"SELECT {Columns} FROM payments.payments WHERE order_id = @OrderId AND idempotency_key = @Key", new { OrderId = orderId, Key = idempotencyKey }, cancellationToken);

    public Task<Payment?> FindOpenPixAsync(Guid orderId, DateTime nowUtc, CancellationToken cancellationToken) =>
        SingleAsync(
            $"""
            SELECT {Columns} FROM payments.payments
             WHERE order_id = @OrderId AND method = 'Pix' AND status = 'Pending' AND provider_payment_id IS NOT NULL
               AND (expires_at IS NULL OR expires_at > @Now)
             ORDER BY created_at DESC LIMIT 1
            """,
            new { OrderId = orderId, Now = nowUtc },
            cancellationToken);

    public Task<Payment?> GetByProviderIdForUpdateAsync(string provider, string providerPaymentId, CancellationToken cancellationToken) =>
        SingleAsync(
            $"SELECT {Columns} FROM payments.payments WHERE provider = @Provider AND provider_payment_id = @ProviderPaymentId FOR UPDATE",
            new { Provider = provider, ProviderPaymentId = providerPaymentId },
            cancellationToken);

    public Task<Payment?> GetForUpdateAsync(Guid paymentId, CancellationToken cancellationToken) =>
        SingleAsync($"SELECT {Columns} FROM payments.payments WHERE id = @Id FOR UPDATE", new { Id = paymentId }, cancellationToken);

    public Task<bool> HasApprovedAsync(Guid orderId, Guid exceptPaymentId, CancellationToken cancellationToken) =>
        unitOfWork.Connection.ExecuteScalarAsync<bool>(Command(
            "SELECT EXISTS (SELECT 1 FROM payments.payments WHERE order_id = @OrderId AND id <> @Except AND status = 'Approved')",
            new { OrderId = orderId, Except = exceptPaymentId },
            cancellationToken));

    public Task UpdateAsync(Payment payment, CancellationToken cancellationToken) =>
        unitOfWork.Connection.ExecuteAsync(Command(
            """
            UPDATE payments.payments
               SET provider_payment_id = @ProviderPaymentId, status = @Status, status_detail = @StatusDetail,
                   pix_qr_code = @PixQrCode, pix_qr_code_base64 = @PixQrCodeBase64, expires_at = @ExpiresAt,
                   approved_at = @ApprovedAt, updated_at = @UpdatedAt, version = version + 1
             WHERE id = @Id
            """,
            new
            {
                payment.Id,
                payment.ProviderPaymentId,
                Status = payment.Status.ToString(),
                payment.StatusDetail,
                payment.PixQrCode,
                payment.PixQrCodeBase64,
                ExpiresAt = payment.ExpiresAtUtc,
                ApprovedAt = payment.ApprovedAtUtc,
                UpdatedAt = payment.UpdatedAtUtc,
            },
            cancellationToken));

    public async Task<bool> RecordEventAsync(
        string provider, string eventId, string providerPaymentId, string payloadJson, DateTime nowUtc, CancellationToken cancellationToken) =>
        await unitOfWork.Connection.ExecuteAsync(Command(
            """
            INSERT INTO payments.payment_events (provider, provider_event_id, provider_payment_id, payload, received_at)
            VALUES (@Provider, @EventId, @ProviderPaymentId, @Payload::jsonb, @Now)
            ON CONFLICT DO NOTHING
            """,
            new { Provider = provider, EventId = eventId, ProviderPaymentId = providerPaymentId, Payload = payloadJson, Now = nowUtc },
            cancellationToken)) == 1;

    public async Task<IReadOnlyList<(string Provider, string ProviderPaymentId)>> ListStalePendingAsync(
        DateTime olderThanUtc, int limit, CancellationToken cancellationToken)
    {
        var rows = await unitOfWork.Connection.QueryAsync<(string Provider, string ProviderPaymentId)>(Command(
            """
            SELECT provider, provider_payment_id FROM payments.payments
             WHERE status = 'Pending' AND provider_payment_id IS NOT NULL AND updated_at < @OlderThan
             ORDER BY updated_at LIMIT @Limit
            """,
            new { OlderThan = olderThanUtc, Limit = limit },
            cancellationToken));
        return rows.ToList();
    }

    private async Task<Payment?> SingleAsync(string sql, object parameters, CancellationToken cancellationToken)
    {
        var row = await unitOfWork.Connection.QuerySingleOrDefaultAsync<Row>(Command(sql, parameters, cancellationToken));
        return row is null
            ? null
            : Payment.Rehydrate(
                row.Id,
                row.OrderId,
                row.Provider,
                row.ProviderPaymentId,
                Enum.Parse<PaymentMethod>(row.Method),
                Enum.Parse<PaymentStatus>(row.Status),
                row.StatusDetail,
                row.Amount,
                row.Installments,
                row.PixQrCode,
                row.PixQrCodeBase64,
                row.ExpiresAt,
                row.ApprovedAt,
                row.CreatedAt,
                row.UpdatedAt,
                row.Version);
    }

    private CommandDefinition Command(string sql, object parameters, CancellationToken cancellationToken) =>
        new(sql, parameters, unitOfWork.Transaction, cancellationToken: cancellationToken);

    private sealed class Row
    {
        public Guid Id { get; init; }
        public Guid OrderId { get; init; }
        public string Provider { get; init; } = string.Empty;
        public string? ProviderPaymentId { get; init; }
        public string Method { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string? StatusDetail { get; init; }
        public decimal Amount { get; init; }
        public short? Installments { get; init; }
        public string? PixQrCode { get; init; }
        public string? PixQrCodeBase64 { get; init; }
        public DateTime? ExpiresAt { get; init; }
        public DateTime? ApprovedAt { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime UpdatedAt { get; init; }
        public int Version { get; init; }
    }
}
