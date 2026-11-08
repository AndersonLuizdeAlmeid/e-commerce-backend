using Dapper;
using ECommerce.Application.Abstractions.Data;
using ECommerce.Domain.Catalog;
using ECommerce.Domain.Identity;
using ECommerce.Domain.Ordering;
using ECommerce.Domain.Shipping;

namespace ECommerce.Infrastructure.Persistence.Repositories;

internal sealed class OrderRepository(IUnitOfWork unitOfWork) : IOrderRepository
{
    private const string OrderColumns = """
        id, number, customer_id, status, shipping_fee, gift_wrap_price,
        shipping_service, shipping_service_name, shipping_days, shipping_is_fallback,
        ship_recipient, ship_postal_code, ship_street, ship_number, ship_complement, ship_district, ship_city, ship_state,
        is_gift, gift_wrap, gift_message, cancellation_reason, notes,
        payment_due_at, payment_reminder_sent_at, version, placed_at, updated_at
        """;

    public async Task AddAsync(Order order, string? idempotencyKey, CancellationToken cancellationToken)
    {
        var number = await unitOfWork.Connection.ExecuteScalarAsync<long>(Command(
            """
            INSERT INTO ordering.orders (
                id, customer_id, status, subtotal, shipping_fee, gift_wrap_price, total,
                shipping_service, shipping_service_name, shipping_days, shipping_is_fallback,
                ship_recipient, ship_postal_code, ship_street, ship_number, ship_complement, ship_district, ship_city, ship_state,
                is_gift, gift_wrap, gift_message, notes, idempotency_key, payment_due_at, placed_at, updated_at)
            VALUES (
                @Id, @CustomerId, @Status, @Subtotal, @ShippingFee, @GiftWrapPrice, @Total,
                @ShippingService, @ShippingServiceName, @ShippingDays, @ShippingIsFallback,
                @Recipient, @PostalCode, @Street, @Number, @Complement, @District, @City, @State,
                @IsGift, @GiftWrap, @GiftMessage, @Notes, @IdempotencyKey, @PaymentDueAt, @PlacedAt, @UpdatedAt)
            RETURNING number
            """,
            new
            {
                order.Id,
                CustomerId = order.CustomerId.Value,
                Status = order.Status.ToString(),
                order.Subtotal,
                ShippingFee = order.Shipping.Price,
                order.GiftWrapPrice,
                order.Total,
                ShippingService = order.Shipping.ServiceCode,
                ShippingServiceName = order.Shipping.ServiceName,
                ShippingDays = (short?)order.Shipping.DeliveryDays,
                ShippingIsFallback = order.Shipping.IsFallback,
                order.Address.Recipient,
                PostalCode = order.Address.PostalCode.Value,
                order.Address.Street,
                order.Address.Number,
                order.Address.Complement,
                order.Address.District,
                order.Address.City,
                order.Address.State,
                order.Gift.IsGift,
                GiftWrap = order.Gift.Wrap,
                GiftMessage = order.Gift.Message,
                order.Notes,
                IdempotencyKey = idempotencyKey,
                PaymentDueAt = order.PaymentDueAtUtc,
                PlacedAt = order.PlacedAtUtc,
                UpdatedAt = order.UpdatedAtUtc,
            },
            cancellationToken));
        order.AssignNumber(number);

        await unitOfWork.Connection.ExecuteAsync(Command(
            """
            INSERT INTO ordering.order_items
                (order_id, variant_id, product_id, sku, product_name, color_name, size_name, image_url, unit_price, quantity, position)
            VALUES
                (@OrderId, @VariantId, @ProductId, @Sku, @ProductName, @ColorName, @SizeName, @ImageUrl, @UnitPrice, @Quantity, @Position)
            """,
            order.Items.Select((item, index) => new
            {
                OrderId = order.Id,
                VariantId = item.VariantId.Value,
                ProductId = item.ProductId.Value,
                item.Sku,
                item.ProductName,
                item.ColorName,
                item.SizeName,
                item.ImageUrl,
                item.UnitPrice,
                item.Quantity,
                Position = (short)index,
            }).ToList(),
            cancellationToken));

        await InsertHistoryAsync(order, cancellationToken);
    }

    public async Task<Order?> GetForUpdateAsync(Guid orderId, UserId? customerId, CancellationToken cancellationToken)
    {
        var row = await unitOfWork.Connection.QuerySingleOrDefaultAsync<OrderRow>(Command(
            $"""
            SELECT {OrderColumns} FROM ordering.orders
             WHERE id = @Id AND (@CustomerId::uuid IS NULL OR customer_id = @CustomerId)
               FOR UPDATE
            """,
            new { Id = orderId, CustomerId = customerId?.Value },
            cancellationToken));

        return row is null ? null : (await WithItemsAsync([row], cancellationToken))[0];
    }

    public async Task<bool> UpdateAsync(Order order, CancellationToken cancellationToken)
    {
        var updated = await unitOfWork.Connection.ExecuteAsync(Command(
            """
            UPDATE ordering.orders
               SET status = @Status, cancellation_reason = @CancellationReason,
                   payment_reminder_sent_at = @ReminderSentAt, updated_at = @UpdatedAt, version = version + 1
             WHERE id = @Id AND version = @Version
            """,
            new
            {
                order.Id,
                Status = order.Status.ToString(),
                CancellationReason = order.CancellationReason?.ToString(),
                ReminderSentAt = order.PaymentReminderSentAtUtc,
                UpdatedAt = order.UpdatedAtUtc,
                order.Version,
            },
            cancellationToken));

        if (updated == 0)
        {
            return false;
        }

        await InsertHistoryAsync(order, cancellationToken);
        return true;
    }

    public Task<Guid?> FindByIdempotencyKeyAsync(UserId customerId, string idempotencyKey, CancellationToken cancellationToken) =>
        unitOfWork.Connection.QuerySingleOrDefaultAsync<Guid?>(Command(
            "SELECT id FROM ordering.orders WHERE customer_id = @CustomerId AND idempotency_key = @Key",
            new { CustomerId = customerId.Value, Key = idempotencyKey },
            cancellationToken));

    public Task<IReadOnlyList<Order>> LockOverdueAsync(DateTime nowUtc, int limit, CancellationToken cancellationToken) =>
        LockAsync(
            "status = 'PendingPayment' AND payment_due_at <= @Now ORDER BY payment_due_at",
            nowUtc,
            limit,
            cancellationToken);

    public Task<IReadOnlyList<Order>> LockDueForReminderAsync(DateTime nowUtc, int limit, CancellationToken cancellationToken) =>
        LockAsync(
            $"status = 'PendingPayment' AND payment_reminder_sent_at IS NULL AND placed_at <= @Now - interval '{(int)Order.ReminderAfter.TotalHours} hours' " +
            "AND payment_due_at > @Now ORDER BY placed_at",
            nowUtc,
            limit,
            cancellationToken);

    public Task<decimal> GetGiftWrapPriceAsync(CancellationToken cancellationToken) =>
        unitOfWork.Connection.ExecuteScalarAsync<decimal>(Command(
            "SELECT coalesce((SELECT gift_wrap_price FROM ordering.settings WHERE id = 1), 0)", new { }, cancellationToken));

    public Task SaveGiftWrapPriceAsync(decimal price, DateTime nowUtc, CancellationToken cancellationToken) =>
        unitOfWork.Connection.ExecuteAsync(Command(
            """
            INSERT INTO ordering.settings (id, gift_wrap_price, updated_at) VALUES (1, @Price, @Now)
            ON CONFLICT (id) DO UPDATE SET gift_wrap_price = excluded.gift_wrap_price, updated_at = excluded.updated_at
            """,
            new { Price = price, Now = nowUtc },
            cancellationToken));

    private async Task<IReadOnlyList<Order>> LockAsync(string condition, DateTime nowUtc, int limit, CancellationToken cancellationToken)
    {
        var rows = (await unitOfWork.Connection.QueryAsync<OrderRow>(Command(
            $"SELECT {OrderColumns} FROM ordering.orders WHERE {condition} LIMIT @Limit FOR UPDATE SKIP LOCKED",
            new { Now = nowUtc, Limit = limit },
            cancellationToken))).AsList();

        return rows.Count == 0 ? [] : await WithItemsAsync(rows, cancellationToken);
    }

    private async Task<IReadOnlyList<Order>> WithItemsAsync(IReadOnlyList<OrderRow> rows, CancellationToken cancellationToken)
    {
        var items = (await unitOfWork.Connection.QueryAsync<ItemRow>(Command(
            """
            SELECT order_id, variant_id, product_id, sku, product_name, color_name, size_name, image_url, unit_price, quantity
              FROM ordering.order_items
             WHERE order_id = ANY(@Ids)
             ORDER BY order_id, position
            """,
            new { Ids = rows.Select(row => row.Id).ToArray() },
            cancellationToken))).ToLookup(item => item.OrderId);

        return rows.Select(row => Order.Rehydrate(
                row.Id,
                row.Number,
                new UserId(row.CustomerId),
                items[row.Id].Select(item => new OrderItem(
                    new VariantId(item.VariantId),
                    new ProductId(item.ProductId),
                    item.Sku,
                    item.ProductName,
                    item.ColorName,
                    item.SizeName,
                    item.ImageUrl,
                    item.UnitPrice,
                    item.Quantity)),
                new ShippingAddress(
                    row.ShipRecipient,
                    PostalCode.Rehydrate(row.ShipPostalCode),
                    row.ShipStreet,
                    row.ShipNumber,
                    row.ShipComplement,
                    row.ShipDistrict,
                    row.ShipCity,
                    row.ShipState),
                new ShippingSelection(row.ShippingService, row.ShippingServiceName, row.ShippingFee, row.ShippingDays, row.ShippingIsFallback),
                new OrderGift(row.IsGift, row.GiftWrap, row.GiftMessage),
                row.GiftWrapPrice,
                row.Notes,
                Enum.Parse<OrderStatus>(row.Status),
                row.CancellationReason is null ? null : Enum.Parse<CancellationReason>(row.CancellationReason),
                row.PlacedAt,
                row.PaymentDueAt,
                row.PaymentReminderSentAt,
                row.UpdatedAt,
                row.Version))
            .ToList();
    }

    private Task InsertHistoryAsync(Order order, CancellationToken cancellationToken) =>
        order.NewHistory.Count == 0
            ? Task.CompletedTask
            : unitOfWork.Connection.ExecuteAsync(Command(
                """
                INSERT INTO ordering.order_status_history (id, order_id, from_status, to_status, changed_by, changed_at, note)
                VALUES (@Id, @OrderId, @From, @To, @ChangedBy, @ChangedAt, @Note)
                """,
                order.NewHistory.Select(change => new
                {
                    change.Id,
                    OrderId = order.Id,
                    From = change.From?.ToString(),
                    To = change.To.ToString(),
                    ChangedBy = change.ChangedBy?.Value,
                    ChangedAt = change.ChangedAtUtc,
                    change.Note,
                }).ToList(),
                cancellationToken));

    private CommandDefinition Command(string sql, object parameters, CancellationToken cancellationToken) =>
        new(sql, parameters, unitOfWork.Transaction, cancellationToken: cancellationToken);

    private sealed class OrderRow
    {
        public Guid Id { get; init; }
        public long Number { get; init; }
        public Guid CustomerId { get; init; }
        public string Status { get; init; } = string.Empty;
        public decimal ShippingFee { get; init; }
        public decimal GiftWrapPrice { get; init; }
        public string ShippingService { get; init; } = string.Empty;
        public string ShippingServiceName { get; init; } = string.Empty;
        public short? ShippingDays { get; init; }
        public bool ShippingIsFallback { get; init; }
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
        public string? CancellationReason { get; init; }
        public string? Notes { get; init; }
        public DateTime PaymentDueAt { get; init; }
        public DateTime? PaymentReminderSentAt { get; init; }
        public int Version { get; init; }
        public DateTime PlacedAt { get; init; }
        public DateTime UpdatedAt { get; init; }
    }

    private sealed class ItemRow
    {
        public Guid OrderId { get; init; }
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
}
