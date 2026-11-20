using Dapper;
using ECommerce.Application.Abstractions.Authentication;
using ECommerce.Application.Abstractions.Data;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Domain.Abstractions;
using ECommerce.Domain.Identity;
using FluentValidation;

namespace ECommerce.Application.Identity.Privacy;

// Direitos da titular na LGPD (arquivo 17 §5, entrega 6 §3 P7 e P8). Fatia 6.3.

public sealed record DataExportAccount(
    string FullName, string Email, string? Phone, string? Cpf, DateTime CreatedAt, DateTime? EmailConfirmedAt);

public sealed record DataExportAddress(
    string Recipient, string PostalCode, string Street, string Number, string? Complement, string District, string City, string State,
    bool IsDefault, DateTime CreatedAt);

public sealed record DataExportOrderItem(string Sku, string ProductName, string? ColorName, string? SizeName, decimal UnitPrice, int Quantity);

public sealed record DataExportOrderStatus(string Status, DateTime ChangedAt);

public sealed record DataExportPayment(string Method, string Status, decimal Amount, int? Installments, DateTime CreatedAt, DateTime? ApprovedAt);

public sealed record DataExportOrder(
    string Number,
    string Status,
    DateTime PlacedAt,
    decimal Subtotal,
    decimal ShippingFee,
    decimal GiftWrapPrice,
    decimal Total,
    string ShippingServiceName,
    string? TrackingCode,
    DataExportAddress ShippingAddress,
    bool IsGift,
    string? GiftMessage,
    string? Notes,
    IReadOnlyList<DataExportOrderItem> Items,
    IReadOnlyList<DataExportOrderStatus> History,
    IReadOnlyList<DataExportPayment> Payments);

/// <summary>
/// Tudo o que a loja guarda sobre a cliente (P8). <see cref="Notes"/> explica o que não está aqui e por quê: a loja não
/// guarda cartão, não usa cookie além do de sessão e não registra consentimento de marketing porque não faz marketing.
/// </summary>
public sealed record DataExportResponse(
    DateTime GeneratedAt,
    DataExportAccount Account,
    IReadOnlyList<DataExportAddress> Addresses,
    IReadOnlyList<DataExportOrder> Orders,
    IReadOnlyList<string> Notes);

public sealed record ExportMyDataQuery(UserId UserId) : IQuery<DataExportResponse>;

internal sealed class ExportMyDataHandler(IDbConnectionFactory connectionFactory, TimeProvider time)
    : IQueryHandler<ExportMyDataQuery, DataExportResponse>
{
    private static readonly string[] ExportNotes =
    [
        "Dados de cartão não passam pela loja: ficam com o Mercado Pago, que processa o pagamento.",
        "A loja usa só o cookie da sessão (para você continuar conectada). Não há analytics, pixel nem e-mail de marketing.",
        "Os e-mails enviados ficam registrados por 180 dias só com o endereço mascarado, para conferir a entrega.",
    ];

    // Só linhas da própria conta: toda consulta filtra pelo id de quem pediu (IDOR, arquivo 09 §3).
    private const string Sql = """
        SELECT c.full_name, u.email::text AS email, c.phone, c.cpf, u.created_at, u.email_confirmed_at
          FROM identity.users u
          JOIN customers.customers c ON c.id = u.id
         WHERE u.id = @Id AND u.is_active AND u.anonymized_at IS NULL;

        SELECT recipient, postal_code, street, number, complement, district, city, state, is_default, created_at
          FROM customers.addresses
         WHERE customer_id = @Id
         ORDER BY is_default DESC, created_at;

        SELECT id, number, status, placed_at, subtotal, shipping_fee, gift_wrap_price, total, shipping_service_name, tracking_code,
               ship_recipient, ship_postal_code, ship_street, ship_number, ship_complement, ship_district, ship_city, ship_state,
               is_gift, gift_message, notes
          FROM ordering.orders
         WHERE customer_id = @Id
         ORDER BY placed_at;

        SELECT i.order_id, i.sku, i.product_name, i.color_name, i.size_name, i.unit_price, i.quantity
          FROM ordering.order_items i
          JOIN ordering.orders o ON o.id = i.order_id
         WHERE o.customer_id = @Id
         ORDER BY i.order_id, i.position;

        SELECT h.order_id, h.to_status, h.changed_at
          FROM ordering.order_status_history h
          JOIN ordering.orders o ON o.id = h.order_id
         WHERE o.customer_id = @Id AND (h.from_status IS NULL OR h.from_status <> h.to_status)
         ORDER BY h.order_id, h.changed_at, h.id;

        SELECT p.order_id, p.method, p.status, p.amount, p.installments, p.created_at, p.approved_at
          FROM payments.payments p
          JOIN ordering.orders o ON o.id = p.order_id
         WHERE o.customer_id = @Id
         ORDER BY p.order_id, p.created_at;
        """;

    public async Task<Result<DataExportResponse>> Handle(ExportMyDataQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        using var grid = await connection.QueryMultipleAsync(new CommandDefinition(Sql, new { Id = query.UserId.Value }, cancellationToken: cancellationToken));

        var account = await grid.ReadSingleOrDefaultAsync<AccountRow>();
        if (account is null)
        {
            return IdentityErrors.UserNotFound;
        }

        var addresses = (await grid.ReadAsync<AddressRow>())
            .Select(row => new DataExportAddress(
                row.Recipient, row.PostalCode, row.Street, row.Number, row.Complement, row.District, row.City, row.State, row.IsDefault, Utc(row.CreatedAt)))
            .ToList();
        var orders = (await grid.ReadAsync<OrderRow>()).ToList();
        var items = (await grid.ReadAsync<ItemRow>()).ToLookup(row => row.OrderId);
        var history = (await grid.ReadAsync<HistoryRow>()).ToLookup(row => row.OrderId);
        var payments = (await grid.ReadAsync<PaymentRow>()).ToLookup(row => row.OrderId);

        return new DataExportResponse(
            time.GetUtcNow().UtcDateTime,
            new DataExportAccount(account.FullName, account.Email, account.Phone, account.Cpf, Utc(account.CreatedAt), account.EmailConfirmedAt is { } at ? Utc(at) : null),
            addresses,
            orders.Select(order => new DataExportOrder(
                    Domain.Ordering.Order.FormatNumber(order.Number),
                    order.Status,
                    Utc(order.PlacedAt),
                    order.Subtotal,
                    order.ShippingFee,
                    order.GiftWrapPrice,
                    order.Total,
                    order.ShippingServiceName,
                    order.TrackingCode,
                    new DataExportAddress(
                        order.ShipRecipient, order.ShipPostalCode, order.ShipStreet, order.ShipNumber, order.ShipComplement, order.ShipDistrict,
                        order.ShipCity, order.ShipState, false, Utc(order.PlacedAt)),
                    order.IsGift,
                    order.GiftMessage,
                    order.Notes,
                    items[order.Id].Select(item => new DataExportOrderItem(item.Sku, item.ProductName, item.ColorName, item.SizeName, item.UnitPrice, item.Quantity)).ToList(),
                    history[order.Id].Select(change => new DataExportOrderStatus(change.ToStatus, Utc(change.ChangedAt))).ToList(),
                    payments[order.Id]
                        .Select(payment => new DataExportPayment(
                            payment.Method, payment.Status, payment.Amount, payment.Installments, Utc(payment.CreatedAt),
                            payment.ApprovedAt is { } approved ? Utc(approved) : null))
                        .ToList()))
                .ToList(),
            ExportNotes);
    }

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private sealed class AccountRow
    {
        public string FullName { get; init; } = string.Empty;
        public string Email { get; init; } = string.Empty;
        public string? Phone { get; init; }
        public string? Cpf { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime? EmailConfirmedAt { get; init; }
    }

    private sealed class AddressRow
    {
        public string Recipient { get; init; } = string.Empty;
        public string PostalCode { get; init; } = string.Empty;
        public string Street { get; init; } = string.Empty;
        public string Number { get; init; } = string.Empty;
        public string? Complement { get; init; }
        public string District { get; init; } = string.Empty;
        public string City { get; init; } = string.Empty;
        public string State { get; init; } = string.Empty;
        public bool IsDefault { get; init; }
        public DateTime CreatedAt { get; init; }
    }

    private sealed class OrderRow
    {
        public Guid Id { get; init; }
        public long Number { get; init; }
        public string Status { get; init; } = string.Empty;
        public DateTime PlacedAt { get; init; }
        public decimal Subtotal { get; init; }
        public decimal ShippingFee { get; init; }
        public decimal GiftWrapPrice { get; init; }
        public decimal Total { get; init; }
        public string ShippingServiceName { get; init; } = string.Empty;
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
        public string? GiftMessage { get; init; }
        public string? Notes { get; init; }
    }

    private sealed class ItemRow
    {
        public Guid OrderId { get; init; }
        public string Sku { get; init; } = string.Empty;
        public string ProductName { get; init; } = string.Empty;
        public string? ColorName { get; init; }
        public string? SizeName { get; init; }
        public decimal UnitPrice { get; init; }
        public int Quantity { get; init; }
    }

    private sealed class HistoryRow
    {
        public Guid OrderId { get; init; }
        public string ToStatus { get; init; } = string.Empty;
        public DateTime ChangedAt { get; init; }
    }

    private sealed class PaymentRow
    {
        public Guid OrderId { get; init; }
        public string Method { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public decimal Amount { get; init; }
        public short? Installments { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime? ApprovedAt { get; init; }
    }
}

/// <summary>
/// Exclusão da conta pela cliente (P7). Pede a senha atual; recusa com pedido entre o pagamento e a entrega ou com
/// estorno pendente. Senão, apaga tudo o que identifica a pessoa e mantém o que a lei manda guardar (valores, peças e
/// datas dos pedidos).
/// </summary>
public sealed record DeleteMyAccountCommand(UserId UserId, string? Password) : ICommand;

internal sealed class DeleteMyAccountValidator : AbstractValidator<DeleteMyAccountCommand>
{
    public DeleteMyAccountValidator() => RuleFor(x => x.Password).NotEmpty().WithMessage("Informe sua senha para confirmar.");
}

/// <summary>O que aparece no lugar do nome, no painel e nos pedidos que ficam.</summary>
public static class RemovedCustomer
{
    public const string Name = "Cliente removida";
    public const string Text = "Removido";
}

internal sealed class DeleteMyAccountHandler(
    IUserRepository users,
    IRefreshTokenRepository refreshTokens,
    IPasswordHasher passwordHasher,
    IUnitOfWork unitOfWork,
    TimeProvider time) : ICommandHandler<DeleteMyAccountCommand>
{
    // Aguardando pagamento também conta: o PIX pode ser pago depois e o pedido seguiria sem destinatário.
    private const string BlockersSql = """
        SELECT EXISTS (SELECT 1 FROM ordering.orders
                        WHERE customer_id = @Id AND status IN ('PendingPayment','PaymentConfirmed','Preparing','Shipped')) AS has_open_order,
               EXISTS (SELECT 1 FROM payments.payments p JOIN ordering.orders o ON o.id = p.order_id
                        WHERE o.customer_id = @Id
                          AND (p.status = 'RefundFailed' OR (p.status = 'Approved' AND o.status IN ('Cancelled','Returned')))) AS has_pending_refund
        """;

    // O pedido fica com valores, peças, datas, cidade e UF (estatística e obrigação fiscal), sem nada que identifique a
    // pessoa. As notas do histórico são da loja e ficam. Sacola, endereços e links de e-mail somem.
    private const string AnonymizeSql = """
        UPDATE customers.customers SET full_name = @Name, phone = NULL, cpf = NULL, updated_at = @Now WHERE id = @Id;
        DELETE FROM customers.addresses WHERE customer_id = @Id;
        DELETE FROM carts.carts WHERE customer_id = @Id;
        UPDATE identity.user_tokens SET used_at = @Now WHERE user_id = @Id AND used_at IS NULL;
        UPDATE ordering.orders
           SET ship_recipient = @Name, ship_postal_code = '00000000', ship_street = @Text, ship_number = '-',
               ship_complement = NULL, ship_district = @Text, gift_message = NULL, notes = NULL, version = version + 1
         WHERE customer_id = @Id;
        """;

    public async Task<Result> Handle(DeleteMyAccountCommand command, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var user = await users.GetByIdAsync(command.UserId, cancellationToken);
        if (user is null || !user.IsActive || user.IsAnonymized || user.Role != Role.Customer)
        {
            return Result.Failure(IdentityErrors.UserNotFound);
        }

        if (passwordHasher.Verify(user.PasswordHash, command.Password!) == PasswordCheck.Failed)
        {
            return Result.Failure(IdentityErrors.WrongCurrentPassword);
        }

        var blockers = await unitOfWork.Connection.QuerySingleAsync<(bool HasOpenOrder, bool HasPendingRefund)>(
            new CommandDefinition(BlockersSql, new { Id = user.Id.Value }, unitOfWork.Transaction, cancellationToken: cancellationToken));
        if (blockers.HasOpenOrder)
        {
            return Result.Failure(IdentityErrors.AccountHasOpenOrders);
        }

        if (blockers.HasPendingRefund)
        {
            return Result.Failure(IdentityErrors.AccountHasPendingRefund);
        }

        user.Anonymize(now);
        if (!await users.UpdateAsync(user, cancellationToken))
        {
            return Result.Failure(IdentityErrors.ConcurrencyConflict);
        }

        await refreshTokens.RevokeAllForUserAsync(user.Id, now, cancellationToken);
        await unitOfWork.Connection.ExecuteAsync(new CommandDefinition(
            AnonymizeSql,
            new { Id = user.Id.Value, Now = now, RemovedCustomer.Name, RemovedCustomer.Text },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));
        return Result.Success();
    }
}
