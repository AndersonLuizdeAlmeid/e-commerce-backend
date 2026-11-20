using Dapper;
using ECommerce.Application.Abstractions.Data;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Application.Abstractions.Outbox;
using ECommerce.Application.Notifications;
using ECommerce.Application.Ordering;
using ECommerce.Domain.Abstractions;
using ECommerce.Domain.Identity;
using ECommerce.Domain.Ordering;
using ECommerce.Domain.Payments;
using FluentValidation;

namespace ECommerce.Application.Payments;

// Estornos pendentes (RF-055, arquivo 21 §5.4, entrega 5 §3 P6). Fatia 5.2.

/// <summary>Por que o dinheiro está preso: <see cref="RefundFailed"/>, <see cref="RefundNotDone"/> ou <see cref="ManualPayment"/>.</summary>
public static class PendingRefundKinds
{
    /// <summary>A plataforma recusou o estorno.</summary>
    public const string RefundFailed = "RefundFailed";

    /// <summary>Pedido cancelado há mais de 30 min e o pagamento continua aprovado: o Outbox desistiu ou ainda não conseguiu.</summary>
    public const string RefundNotDone = "RefundNotDone";

    /// <summary>Pagamento confirmado à mão em pedido cancelado ou devolvido: só a loja devolve (entrega 5 §3, P2).</summary>
    public const string ManualPayment = "ManualPayment";
}

public static class PendingRefundActions
{
    public const string Retry = "Retry";
    public const string Sync = "Sync";
    public const string MarkRefunded = "MarkRefunded";
}

public sealed record PendingRefundResponse(
    Guid PaymentId,
    Guid OrderId,
    string OrderNumber,
    string CustomerName,
    string CustomerEmail,
    string Method,
    string? ProviderPaymentId,
    decimal Amount,
    string Status,
    string? StatusDetail,
    string Kind,
    DateTime CancelledAt,
    IReadOnlyList<string> Actions,
    string OrderStatus);

public sealed record ListPendingRefundsQuery : IQuery<IReadOnlyList<PendingRefundResponse>>;

public sealed record RetryRefundCommand(Guid PaymentId) : ICommand;

public sealed record SyncPaymentWithProviderCommand(Guid PaymentId) : ICommand;

public sealed record MarkPaymentRefundedCommand(Guid PaymentId, string? Note, UserId AdminId) : ICommand;

internal sealed class MarkPaymentRefundedValidator : AbstractValidator<MarkPaymentRefundedCommand>
{
    public MarkPaymentRefundedValidator() =>
        RuleFor(x => x.Note).NotEmpty().WithMessage("Conte como o valor foi devolvido (PIX, transferência, dinheiro...).").MaximumLength(500);
}

/// <summary>A mesma regra serve à fila e às ações, para a tela e o servidor nunca discordarem do que está pendente.</summary>
internal static class PendingRefunds
{
    public static readonly TimeSpan OutboxGrace = TimeSpan.FromMinutes(30);

    public const string Condition = """
        o.status IN ('Cancelled','Returned')
        AND (p.status = 'RefundFailed'
             OR (p.status = 'Approved' AND (p.provider = 'manual' OR o.updated_at < @Since)))
        """;

    public static string? KindOf(string paymentStatus, string provider) => (paymentStatus, provider) switch
    {
        (nameof(PaymentStatus.RefundFailed), _) => PendingRefundKinds.RefundFailed,
        (nameof(PaymentStatus.Approved), Payment.ManualProvider) => PendingRefundKinds.ManualPayment,
        (nameof(PaymentStatus.Approved), _) => PendingRefundKinds.RefundNotDone,
        _ => null,
    };

    public static IReadOnlyList<string> ActionsFor(string kind) => kind == PendingRefundKinds.ManualPayment
        ? [PendingRefundActions.MarkRefunded]
        : [PendingRefundActions.Retry, PendingRefundActions.Sync];

    /// <summary>O pagamento ainda está na fila? Trava o pagamento e o pedido para a ação valer sobre o estado atual.</summary>
    public static async Task<(Payment Payment, Order Order)?> FindAsync(
        Guid paymentId, IPaymentRepository payments, IOrderRepository orders, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var payment = await payments.GetForUpdateAsync(paymentId, cancellationToken);
        if (payment is null)
        {
            return null;
        }

        var order = await orders.GetForUpdateAsync(payment.OrderId, null, cancellationToken);
        if (order is null || order.Status is not (OrderStatus.Cancelled or OrderStatus.Returned))
        {
            return null;
        }

        var stuck = payment.Status == PaymentStatus.RefundFailed
            || (payment.Status == PaymentStatus.Approved && (payment.IsManual || order.UpdatedAtUtc < nowUtc - OutboxGrace));
        return stuck ? (payment, order) : null;
    }
}

internal sealed class ListPendingRefundsHandler(IDbConnectionFactory connectionFactory, TimeProvider time)
    : IQueryHandler<ListPendingRefundsQuery, IReadOnlyList<PendingRefundResponse>>
{
    private const string Sql = $"""
        SELECT p.id AS payment_id, o.id AS order_id, o.number, c.full_name AS customer_name, u.email::text AS customer_email,
               p.method, p.provider, p.provider_payment_id, p.amount, p.status, p.status_detail, o.updated_at AS cancelled_at,
               o.status AS order_status
          FROM payments.payments p
          JOIN ordering.orders o ON o.id = p.order_id
          JOIN identity.users u ON u.id = o.customer_id
          JOIN customers.customers c ON c.id = o.customer_id
         WHERE {PendingRefunds.Condition}
         ORDER BY o.updated_at, p.id
         LIMIT 200;
        """;

    public async Task<Result<IReadOnlyList<PendingRefundResponse>>> Handle(ListPendingRefundsQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<Row>(new CommandDefinition(
            Sql, new { Since = time.GetUtcNow().UtcDateTime - PendingRefunds.OutboxGrace }, cancellationToken: cancellationToken));

        var items = rows
            .Select(row =>
            {
                var kind = PendingRefunds.KindOf(row.Status, row.Provider) ?? PendingRefundKinds.RefundNotDone;
                return new PendingRefundResponse(
                    row.PaymentId,
                    row.OrderId,
                    Order.FormatNumber(row.Number),
                    row.CustomerName,
                    row.CustomerEmail,
                    row.Method,
                    row.ProviderPaymentId,
                    row.Amount,
                    row.Status,
                    row.StatusDetail,
                    kind,
                    DateTime.SpecifyKind(row.CancelledAt, DateTimeKind.Utc),
                    PendingRefunds.ActionsFor(kind),
                    row.OrderStatus);
            })
            .ToList();

        return items;
    }

    private sealed class Row
    {
        public Guid PaymentId { get; init; }
        public Guid OrderId { get; init; }
        public long Number { get; init; }
        public string CustomerName { get; init; } = string.Empty;
        public string CustomerEmail { get; init; } = string.Empty;
        public string Method { get; init; } = string.Empty;
        public string Provider { get; init; } = string.Empty;
        public string? ProviderPaymentId { get; init; }
        public decimal Amount { get; init; }
        public string Status { get; init; } = string.Empty;
        public string? StatusDetail { get; init; }
        public DateTime CancelledAt { get; init; }
        public string OrderStatus { get; init; } = string.Empty;
    }
}

/// <summary>Nova tentativa de estorno pelo Outbox, com as mesmas regras do estorno automático.</summary>
internal sealed class RetryRefundHandler(IPaymentRepository payments, IOrderRepository orders, IOutbox outbox, TimeProvider time)
    : ICommandHandler<RetryRefundCommand>
{
    public async Task<Result> Handle(RetryRefundCommand command, CancellationToken cancellationToken)
    {
        var found = await PendingRefunds.FindAsync(command.PaymentId, payments, orders, time.GetUtcNow().UtcDateTime, cancellationToken);
        if (found is not { } pending)
        {
            return Result.Failure(PaymentErrors.NothingToRefund);
        }

        if (pending.Payment.IsManual)
        {
            return Result.Failure(PaymentErrors.NotOnProvider);
        }

        await outbox.EnqueueAsync(RefundPaymentMessage.Type, new RefundPaymentMessage(pending.Payment.Id), cancellationToken);
        return Result.Success();
    }
}

/// <summary>Consulta a plataforma: estorno feito pelo painel do Mercado Pago tira o pagamento da fila.</summary>
internal sealed class SyncPaymentWithProviderHandler(IPaymentRepository payments, IOrderRepository orders, IOutbox outbox, TimeProvider time)
    : ICommandHandler<SyncPaymentWithProviderCommand>
{
    public async Task<Result> Handle(SyncPaymentWithProviderCommand command, CancellationToken cancellationToken)
    {
        var found = await PendingRefunds.FindAsync(command.PaymentId, payments, orders, time.GetUtcNow().UtcDateTime, cancellationToken);
        if (found is not { } pending)
        {
            return Result.Failure(PaymentErrors.NothingToRefund);
        }

        if (pending.Payment.IsManual || pending.Payment.ProviderPaymentId is null)
        {
            return Result.Failure(PaymentErrors.NotOnProvider);
        }

        await outbox.EnqueueAsync(
            SyncPaymentMessage.Type, new SyncPaymentMessage(pending.Payment.Provider, pending.Payment.ProviderPaymentId), cancellationToken);
        return Result.Success();
    }
}

/// <summary>A loja devolveu à mão o pagamento confirmado à mão (entrega 5 §3, P2): sai da fila e a cliente recebe o EM-13.</summary>
internal sealed class MarkPaymentRefundedHandler(
    IPaymentRepository payments, IOrderRepository orders, IUnitOfWork unitOfWork, IOutbox outbox, TimeProvider time)
    : ICommandHandler<MarkPaymentRefundedCommand>
{
    public async Task<Result> Handle(MarkPaymentRefundedCommand command, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var found = await PendingRefunds.FindAsync(command.PaymentId, payments, orders, now, cancellationToken);
        if (found is not { } pending)
        {
            return Result.Failure(PaymentErrors.NothingToRefund);
        }

        if (!pending.Payment.MarkRefundedManually(command.Note!, now))
        {
            return Result.Failure(PaymentErrors.ManualOnly);
        }

        await payments.UpdateAsync(pending.Payment, cancellationToken);
        await OrderEmails.EnqueueAsync(unitOfWork, outbox, EmailTemplates.PaymentRefunded, pending.Order, cancellationToken, refund: true);
        return Result.Success();
    }
}
