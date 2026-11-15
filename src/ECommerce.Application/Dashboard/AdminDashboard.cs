using Dapper;
using ECommerce.Application.Abstractions.Data;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Application.Ordering;
using ECommerce.Application.Payments;
using ECommerce.Domain.Abstractions;
using FluentValidation;

namespace ECommerce.Application.Dashboard;

// RF-055 — dashboard do painel. Entrega 5, fatia 5.4 (§3 P10 e P11).

/// <summary>O que espera a loja agora, independente do período.</summary>
public sealed record DashboardToDoResponse(int AwaitingPayment, int ToPrepare, int Preparing, int InTransit, int PendingRefunds);

public sealed record DashboardLowStockItem(
    Guid VariantId, Guid ProductId, string ProductName, string? ColorName, string? SizeName, string Sku, int QuantityOnHand, int LowStockThreshold);

/// <summary>
/// Período em dias de Brasília. <see cref="Revenue"/>: pagamentos aprovados no período que continuam aprovados;
/// <see cref="RefundedAmount"/>: os aprovados no período que já voltaram para a cliente.
/// </summary>
public sealed record DashboardResponse(
    DateOnly From,
    DateOnly To,
    int OrdersPlaced,
    IReadOnlyList<OrderStatusCount> OrdersByStatus,
    decimal Revenue,
    int PaidOrders,
    decimal AverageTicket,
    decimal RefundedAmount,
    int RefundedPayments,
    DashboardToDoResponse ToDo,
    int LowStockCount,
    IReadOnlyList<DashboardLowStockItem> LowStock);

/// <summary>Sem datas: os últimos 30 dias, contando hoje.</summary>
public sealed record GetDashboardQuery(DateOnly? From = null, DateOnly? To = null) : IQuery<DashboardResponse>
{
    public const int DefaultDays = 30;
    public const int MaxDays = 366;
    public const int LowStockShown = 8;
}

internal sealed class GetDashboardValidator : AbstractValidator<GetDashboardQuery>
{
    public GetDashboardValidator()
    {
        RuleFor(x => x.To)
            .GreaterThanOrEqualTo(x => x.From)
            .When(x => x.From is not null && x.To is not null)
            .WithMessage("O fim do período vem depois do início.");
        RuleFor(x => x)
            .Must(x => x.From is null || x.To is null || x.To.Value.DayNumber - x.From.Value.DayNumber < GetDashboardQuery.MaxDays)
            .WithName("to")
            .WithMessage("Escolha um período de até um ano.");
    }
}

internal sealed class GetDashboardHandler(IDbConnectionFactory connectionFactory, TimeProvider time) : IQueryHandler<GetDashboardQuery, DashboardResponse>
{
    private static readonly TimeZoneInfo SaoPaulo = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    private const string Sql = $"""
        SELECT status, count(*)::int AS count
          FROM ordering.orders
         WHERE placed_at >= (@From::timestamp AT TIME ZONE 'America/Sao_Paulo')
           AND placed_at < (@ToExclusive::timestamp AT TIME ZONE 'America/Sao_Paulo')
         GROUP BY status;

        SELECT coalesce(sum(amount) FILTER (WHERE status = 'Approved'), 0) AS revenue,
               count(DISTINCT order_id) FILTER (WHERE status = 'Approved')::int AS paid_orders,
               coalesce(sum(amount) FILTER (WHERE status = 'Refunded'), 0) AS refunded_amount,
               count(*) FILTER (WHERE status = 'Refunded')::int AS refunded_payments
          FROM payments.payments
         WHERE approved_at >= (@From::timestamp AT TIME ZONE 'America/Sao_Paulo')
           AND approved_at < (@ToExclusive::timestamp AT TIME ZONE 'America/Sao_Paulo');

        SELECT count(*) FILTER (WHERE status = 'PendingPayment')::int AS awaiting_payment,
               count(*) FILTER (WHERE status = 'PaymentConfirmed')::int AS to_prepare,
               count(*) FILTER (WHERE status = 'Preparing')::int AS preparing,
               count(*) FILTER (WHERE status = 'Shipped')::int AS in_transit,
               (SELECT count(*) FROM payments.payments p JOIN ordering.orders o ON o.id = p.order_id
                 WHERE {PendingRefunds.Condition})::int AS pending_refunds
          FROM ordering.orders
         WHERE status IN ('PendingPayment','PaymentConfirmed','Preparing','Shipped');

        SELECT count(*)::int
          FROM inventory.stock_items st
          JOIN catalog.product_variants v ON v.id = st.variant_id
          JOIN catalog.products p ON p.id = v.product_id
         WHERE st.quantity_on_hand <= st.low_stock_threshold AND v.is_active AND p.is_active;

        SELECT st.variant_id, v.product_id, p.name AS product_name, co.name AS color_name, s.name AS size_name, v.sku,
               st.quantity_on_hand, st.low_stock_threshold
          FROM inventory.stock_items st
          JOIN catalog.product_variants v ON v.id = st.variant_id
          JOIN catalog.products p ON p.id = v.product_id
          LEFT JOIN catalog.colors co ON co.id = v.color_id
          LEFT JOIN catalog.sizes s ON s.id = v.size_id
         WHERE st.quantity_on_hand <= st.low_stock_threshold AND v.is_active AND p.is_active
         ORDER BY st.quantity_on_hand, p.name, v.id
         LIMIT @LowStockShown;
        """;

    public async Task<Result<DashboardResponse>> Handle(GetDashboardQuery query, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now, SaoPaulo));
        var to = query.To ?? today;
        var from = query.From ?? to.AddDays(-(GetDashboardQuery.DefaultDays - 1));

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        using var grid = await connection.QueryMultipleAsync(new CommandDefinition(
            Sql,
            new
            {
                From = from.ToDateTime(TimeOnly.MinValue),
                ToExclusive = to.AddDays(1).ToDateTime(TimeOnly.MinValue),
                Since = now - PendingRefunds.OutboxGrace,
                GetDashboardQuery.LowStockShown,
            },
            cancellationToken: cancellationToken));

        var byStatus = (await grid.ReadAsync<StatusRow>()).Select(row => new OrderStatusCount(row.Status, row.Count)).ToList();
        var money = await grid.ReadSingleAsync<MoneyRow>();
        var todo = await grid.ReadSingleAsync<ToDoRow>();
        var lowStockCount = await grid.ReadSingleAsync<int>();
        var lowStock = (await grid.ReadAsync<LowStockRow>())
            .Select(row => new DashboardLowStockItem(
                row.VariantId, row.ProductId, row.ProductName, row.ColorName, row.SizeName, row.Sku, row.QuantityOnHand, row.LowStockThreshold))
            .ToList();

        return new DashboardResponse(
            from,
            to,
            byStatus.Sum(row => row.Count),
            byStatus,
            money.Revenue,
            money.PaidOrders,
            money.PaidOrders == 0 ? 0m : decimal.Round(money.Revenue / money.PaidOrders, 2, MidpointRounding.AwayFromZero),
            money.RefundedAmount,
            money.RefundedPayments,
            new DashboardToDoResponse(todo.AwaitingPayment, todo.ToPrepare, todo.Preparing, todo.InTransit, todo.PendingRefunds),
            lowStockCount,
            lowStock);
    }

    private sealed class StatusRow
    {
        public string Status { get; init; } = string.Empty;
        public int Count { get; init; }
    }

    private sealed class MoneyRow
    {
        public decimal Revenue { get; init; }
        public int PaidOrders { get; init; }
        public decimal RefundedAmount { get; init; }
        public int RefundedPayments { get; init; }
    }

    private sealed class ToDoRow
    {
        public int AwaitingPayment { get; init; }
        public int ToPrepare { get; init; }
        public int Preparing { get; init; }
        public int InTransit { get; init; }
        public int PendingRefunds { get; init; }
    }

    private sealed class LowStockRow
    {
        public Guid VariantId { get; init; }
        public Guid ProductId { get; init; }
        public string ProductName { get; init; } = string.Empty;
        public string? ColorName { get; init; }
        public string? SizeName { get; init; }
        public string Sku { get; init; } = string.Empty;
        public int QuantityOnHand { get; init; }
        public int LowStockThreshold { get; init; }
    }
}
