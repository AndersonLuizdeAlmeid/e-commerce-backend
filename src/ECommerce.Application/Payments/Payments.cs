using Dapper;
using ECommerce.Application.Abstractions.Data;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Application.Abstractions.Outbox;
using ECommerce.Application.Abstractions.Payments;
using ECommerce.Application.Notifications;
using ECommerce.Application.Ordering;
using ECommerce.Domain.Abstractions;
using ECommerce.Domain.Identity;
using ECommerce.Domain.Ordering;
using ECommerce.Domain.Payments;
using FluentValidation;

namespace ECommerce.Application.Payments;

// RF-044 (pagar), RF-045 (confirmação pela plataforma) e a reconciliação do arquivo 21 §5.5.

/// <summary><see cref="CanSimulate"/>: só com o provedor falso, em desenvolvimento (entrega 4 §3, P3).</summary>
public sealed record PaymentResponse(
    Guid Id,
    string Method,
    string Status,
    string? StatusDetail,
    decimal Amount,
    int? Installments,
    string? PixQrCode,
    string? PixQrCodeBase64,
    DateTime? ExpiresAt,
    string OrderStatus,
    bool CanSimulate);

public sealed record PayOrderCommand(
    UserId CustomerId,
    Guid OrderId,
    string Method,
    string? CardToken,
    string? PaymentMethodId,
    string? IssuerId,
    int? Installments,
    string? IdempotencyKey) : ICommand<PaymentResponse>;

public sealed record GetCurrentPaymentQuery(UserId CustomerId, Guid OrderId) : IQuery<PaymentResponse>;

/// <summary>Notificação já com a assinatura conferida.</summary>
public sealed record ReceivePaymentNotificationCommand(string Provider, PaymentNotification Notification, string Payload) : ICommand;

/// <summary>Consulta a plataforma e aplica o resultado (webhook, reconciliação e simulação passam por aqui).</summary>
public sealed record SyncPaymentCommand(string Provider, string ProviderPaymentId) : ICommand;

/// <summary>Uma rodada da reconciliação: pendentes há mais de 10 min sem notificação (arquivo 21 §5.5).</summary>
public sealed record ReconcilePaymentsCommand(int BatchSize = 50) : ICommand<int>;

public sealed record SimulatePaymentApprovalCommand(UserId CustomerId, Guid OrderId) : ICommand;

/// <summary>Mensagem do Outbox: sincroniza um pagamento com a plataforma.</summary>
public sealed record SyncPaymentMessage(string Provider, string ProviderPaymentId)
{
    public const string Type = "payments.sync";
}

/// <summary>Mensagem do Outbox: cancela na plataforma o PIX pendente de um pedido cancelado.</summary>
public sealed record CancelProviderPaymentMessage(string Provider, string ProviderPaymentId)
{
    public const string Type = "payments.cancel";
}

internal sealed class PayOrderValidator : AbstractValidator<PayOrderCommand>
{
    public PayOrderValidator()
    {
        RuleFor(x => x.Method)
            .Must(method => method is nameof(PaymentMethod.Pix) or nameof(PaymentMethod.CreditCard))
            .WithMessage(PaymentErrors.MethodNotAvailable.Message);
        RuleFor(x => x.CardToken).NotEmpty().MaximumLength(200).When(x => x.Method == nameof(PaymentMethod.CreditCard))
            .WithMessage("Preencha os dados do cartão.");
        RuleFor(x => x.PaymentMethodId).NotEmpty().MaximumLength(40).When(x => x.Method == nameof(PaymentMethod.CreditCard));
        RuleFor(x => x.Installments).InclusiveBetween(1, 12).When(x => x.Installments is not null);
        RuleFor(x => x.IdempotencyKey).MaximumLength(100);
    }
}

internal static class PaymentView
{
    public static PaymentResponse From(Payment payment, OrderStatus orderStatus, bool canSimulate) => new(
        payment.Id,
        payment.Method.ToString(),
        payment.Status.ToString(),
        payment.StatusDetail,
        payment.Amount,
        payment.Installments,
        payment.PixQrCode,
        payment.PixQrCodeBase64,
        payment.ExpiresAtUtc,
        orderStatus.ToString(),
        canSimulate && payment.Status == PaymentStatus.Pending);
}

/// <summary>
/// Pagar (RF-044): só pedido aguardando pagamento e no prazo (CA1); o valor é o total do pedido (CA4). Pedir PIX de
/// novo devolve o mesmo QR enquanto ele vale (P5). Cartão aprovado na hora já confirma o pedido; o webhook só repete.
/// </summary>
internal sealed class PayOrderHandler(
    IUnitOfWork unitOfWork,
    IOrderRepository orders,
    IPaymentRepository payments,
    IPaymentGateway gateway,
    IOutbox outbox,
    TimeProvider time,
    PaymentOptions paymentOptions) : ICommandHandler<PayOrderCommand, PaymentResponse>
{
    public async Task<Result<PaymentResponse>> Handle(PayOrderCommand command, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var order = await orders.GetForUpdateAsync(command.OrderId, command.CustomerId, cancellationToken);
        if (order is null)
        {
            return OrderErrors.OrderNotFound;
        }

        if (!string.IsNullOrWhiteSpace(command.IdempotencyKey)
            && await payments.FindByIdempotencyKeyAsync(order.Id, command.IdempotencyKey, cancellationToken) is { } repeated)
        {
            return PaymentView.From(repeated, order.Status, gateway.CanSimulate);
        }

        if (order.Status != OrderStatus.PendingPayment || order.IsPaymentOverdue(now))
        {
            return PaymentErrors.OrderNotPayable;
        }

        var method = Enum.Parse<PaymentMethod>(command.Method);
        if (method == PaymentMethod.Pix && await payments.FindOpenPixAsync(order.Id, now, cancellationToken) is { } open)
        {
            return PaymentView.From(open, order.Status, gateway.CanSimulate);
        }

        var installments = method == PaymentMethod.CreditCard ? Math.Min(command.Installments ?? 1, paymentOptions.MaxInstallments) : (int?)null;
        var payment = Payment.Start(order.Id, gateway.Name, method, order.Total, installments, method == PaymentMethod.Pix ? order.PaymentDueAtUtc : null, now);
        var payer = await PayerAsync(order.CustomerId, cancellationToken);

        // Mesma chave a cada nova tentativa da cliente: se a primeira resposta se perdeu, a plataforma não cobra de novo.
        var providerKey = string.IsNullOrWhiteSpace(command.IdempotencyKey) ? payment.Id.ToString() : $"{order.Id:N}-{command.IdempotencyKey}";
        var created = await gateway.CreatePaymentAsync(
            new CreatePaymentRequest(
                providerKey,
                order.Id,
                order.Code,
                payment.Amount,
                method,
                payer,
                payment.ExpiresAtUtc,
                command.CardToken,
                command.PaymentMethodId,
                command.IssuerId,
                installments),
            cancellationToken);
        if (created.IsFailure)
        {
            return created.Error;
        }

        payment.Apply(created.Value, now);
        await payments.AddAsync(payment, string.IsNullOrWhiteSpace(command.IdempotencyKey) ? null : command.IdempotencyKey, cancellationToken);

        if (payment.Status == PaymentStatus.Approved)
        {
            await PaymentOutcome.ApprovedAsync(payment, order, orders, payments, unitOfWork, outbox, now, cancellationToken);
        }

        return PaymentView.From(payment, order.Status, gateway.CanSimulate);
    }

    private async Task<PaymentPayer> PayerAsync(UserId customerId, CancellationToken cancellationToken)
    {
        var row = await unitOfWork.Connection.QuerySingleAsync<PayerRow>(new CommandDefinition(
            """
            SELECT u.email::text AS email, c.full_name, c.cpf
              FROM identity.users u JOIN customers.customers c ON c.id = u.id
             WHERE u.id = @Id
            """,
            new { Id = customerId.Value },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));
        var names = row.FullName.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new PaymentPayer(row.Email, names[0], names.Length > 1 ? names[1] : names[0], row.Cpf);
    }

    private sealed class PayerRow
    {
        public string Email { get; init; } = string.Empty;
        public string FullName { get; init; } = string.Empty;
        public string? Cpf { get; init; }
    }
}

/// <summary>Opções que a Application precisa da seção <c>Payments</c>; a Infrastructure registra a partir da configuração.</summary>
public sealed class PaymentOptions
{
    public int MaxInstallments { get; init; } = 1;
}

/// <summary>O que acontece quando a plataforma confirma um pagamento (RF-045, RN-101, RN-102).</summary>
internal static class PaymentOutcome
{
    public static async Task ApprovedAsync(
        Payment payment,
        Order order,
        IOrderRepository orders,
        IPaymentRepository payments,
        IUnitOfWork unitOfWork,
        IOutbox outbox,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        // RN-102 e RN-101: segundo pagamento aprovado, ou aprovado depois do cancelamento. O estorno automático chega
        // na fatia 4.5; até lá a loja recebe o aviso e estorna pelo painel do Mercado Pago.
        if (payment.Amount != order.Total || await payments.HasApprovedAsync(order.Id, payment.Id, cancellationToken) || order.Status != OrderStatus.PendingPayment)
        {
            var reason = payment.Amount != order.Total
                ? $"o valor pago ({MoneyFormat.Brl(payment.Amount)}) é diferente do total do pedido ({MoneyFormat.Brl(order.Total)})"
                : order.Status == OrderStatus.Cancelled
                    ? "o pedido já estava cancelado quando o pagamento foi aprovado"
                    : "o pedido já tinha outro pagamento aprovado";
            await OrderEmails.EnqueueStoreAlertAsync(unitOfWork, outbox, order, $"Pagamento {payment.ProviderPaymentId} precisa de estorno: {reason}.", cancellationToken);
            return;
        }

        order.ConfirmPayment(null, nowUtc, $"Pagamento {payment.Method} {payment.ProviderPaymentId}");
        if (!await orders.UpdateAsync(order, cancellationToken))
        {
            throw new InvalidOperationException($"O pedido {order.Id} mudou durante a confirmação do pagamento.");
        }

        await OrderEmails.EnqueueAsync(unitOfWork, outbox, EmailTemplates.PaymentConfirmed, order, cancellationToken);
        await OrderEmails.EnqueueStoreAsync(unitOfWork, outbox, EmailTemplates.NewPaidOrder, order, cancellationToken);
    }
}

internal sealed class GetCurrentPaymentHandler(IDbConnectionFactory connectionFactory, IPaymentGateway gateway)
    : IQueryHandler<GetCurrentPaymentQuery, PaymentResponse>
{
    // A tela do PIX consulta a cada poucos segundos: lê só do banco (entrega 4 §4.3).
    private const string Sql = """
        SELECT p.id, p.method, p.status, p.status_detail, p.amount, p.installments, p.pix_qr_code, p.pix_qr_code_base64,
               p.expires_at, o.status AS order_status
          FROM ordering.orders o
          JOIN payments.payments p ON p.order_id = o.id
         WHERE o.id = @OrderId AND o.customer_id = @CustomerId
         ORDER BY p.created_at DESC
         LIMIT 1
        """;

    public async Task<Result<PaymentResponse>> Handle(GetCurrentPaymentQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            Sql, new { query.OrderId, CustomerId = query.CustomerId.Value }, cancellationToken: cancellationToken));
        if (row is null)
        {
            return PaymentErrors.PaymentNotFound;
        }

        return new PaymentResponse(
            row.Id,
            row.Method,
            row.Status,
            row.StatusDetail,
            row.Amount,
            row.Installments,
            row.PixQrCode,
            row.PixQrCodeBase64,
            row.ExpiresAt is { } expires ? DateTime.SpecifyKind(expires, DateTimeKind.Utc) : null,
            row.OrderStatus,
            gateway.CanSimulate && row.Status == nameof(PaymentStatus.Pending));
    }

    private sealed class Row
    {
        public Guid Id { get; init; }
        public string Method { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string? StatusDetail { get; init; }
        public decimal Amount { get; init; }
        public short? Installments { get; init; }
        public string? PixQrCode { get; init; }
        public string? PixQrCodeBase64 { get; init; }
        public DateTime? ExpiresAt { get; init; }
        public string OrderStatus { get; init; } = string.Empty;
    }
}

/// <summary>Grava o evento (repetido é ignorado) e deixa a consulta à plataforma para o Outbox: a resposta sai rápido.</summary>
internal sealed class ReceivePaymentNotificationHandler(IPaymentRepository payments, IOutbox outbox, TimeProvider time)
    : ICommandHandler<ReceivePaymentNotificationCommand>
{
    public async Task<Result> Handle(ReceivePaymentNotificationCommand command, CancellationToken cancellationToken)
    {
        var isNew = await payments.RecordEventAsync(
            command.Provider, command.Notification.EventId, command.Notification.ProviderPaymentId, command.Payload, time.GetUtcNow().UtcDateTime, cancellationToken);
        if (isNew)
        {
            await outbox.EnqueueAsync(SyncPaymentMessage.Type, new SyncPaymentMessage(command.Provider, command.Notification.ProviderPaymentId), cancellationToken);
        }

        return Result.Success();
    }
}

/// <summary>
/// Consulta a plataforma, nunca confia só no corpo da notificação (arquivo 21 §5.2), e aplica: aprovado confirma o
/// pedido; recusado, expirado ou cancelado só registram (o job de 48 h cuida do pedido).
/// </summary>
internal sealed class SyncPaymentHandler(
    IUnitOfWork unitOfWork,
    IOrderRepository orders,
    IPaymentRepository payments,
    IPaymentGateway gateway,
    IOutbox outbox,
    TimeProvider time) : ICommandHandler<SyncPaymentCommand>
{
    public async Task<Result> Handle(SyncPaymentCommand command, CancellationToken cancellationToken)
    {
        if (!string.Equals(command.Provider, gateway.Name, StringComparison.OrdinalIgnoreCase))
        {
            return Result.Success();
        }

        var snapshot = await gateway.GetPaymentAsync(command.ProviderPaymentId, cancellationToken);
        if (snapshot.IsFailure)
        {
            return Result.Failure(snapshot.Error);
        }

        var payment = await payments.GetByProviderIdForUpdateAsync(gateway.Name, command.ProviderPaymentId, cancellationToken);
        if (payment is null)
        {
            // A notificação pode chegar antes do commit de quem criou o pagamento: o Outbox tenta de novo depois.
            return Result.Failure(PaymentErrors.PaymentNotFound);
        }

        var order = await orders.GetForUpdateAsync(payment.OrderId, null, cancellationToken);
        if (order is null)
        {
            return Result.Failure(OrderErrors.OrderNotFound);
        }

        var now = time.GetUtcNow().UtcDateTime;
        if (snapshot.Value.Disputed)
        {
            await OrderEmails.EnqueueStoreAlertAsync(
                unitOfWork, outbox, order, $"O pagamento {payment.ProviderPaymentId} foi contestado no Mercado Pago ({snapshot.Value.StatusDetail}).", cancellationToken);
            return Result.Success();
        }

        if (!payment.Apply(snapshot.Value, now))
        {
            await payments.UpdateAsync(payment, cancellationToken);
            return Result.Success();
        }

        await payments.UpdateAsync(payment, cancellationToken);
        if (payment.Status == PaymentStatus.Approved)
        {
            await PaymentOutcome.ApprovedAsync(payment, order, orders, payments, unitOfWork, outbox, now, cancellationToken);
        }

        return Result.Success();
    }
}

internal sealed class ReconcilePaymentsHandler(IPaymentRepository payments, IOutbox outbox, TimeProvider time)
    : ICommandHandler<ReconcilePaymentsCommand, int>
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMinutes(10);

    public async Task<Result<int>> Handle(ReconcilePaymentsCommand command, CancellationToken cancellationToken)
    {
        var stale = await payments.ListStalePendingAsync(time.GetUtcNow().UtcDateTime - Quiet, command.BatchSize, cancellationToken);
        foreach (var (provider, providerPaymentId) in stale)
        {
            await outbox.EnqueueAsync(SyncPaymentMessage.Type, new SyncPaymentMessage(provider, providerPaymentId), cancellationToken);
        }

        return stale.Count;
    }
}

internal sealed class SimulatePaymentApprovalHandler(IDbConnectionFactory connectionFactory, IPaymentGateway gateway, IOutbox outbox)
    : ICommandHandler<SimulatePaymentApprovalCommand>
{
    public async Task<Result> Handle(SimulatePaymentApprovalCommand command, CancellationToken cancellationToken)
    {
        if (!gateway.CanSimulate)
        {
            return Result.Failure(PaymentErrors.SimulationNotAvailable);
        }

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var providerPaymentId = await connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            """
            SELECT p.provider_payment_id
              FROM payments.payments p JOIN ordering.orders o ON o.id = p.order_id
             WHERE o.id = @OrderId AND o.customer_id = @CustomerId AND p.status = 'Pending' AND p.provider_payment_id IS NOT NULL
             ORDER BY p.created_at DESC LIMIT 1
            """,
            new { command.OrderId, CustomerId = command.CustomerId.Value },
            cancellationToken: cancellationToken));
        if (providerPaymentId is null)
        {
            return Result.Failure(PaymentErrors.PaymentNotFound);
        }

        var simulated = await gateway.SimulateApprovalAsync(providerPaymentId, cancellationToken);
        if (simulated.IsFailure)
        {
            return simulated;
        }

        await outbox.EnqueueAsync(SyncPaymentMessage.Type, new SyncPaymentMessage(gateway.Name, providerPaymentId), cancellationToken);
        return Result.Success();
    }
}
