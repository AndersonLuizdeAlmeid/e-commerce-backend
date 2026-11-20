using System.Globalization;
using Dapper;
using ECommerce.Application.Abstractions.Data;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Application.Abstractions.Outbox;
using ECommerce.Application.Abstractions.Paging;
using ECommerce.Application.Notifications;
using ECommerce.Domain.Abstractions;
using ECommerce.Domain.Identity;
using ECommerce.Domain.Inventory;
using ECommerce.Domain.Ordering;
using ECommerce.Domain.Payments;
using FluentValidation;

namespace ECommerce.Application.Ordering;

// RF-053 (pedidos no painel) e RF-047 CA3 (correção da dedicatória). Entrega 5, fatia 5.1.

public sealed record AdminOrderSummaryResponse(
    Guid Id,
    string Number,
    string Status,
    DateTime PlacedAt,
    string CustomerName,
    string CustomerEmail,
    decimal Total,
    int ItemCount,
    string FirstProductName,
    string? FirstImageUrl,
    bool IsGift,
    string? PaymentMethod,
    string ShippingServiceName,
    string? TrackingCode);

public sealed record OrderStatusCount(string Status, int Count);

/// <summary><see cref="StatusCounts"/>: com os mesmos filtros, menos a situação, para as abas mostrarem quantos há em cada.</summary>
public sealed record AdminOrderListResponse(PagedResponse<AdminOrderSummaryResponse> Orders, IReadOnlyList<OrderStatusCount> StatusCounts);

/// <summary><see cref="Removed"/>: a cliente excluiu a conta (entrega 6 §3, P7); nome e e-mail já vêm substituídos.</summary>
public sealed record AdminOrderCustomerResponse(Guid Id, string Name, string Email, string? Phone, int OrderCount, bool Removed);

/// <summary><see cref="ChangedBy"/> nulo: o sistema (webhook, job de 48 h).</summary>
public sealed record AdminOrderHistoryResponse(string? FromStatus, string ToStatus, DateTime ChangedAt, string? ChangedBy, string? Note);

public sealed record AdminOrderPaymentResponse(
    Guid Id,
    string Method,
    string Status,
    string? StatusDetail,
    decimal Amount,
    int? Installments,
    string? ProviderPaymentId,
    DateTime CreatedAt,
    DateTime? ApprovedAt);

/// <summary><see cref="AllowedActions"/>: o que o painel oferece agora; a regra que vale é a do servidor.</summary>
public sealed record AdminOrderResponse(
    Guid Id,
    string Number,
    string Status,
    string? CancellationReason,
    DateTime PlacedAt,
    DateTime PaymentDueAt,
    AdminOrderCustomerResponse Customer,
    IReadOnlyList<OrderItemResponse> Items,
    decimal Subtotal,
    decimal ShippingFee,
    decimal GiftWrapPrice,
    decimal Total,
    OrderShippingResponse Shipping,
    OrderAddressResponse Address,
    OrderGiftResponse Gift,
    string? Notes,
    IReadOnlyList<AdminOrderHistoryResponse> History,
    IReadOnlyList<AdminOrderPaymentResponse> Payments,
    IReadOnlyList<string> AllowedActions);

/// <summary>Ações do painel sobre o pedido, com os nomes que a API devolve em <see cref="AdminOrderResponse.AllowedActions"/>.</summary>
public static class AdminOrderActions
{
    public const string ConfirmPayment = "ConfirmPayment";
    public const string StartPreparation = "StartPreparation";
    public const string Ship = "Ship";
    public const string UpdateTracking = "UpdateTracking";
    public const string Deliver = "Deliver";
    public const string Cancel = "Cancel";
    public const string CorrectGiftMessage = "CorrectGiftMessage";
    public const string Return = "Return";

    public static IReadOnlyList<string> For(OrderStatus status, bool isGift)
    {
        var actions = status switch
        {
            OrderStatus.PendingPayment => new List<string> { ConfirmPayment, Cancel },
            OrderStatus.PaymentConfirmed => [StartPreparation, Cancel],
            OrderStatus.Preparing => [Ship, Cancel],
            OrderStatus.Shipped => [Deliver, UpdateTracking],
            OrderStatus.Delivered => [Return],
            _ => [],
        };

        if (isGift && status is OrderStatus.PendingPayment or OrderStatus.PaymentConfirmed or OrderStatus.Preparing)
        {
            actions.Add(CorrectGiftMessage);
        }

        return actions;
    }
}

/// <summary>
/// <see cref="From"/> e <see cref="To"/>: dias no horário de Brasília, pela data do pedido. <see cref="Search"/>: número
/// (<c>PED-000123</c> ou <c>123</c>), nome ou e-mail. <see cref="PaymentMethod"/>: forma com que o pedido foi pago.
/// </summary>
public sealed record ListAdminOrdersQuery(
    int Page = 1,
    int PageSize = ListAdminOrdersQuery.DefaultPageSize,
    string? Status = null,
    DateOnly? From = null,
    DateOnly? To = null,
    string? Search = null,
    string? PaymentMethod = null,
    bool GiftOnly = false) : IQuery<AdminOrderListResponse>
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;
}

public sealed record GetAdminOrderQuery(Guid OrderId) : IQuery<AdminOrderResponse>;

public sealed record StartOrderPreparationCommand(Guid OrderId, UserId AdminId) : ICommand;

public sealed record ShipOrderCommand(Guid OrderId, string? TrackingCode, UserId AdminId) : ICommand;

public sealed record UpdateOrderTrackingCommand(Guid OrderId, string? TrackingCode, UserId AdminId) : ICommand;

public sealed record DeliverOrderCommand(Guid OrderId, UserId AdminId) : ICommand;

/// <summary>Cancelamento pela loja: o motivo fica no histórico, só no painel (entrega 5 §3, P5).</summary>
public sealed record AdminCancelOrderCommand(Guid OrderId, string? Reason, UserId AdminId) : ICommand;

/// <summary>Confirmação manual (RF-053 CA1): exceção, com motivo obrigatório (entrega 5 §3, P1).</summary>
public sealed record ConfirmOrderPaymentCommand(Guid OrderId, string? Reason, UserId AdminId) : ICommand;

public sealed record CorrectGiftMessageCommand(Guid OrderId, string? Message, UserId AdminId) : ICommand;

/// <summary>Devolução de pedido entregue, com o motivo no histórico (entrega 6 §3, P11).</summary>
public sealed record ReturnOrderCommand(Guid OrderId, string? Note, UserId AdminId) : ICommand;

internal static class AdminOrderRules
{
    public const int ReasonMaxLength = 500;
}

internal sealed class ListAdminOrdersValidator : AbstractValidator<ListAdminOrdersQuery>
{
    public ListAdminOrdersValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, ListAdminOrdersQuery.MaxPageSize);
        RuleFor(x => x.Status)
            .Must(status => Enum.TryParse<OrderStatus>(status, ignoreCase: false, out _))
            .When(x => x.Status is not null)
            .WithMessage("Situação desconhecida.");
        RuleFor(x => x.PaymentMethod)
            .Must(method => Enum.TryParse<PaymentMethod>(method, ignoreCase: false, out _))
            .When(x => x.PaymentMethod is not null)
            .WithMessage("Forma de pagamento desconhecida.");
        RuleFor(x => x.To)
            .GreaterThanOrEqualTo(x => x.From)
            .When(x => x.From is not null && x.To is not null)
            .WithMessage("O fim do período vem depois do início.");
        RuleFor(x => x.Search).MaximumLength(120);
    }
}

internal sealed class ShipOrderValidator : AbstractValidator<ShipOrderCommand>
{
    public ShipOrderValidator() =>
        RuleFor(x => x.TrackingCode)
            .Must(code => TrackingCode.Normalize(code).IsSuccess)
            .WithMessage(OrderErrors.InvalidTrackingCode.Message);
}

internal sealed class UpdateOrderTrackingValidator : AbstractValidator<UpdateOrderTrackingCommand>
{
    public UpdateOrderTrackingValidator() =>
        RuleFor(x => x.TrackingCode)
            .Must(code => TrackingCode.Normalize(code).IsSuccess)
            .WithMessage(OrderErrors.InvalidTrackingCode.Message);
}

internal sealed class AdminCancelOrderValidator : AbstractValidator<AdminCancelOrderCommand>
{
    public AdminCancelOrderValidator() =>
        RuleFor(x => x.Reason).NotEmpty().WithMessage("Escreva o motivo do cancelamento.").MaximumLength(AdminOrderRules.ReasonMaxLength);
}

internal sealed class ReturnOrderValidator : AbstractValidator<ReturnOrderCommand>
{
    public ReturnOrderValidator() =>
        RuleFor(x => x.Note).NotEmpty().WithMessage("Conte o motivo da devolução.").MaximumLength(Order.ReturnNoteMaxLength);
}

internal sealed class ConfirmOrderPaymentValidator : AbstractValidator<ConfirmOrderPaymentCommand>
{
    public ConfirmOrderPaymentValidator() =>
        RuleFor(x => x.Reason)
            .NotEmpty().WithMessage("Escreva como o pagamento foi conferido.")
            .MaximumLength(AdminOrderRules.ReasonMaxLength);
}

internal sealed class ListAdminOrdersHandler(IDbConnectionFactory connectionFactory) : IQueryHandler<ListAdminOrdersQuery, AdminOrderListResponse>
{
    // Filtros comuns à lista e às contagens por situação. Datas em São Paulo (entrega 5 §3, P10).
    private const string Filters = """
          FROM ordering.orders o
          JOIN identity.users u ON u.id = o.customer_id
          JOIN customers.customers c ON c.id = o.customer_id
         WHERE (@From::timestamp IS NULL OR o.placed_at >= (@From::timestamp AT TIME ZONE 'America/Sao_Paulo'))
           AND (@ToExclusive::timestamp IS NULL OR o.placed_at < (@ToExclusive::timestamp AT TIME ZONE 'America/Sao_Paulo'))
           AND (NOT @GiftOnly OR o.is_gift)
           AND (@PaymentMethod::text IS NULL OR EXISTS (
                SELECT 1 FROM payments.payments p
                 WHERE p.order_id = o.id AND p.method = @PaymentMethod AND p.status IN ('Approved','Refunded','RefundFailed')))
           AND (@Search::text IS NULL
                OR o.number = @SearchNumber
                OR c.full_name ILIKE '%' || @Search || '%'
                OR u.email::text ILIKE '%' || @Search || '%')
        """;

    public async Task<Result<AdminOrderListResponse>> Handle(ListAdminOrdersQuery query, CancellationToken cancellationToken)
    {
        var search = string.IsNullOrWhiteSpace(query.Search) ? null : SqlLike.Escape(query.Search.Trim());
        var parameters = new
        {
            From = query.From?.ToDateTime(TimeOnly.MinValue),
            ToExclusive = query.To?.AddDays(1).ToDateTime(TimeOnly.MinValue),
            query.GiftOnly,
            query.PaymentMethod,
            Search = search,
            SearchNumber = OrderNumber.TryParse(query.Search),
            query.Status,
            query.PageSize,
            Offset = (query.Page - 1) * query.PageSize,
        };

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        using var grid = await connection.QueryMultipleAsync(new CommandDefinition(ListSql, parameters, cancellationToken: cancellationToken));

        var counts = (await grid.ReadAsync<CountRow>()).Select(row => new OrderStatusCount(row.Status, row.Count)).ToList();
        var total = await grid.ReadSingleAsync<int>();
        var items = (await grid.ReadAsync<Row>())
            .Select(row => new AdminOrderSummaryResponse(
                row.Id,
                Order.FormatNumber(row.Number),
                row.Status,
                DateTime.SpecifyKind(row.PlacedAt, DateTimeKind.Utc),
                row.CustomerName,
                row.CustomerEmail,
                row.Total,
                row.ItemCount,
                row.FirstProductName ?? string.Empty,
                row.FirstImageUrl,
                row.IsGift,
                row.PaymentMethod,
                row.ShippingServiceName,
                row.TrackingCode))
            .ToList();

        return new AdminOrderListResponse(PagedResponse.Create<AdminOrderSummaryResponse>(items, query.Page, query.PageSize, total), counts);
    }

    // Contagem por situação (abas), total da página e a página, do pedido mais novo para o mais antigo.
    private const string ListSql = $"""
        SELECT o.status, count(*)::int AS count {Filters} GROUP BY o.status;

        SELECT count(*)::int {Filters} AND (@Status::text IS NULL OR o.status = @Status);

        SELECT o.id, o.number, o.status, o.placed_at, c.full_name AS customer_name, u.email::text AS customer_email, o.total,
               (SELECT count(*) FROM ordering.order_items i WHERE i.order_id = o.id)::int AS item_count,
               (SELECT i.product_name FROM ordering.order_items i WHERE i.order_id = o.id ORDER BY i.position LIMIT 1) AS first_product_name,
               (SELECT i.image_url FROM ordering.order_items i WHERE i.order_id = o.id ORDER BY i.position LIMIT 1) AS first_image_url,
               o.is_gift, o.shipping_service_name, o.tracking_code,
               (SELECT p.method FROM payments.payments p
                 WHERE p.order_id = o.id
                 ORDER BY (p.status IN ('Approved','Refunded','RefundFailed')) DESC, p.created_at DESC LIMIT 1) AS payment_method
          {Filters} AND (@Status::text IS NULL OR o.status = @Status)
         ORDER BY o.placed_at DESC, o.id DESC
         LIMIT @PageSize OFFSET @Offset;
        """;

    private sealed class CountRow
    {
        public string Status { get; init; } = string.Empty;
        public int Count { get; init; }
    }

    private sealed class Row
    {
        public Guid Id { get; init; }
        public long Number { get; init; }
        public string Status { get; init; } = string.Empty;
        public DateTime PlacedAt { get; init; }
        public string CustomerName { get; init; } = string.Empty;
        public string CustomerEmail { get; init; } = string.Empty;
        public decimal Total { get; init; }
        public int ItemCount { get; init; }
        public string? FirstProductName { get; init; }
        public string? FirstImageUrl { get; init; }
        public bool IsGift { get; init; }
        public string ShippingServiceName { get; init; } = string.Empty;
        public string? TrackingCode { get; init; }
        public string? PaymentMethod { get; init; }
    }
}

/// <summary>"PED-000123", "ped 123" ou "123" → 123. Qualquer outra coisa → nulo.</summary>
internal static class OrderNumber
{
    public static long? TryParse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var digits = value.Trim().ToUpperInvariant().Replace("PED", string.Empty, StringComparison.Ordinal).Trim(' ', '-');
        return digits.Length is > 0 and <= 12 && digits.All(char.IsAsciiDigit)
            ? long.Parse(digits, CultureInfo.InvariantCulture)
            : null;
    }
}

/// <summary>Busca com <c>LIKE</c>: o que a pessoa digita é texto, não curinga.</summary>
internal static class SqlLike
{
    public static string Escape(string value) =>
        value.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("%", @"\%", StringComparison.Ordinal).Replace("_", @"\_", StringComparison.Ordinal);
}

internal sealed class GetAdminOrderHandler(IDbConnectionFactory connectionFactory, OrderingOptions options)
    : IQueryHandler<GetAdminOrderQuery, AdminOrderResponse>
{
    private const string Sql = """
        SELECT o.id, o.number, o.status, o.cancellation_reason, o.placed_at, o.payment_due_at, o.subtotal, o.shipping_fee,
               o.gift_wrap_price, o.total, o.shipping_service, o.shipping_service_name, o.shipping_days, o.shipping_is_fallback,
               o.tracking_code, o.ship_recipient, o.ship_postal_code, o.ship_street, o.ship_number, o.ship_complement,
               o.ship_district, o.ship_city, o.ship_state, o.is_gift, o.gift_wrap, o.gift_message, o.notes,
               o.customer_id, c.full_name AS customer_name, u.email::text AS customer_email, c.phone AS customer_phone,
               u.anonymized_at IS NOT NULL AS customer_removed,
               (SELECT count(*) FROM ordering.orders other WHERE other.customer_id = o.customer_id)::int AS customer_order_count
          FROM ordering.orders o
          JOIN identity.users u ON u.id = o.customer_id
          JOIN customers.customers c ON c.id = o.customer_id
         WHERE o.id = @OrderId;

        SELECT variant_id, product_id, sku, product_name, color_name, size_name, image_url, unit_price, quantity
          FROM ordering.order_items
         WHERE order_id = @OrderId
         ORDER BY position;

        SELECT h.from_status, h.to_status, h.changed_at, h.note,
               CASE WHEN h.changed_by IS NULL THEN NULL ELSE coalesce(hc.full_name, hu.email::text) END AS changed_by
          FROM ordering.order_status_history h
          LEFT JOIN identity.users hu ON hu.id = h.changed_by
          LEFT JOIN customers.customers hc ON hc.id = h.changed_by
         WHERE h.order_id = @OrderId
         ORDER BY h.changed_at, h.id;

        SELECT id, method, status, status_detail, amount, installments, provider_payment_id, created_at, approved_at
          FROM payments.payments
         WHERE order_id = @OrderId
         ORDER BY created_at;
        """;

    public async Task<Result<AdminOrderResponse>> Handle(GetAdminOrderQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        using var grid = await connection.QueryMultipleAsync(new CommandDefinition(Sql, new { query.OrderId }, cancellationToken: cancellationToken));

        var order = await grid.ReadSingleOrDefaultAsync<OrderRow>();
        if (order is null)
        {
            return OrderErrors.OrderNotFound;
        }

        var items = (await grid.ReadAsync<ItemRow>())
            .Select(item => new OrderItemResponse(
                item.VariantId, item.ProductId, item.Sku, item.ProductName, item.ColorName, item.SizeName, item.ImageUrl,
                item.UnitPrice, item.Quantity, item.UnitPrice * item.Quantity))
            .ToList();
        var history = (await grid.ReadAsync<HistoryRow>())
            .Select(change => new AdminOrderHistoryResponse(change.FromStatus, change.ToStatus, Utc(change.ChangedAt), change.ChangedBy, change.Note))
            .ToList();
        var payments = (await grid.ReadAsync<PaymentRow>())
            .Select(payment => new AdminOrderPaymentResponse(
                payment.Id,
                payment.Method,
                payment.Status,
                payment.StatusDetail,
                payment.Amount,
                payment.Installments,
                payment.ProviderPaymentId,
                Utc(payment.CreatedAt),
                payment.ApprovedAt is { } approvedAt ? Utc(approvedAt) : null))
            .ToList();

        return new AdminOrderResponse(
            order.Id,
            Order.FormatNumber(order.Number),
            order.Status,
            order.CancellationReason,
            Utc(order.PlacedAt),
            Utc(order.PaymentDueAt),
            new AdminOrderCustomerResponse(
                order.CustomerId, order.CustomerName, order.CustomerEmail, order.CustomerPhone, order.CustomerOrderCount, order.CustomerRemoved),
            items,
            order.Subtotal,
            order.ShippingFee,
            order.GiftWrapPrice,
            order.Total,
            new OrderShippingResponse(
                order.ShippingService, order.ShippingServiceName, order.ShippingFee, order.ShippingDays, order.ShippingIsFallback, order.TrackingCode,
                options.TrackingUrl(order.TrackingCode)),
            new OrderAddressResponse(
                order.ShipRecipient, order.ShipPostalCode, order.ShipStreet, order.ShipNumber, order.ShipComplement, order.ShipDistrict, order.ShipCity, order.ShipState),
            new OrderGiftResponse(order.IsGift, order.GiftWrap, order.GiftMessage, order.GiftWrapPrice),
            order.Notes,
            history,
            payments,
            AdminOrderActions.For(Enum.Parse<OrderStatus>(order.Status), order.IsGift));
    }

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private sealed class OrderRow
    {
        public Guid Id { get; init; }
        public long Number { get; init; }
        public string Status { get; init; } = string.Empty;
        public string? CancellationReason { get; init; }
        public DateTime PlacedAt { get; init; }
        public DateTime PaymentDueAt { get; init; }
        public decimal Subtotal { get; init; }
        public decimal ShippingFee { get; init; }
        public decimal GiftWrapPrice { get; init; }
        public decimal Total { get; init; }
        public string ShippingService { get; init; } = string.Empty;
        public string ShippingServiceName { get; init; } = string.Empty;
        public short? ShippingDays { get; init; }
        public bool ShippingIsFallback { get; init; }
        public string? TrackingCode { get; init; }
        public string ShipRecipient { get; init; } = string.Empty;
        public string ShipPostalCode { get; init; } = string.Empty;
        public string ShipStreet { get; init; } = string.Empty;
        public string ShipNumber { get; init; } = string.Empty;
        public string? ShipComplement { get; init; }
        public string ShipDistrict { get; init; } = string.Empty;
        public string ShipCity { get; init; } = string.Empty;
        public string ShipState { get; init; } = string.Empty;
        public bool IsGift { get; init; }
        public bool GiftWrap { get; init; }
        public string? GiftMessage { get; init; }
        public string? Notes { get; init; }
        public Guid CustomerId { get; init; }
        public string CustomerName { get; init; } = string.Empty;
        public string CustomerEmail { get; init; } = string.Empty;
        public string? CustomerPhone { get; init; }
        public int CustomerOrderCount { get; init; }
        public bool CustomerRemoved { get; init; }
    }

    private sealed class ItemRow
    {
        public Guid VariantId { get; init; }
        public Guid ProductId { get; init; }
        public string Sku { get; init; } = string.Empty;
        public string ProductName { get; init; } = string.Empty;
        public string? ColorName { get; init; }
        public string? SizeName { get; init; }
        public string? ImageUrl { get; init; }
        public decimal UnitPrice { get; init; }
        public int Quantity { get; init; }
    }

    private sealed class HistoryRow
    {
        public string? FromStatus { get; init; }
        public string ToStatus { get; init; } = string.Empty;
        public DateTime ChangedAt { get; init; }
        public string? ChangedBy { get; init; }
        public string? Note { get; init; }
    }

    private sealed class PaymentRow
    {
        public Guid Id { get; init; }
        public string Method { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string? StatusDetail { get; init; }
        public decimal Amount { get; init; }
        public short? Installments { get; init; }
        public string? ProviderPaymentId { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime? ApprovedAt { get; init; }
    }
}

/// <summary>Trava o pedido, aplica a mudança e grava; versão mudada no meio → a ação não vale (outra pessoa agiu antes).</summary>
internal static class AdminOrderChange
{
    public static async Task<Result<Order>> ApplyAsync(
        IOrderRepository orders, Guid orderId, Func<Order, Result> change, CancellationToken cancellationToken)
    {
        var order = await orders.GetForUpdateAsync(orderId, null, cancellationToken);
        if (order is null)
        {
            return OrderErrors.OrderNotFound;
        }

        var changed = change(order);
        if (changed.IsFailure)
        {
            return changed.Error;
        }

        if (order.NewHistory.Count > 0 && !await orders.UpdateAsync(order, cancellationToken))
        {
            return OrderErrors.InvalidTransition;
        }

        return order;
    }
}

internal sealed class StartOrderPreparationHandler(IOrderRepository orders, TimeProvider time) : ICommandHandler<StartOrderPreparationCommand>
{
    public async Task<Result> Handle(StartOrderPreparationCommand command, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var result = await AdminOrderChange.ApplyAsync(orders, command.OrderId, order => order.StartPreparation(command.AdminId, now), cancellationToken);
        return result.IsSuccess ? Result.Success() : Result.Failure(result.Error);
    }
}

/// <summary>Enviado com rastreio e EM-08 para a cliente.</summary>
internal sealed class ShipOrderHandler(IOrderRepository orders, IUnitOfWork unitOfWork, IOutbox outbox, OrderingOptions options, TimeProvider time)
    : ICommandHandler<ShipOrderCommand>
{
    public async Task<Result> Handle(ShipOrderCommand command, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var result = await AdminOrderChange.ApplyAsync(orders, command.OrderId, order => order.Ship(command.TrackingCode, command.AdminId, now), cancellationToken);
        if (result.IsFailure)
        {
            return Result.Failure(result.Error);
        }

        var order = result.Value;
        await OrderEmails.EnqueueAsync(
            unitOfWork, outbox, EmailTemplates.OrderShipped, order, cancellationToken, trackingUrl: options.TrackingUrl(order.TrackingCode));
        return Result.Success();
    }
}

internal sealed class UpdateOrderTrackingHandler(IOrderRepository orders, TimeProvider time) : ICommandHandler<UpdateOrderTrackingCommand>
{
    public async Task<Result> Handle(UpdateOrderTrackingCommand command, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var result = await AdminOrderChange.ApplyAsync(
            orders, command.OrderId, order => order.UpdateTrackingCode(command.TrackingCode, command.AdminId, now), cancellationToken);
        return result.IsSuccess ? Result.Success() : Result.Failure(result.Error);
    }
}

/// <summary>Entregue e EM-09 para a cliente.</summary>
internal sealed class DeliverOrderHandler(IOrderRepository orders, IUnitOfWork unitOfWork, IOutbox outbox, TimeProvider time)
    : ICommandHandler<DeliverOrderCommand>
{
    public async Task<Result> Handle(DeliverOrderCommand command, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var result = await AdminOrderChange.ApplyAsync(orders, command.OrderId, order => order.Deliver(command.AdminId, now), cancellationToken);
        if (result.IsFailure)
        {
            return Result.Failure(result.Error);
        }

        await OrderEmails.EnqueueAsync(unitOfWork, outbox, EmailTemplates.OrderDelivered, result.Value, cancellationToken);
        return Result.Success();
    }
}

/// <summary>
/// Cancelamento pela loja (arquivo 04 §4.8): aguardando pagamento, pago ou em separação. Mesmo caminho do cancelamento
/// da cliente: estoque de volta, PIX aberto cancelado, estorno pelo Outbox e EM-10 "pela loja".
/// </summary>
internal sealed class AdminCancelOrderHandler(
    IOrderRepository orders, IStockItemRepository stock, IUnitOfWork unitOfWork, IOutbox outbox, TimeProvider time)
    : ICommandHandler<AdminCancelOrderCommand>
{
    public async Task<Result> Handle(AdminCancelOrderCommand command, CancellationToken cancellationToken)
    {
        var order = await orders.GetForUpdateAsync(command.OrderId, null, cancellationToken);
        if (order is null)
        {
            return Result.Failure(OrderErrors.OrderNotFound);
        }

        return await OrderCancellation.CancelAsync(
            order,
            CancellationReason.AdminRequest,
            command.AdminId,
            orders,
            stock,
            unitOfWork,
            outbox,
            time.GetUtcNow().UtcDateTime,
            command.Reason!.Trim(),
            cancellationToken);
    }
}

/// <summary>
/// Confirmação manual (RF-053 CA1, entrega 5 §3 P1): grava o pagamento <c>Manual</c> aprovado, confirma o pedido, cancela
/// o PIX aberto na plataforma e manda o EM-06. Se a plataforma já aprovou um pagamento que não confirmou o pedido (valor
/// diferente, RN-100), só o pedido é confirmado: não nasce um segundo aprovado.
/// </summary>
internal sealed class ConfirmOrderPaymentHandler(
    IOrderRepository orders, IPaymentRepository payments, IUnitOfWork unitOfWork, IOutbox outbox, TimeProvider time)
    : ICommandHandler<ConfirmOrderPaymentCommand>
{
    public async Task<Result> Handle(ConfirmOrderPaymentCommand command, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var reason = command.Reason!.Trim();
        var order = await orders.GetForUpdateAsync(command.OrderId, null, cancellationToken);
        if (order is null)
        {
            return Result.Failure(OrderErrors.OrderNotFound);
        }

        var confirmed = order.ConfirmPayment(command.AdminId, now, $"Confirmado à mão: {reason}");
        if (confirmed.IsFailure)
        {
            return confirmed;
        }

        if (!await orders.UpdateAsync(order, cancellationToken))
        {
            return Result.Failure(OrderErrors.InvalidTransition);
        }

        if (!await payments.HasApprovedAsync(order.Id, Guid.Empty, cancellationToken))
        {
            await payments.AddAsync(Payment.RecordManual(order.Id, order.Total, reason, now), null, cancellationToken);
        }

        // PIX ainda aberto: a cliente não deve pagar de novo. Se pagar mesmo assim, é o segundo aprovado e volta sozinho (RN-102).
        var openPayments = await unitOfWork.Connection.QueryAsync<(string Provider, string ProviderPaymentId)>(new CommandDefinition(
            "SELECT provider, provider_payment_id FROM payments.payments WHERE order_id = @Id AND status = 'Pending' AND provider_payment_id IS NOT NULL",
            new { order.Id },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));
        foreach (var (provider, providerPaymentId) in openPayments)
        {
            await outbox.EnqueueAsync(
                Payments.CancelProviderPaymentMessage.Type, new Payments.CancelProviderPaymentMessage(provider, providerPaymentId), cancellationToken);
        }

        await OrderEmails.EnqueueAsync(unitOfWork, outbox, EmailTemplates.PaymentConfirmed, order, cancellationToken);
        return Result.Success();
    }
}

internal sealed class CorrectGiftMessageHandler(IOrderRepository orders, TimeProvider time) : ICommandHandler<CorrectGiftMessageCommand>
{
    public async Task<Result> Handle(CorrectGiftMessageCommand command, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var result = await AdminOrderChange.ApplyAsync(
            orders, command.OrderId, order => order.CorrectGiftMessage(command.Message, command.AdminId, now), cancellationToken);
        return result.IsSuccess ? Result.Success() : Result.Failure(result.Error);
    }
}

/// <summary>
/// Devolução (entrega 6 §3, P11): Entregue → Devolvido e estorno total pelo Outbox, como no cancelamento. A cliente recebe
/// o EM-13 quando o estorno é aceito; pagamento confirmado à mão vai para a fila de estornos pendentes, porque só a loja
/// devolve. O estoque não volta sozinho: a peça é conferida e entra por ajuste.
/// </summary>
internal sealed class ReturnOrderHandler(IOrderRepository orders, IUnitOfWork unitOfWork, IOutbox outbox, TimeProvider time)
    : ICommandHandler<ReturnOrderCommand>
{
    public async Task<Result> Handle(ReturnOrderCommand command, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var result = await AdminOrderChange.ApplyAsync(
            orders, command.OrderId, order => order.MarkReturned(command.Note, command.AdminId, now), cancellationToken);
        if (result.IsFailure)
        {
            return Result.Failure(result.Error);
        }

        var approved = await unitOfWork.Connection.QueryAsync<Guid>(new CommandDefinition(
            "SELECT id FROM payments.payments WHERE order_id = @Id AND status = 'Approved' AND provider <> @Manual",
            new { result.Value.Id, Manual = Payment.ManualProvider },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));
        foreach (var paymentId in approved)
        {
            await outbox.EnqueueAsync(Payments.RefundPaymentMessage.Type, new Payments.RefundPaymentMessage(paymentId), cancellationToken);
        }

        return Result.Success();
    }
}
