using System.Globalization;
using ECommerce.Domain.Abstractions;
using ECommerce.Domain.Catalog;
using ECommerce.Domain.Identity;
using ECommerce.Domain.Shipping;

namespace ECommerce.Domain.Ordering;

public static class OrderErrors
{
    public static readonly Error OrderNotFound = Error.NotFound("Ordering.OrderNotFound", "Pedido não encontrado.");

    public static readonly Error EmptyCart = Error.BusinessRule("Ordering.EmptyCart", "Sua sacola está vazia.");

    /// <summary>D05: comprar exige e-mail confirmado.</summary>
    public static readonly Error EmailNotConfirmed = Error.BusinessRule(
        "Identity.EmailNotConfirmed", "Confirme seu e-mail para finalizar a compra. Reenvie o link em Minha conta.");

    public static readonly Error AddressNotFound =
        Error.BusinessRule("Ordering.AddressNotFound", "Escolha um endereço de entrega guardado na sua conta.");

    public static readonly Error ShippingServiceUnavailable = Error.Conflict(
        "Ordering.ShippingServiceUnavailable", "Este frete não está mais disponível para o endereço. Escolha o frete de novo.");

    public static readonly Error InvalidGiftMessage = Error.Validation(
        "Ordering.InvalidGiftMessage", $"A dedicatória tem até {OrderGift.MessageMaxLength} caracteres e {OrderGift.MessageMaxLines} linhas.");

    public static readonly Error InvalidNotes =
        Error.Validation("Ordering.InvalidNotes", $"A observação tem até {Order.NotesMaxLength} caracteres.");

    public static readonly Error InvalidTransition =
        Error.BusinessRule("Ordering.InvalidTransition", "O pedido não pode mudar para esta situação agora.");

    public static readonly Error CannotCancel =
        Error.BusinessRule("Ordering.CannotCancel", "Este pedido não pode mais ser cancelado pelo site. Fale com a MO.");

    public static readonly Error InvalidGiftWrapPrice =
        Error.Validation("Ordering.InvalidGiftWrapPrice", "Informe um valor de embalagem entre R$ 0 e R$ 1.000.");
}

/// <summary>Peças sem saldo no momento da compra (RF-040 CA2): a resposta lista cada uma.</summary>
public sealed record OutOfStockItem(Guid VariantId, string ProductName, int Requested, int Available);

public sealed record OutOfStockError(IReadOnlyList<OutOfStockItem> Items) : Error(
    "Ordering.OutOfStock",
    "Algumas peças acabaram enquanto você comprava: " + string.Join(", ", Items.Select(item => item.ProductName)) + ".",
    ErrorType.Conflict);

public enum OrderStatus
{
    PendingPayment,
    PaymentConfirmed,
    Preparing,
    Shipped,
    Delivered,
    Cancelled,
}

public enum CancellationReason
{
    CustomerRequest,
    AdminRequest,
    PaymentTimeout,
    PaymentRejected,
}

/// <summary>Cópia da peça no momento da compra (RF-040 CA4): mudar o catálogo depois não muda o pedido.</summary>
public sealed record OrderItem(
    VariantId VariantId,
    ProductId ProductId,
    string Sku,
    string ProductName,
    string? ColorName,
    string? SizeName,
    string? ImageUrl,
    decimal UnitPrice,
    int Quantity)
{
    public decimal LineTotal => UnitPrice * Quantity;
}

/// <summary>Cópia do endereço de entrega (arquivo 04 §3, <c>ShippingAddress</c>).</summary>
public sealed record ShippingAddress(
    string Recipient,
    PostalCode PostalCode,
    string Street,
    string Number,
    string? Complement,
    string District,
    string City,
    string State);

/// <summary>Frete escolhido (arquivo 04 §3, <c>ShippingSelection</c>). Sem prazo quando é o valor de contingência.</summary>
public sealed record ShippingSelection(string ServiceCode, string ServiceName, decimal Price, int? DeliveryDays, bool IsFallback);

/// <summary>Presente (RF-047): embalagem e dedicatória só existem quando o pedido é presente.</summary>
public sealed record OrderGift(bool IsGift, bool Wrap, string? Message)
{
    public const int MessageMaxLength = 200;
    public const int MessageMaxLines = 6;

    public static readonly OrderGift None = new(false, false, null);

    /// <summary>Texto puro (CA1): quebras de linha normalizadas e espaços nas pontas fora. Vazio vira nulo.</summary>
    public static Result<OrderGift> Create(bool isGift, bool wrap, string? message)
    {
        if (!isGift)
        {
            return None;
        }

        var text = (message ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
        if (text.Length > MessageMaxLength || text.Split('\n').Length > MessageMaxLines || text.Any(c => char.IsControl(c) && c != '\n'))
        {
            return OrderErrors.InvalidGiftMessage;
        }

        return new OrderGift(true, wrap, text.Length == 0 ? null : text);
    }
}

public sealed record OrderStatusChange(Guid Id, OrderStatus? From, OrderStatus To, UserId? ChangedBy, DateTime ChangedAtUtc, string? Note);

/// <summary>
/// Pedido (arquivo 04 §4.8). O total é sempre calculado aqui (RN-051) e as transições seguem a máquina de estados;
/// cada uma gera uma linha de histórico. <see cref="Number"/> sai da sequência do banco na gravação.
/// </summary>
public sealed class Order
{
    public const int NotesMaxLength = 500;
    public static readonly TimeSpan PaymentWindow = TimeSpan.FromHours(48);
    public static readonly TimeSpan ReminderAfter = TimeSpan.FromHours(24);

    private readonly List<OrderItem> _items;
    private readonly List<OrderStatusChange> _newHistory = [];

    private Order(
        Guid id,
        long number,
        UserId customerId,
        List<OrderItem> items,
        ShippingAddress address,
        ShippingSelection shipping,
        OrderGift gift,
        decimal giftWrapPrice,
        string? notes,
        OrderStatus status,
        CancellationReason? cancellationReason,
        DateTime placedAtUtc,
        DateTime paymentDueAtUtc,
        DateTime? paymentReminderSentAtUtc,
        DateTime updatedAtUtc,
        int version)
    {
        Id = id;
        Number = number;
        CustomerId = customerId;
        _items = items;
        Address = address;
        Shipping = shipping;
        Gift = gift;
        GiftWrapPrice = giftWrapPrice;
        Notes = notes;
        Status = status;
        CancellationReason = cancellationReason;
        PlacedAtUtc = placedAtUtc;
        PaymentDueAtUtc = paymentDueAtUtc;
        PaymentReminderSentAtUtc = paymentReminderSentAtUtc;
        UpdatedAtUtc = updatedAtUtc;
        Version = version;
    }

    public Guid Id { get; }

    public long Number { get; private set; }

    public string Code => FormatNumber(Number);

    public UserId CustomerId { get; }

    public IReadOnlyList<OrderItem> Items => _items;

    public ShippingAddress Address { get; }

    public ShippingSelection Shipping { get; }

    public OrderGift Gift { get; }

    /// <summary>Valor da embalagem congelado no checkout (RF-047 CA2); zero quando não pediu embalagem.</summary>
    public decimal GiftWrapPrice { get; }

    public string? Notes { get; }

    public OrderStatus Status { get; private set; }

    public CancellationReason? CancellationReason { get; private set; }

    public DateTime PlacedAtUtc { get; }

    public DateTime PaymentDueAtUtc { get; }

    public DateTime? PaymentReminderSentAtUtc { get; private set; }

    public DateTime UpdatedAtUtc { get; private set; }

    /// <summary>Versão lida do banco; a gravação só passa se ninguém mudou o pedido no meio (concorrência otimista).</summary>
    public int Version { get; }

    public decimal Subtotal => _items.Sum(item => item.LineTotal);

    public decimal Total => Subtotal + Shipping.Price + GiftWrapPrice;

    public IReadOnlyList<OrderStatusChange> NewHistory => _newHistory;

    /// <summary>"PED-000123" (RF-040 CA6).</summary>
    public static string FormatNumber(long number) => string.Create(CultureInfo.InvariantCulture, $"PED-{number:D6}");

    public static Result<Order> Place(
        UserId customerId,
        IReadOnlyList<OrderItem> items,
        ShippingAddress address,
        ShippingSelection shipping,
        OrderGift gift,
        decimal currentGiftWrapPrice,
        string? notes,
        DateTime nowUtc)
    {
        if (items.Count == 0)
        {
            return OrderErrors.EmptyCart;
        }

        var cleanNotes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        if (cleanNotes is { Length: > NotesMaxLength })
        {
            return OrderErrors.InvalidNotes;
        }

        var order = new Order(
            Guid.CreateVersion7(),
            0,
            customerId,
            items.ToList(),
            address,
            shipping,
            gift,
            gift.Wrap ? currentGiftWrapPrice : 0m,
            cleanNotes,
            OrderStatus.PendingPayment,
            null,
            nowUtc,
            nowUtc + PaymentWindow,
            null,
            nowUtc,
            0);
        order._newHistory.Add(new OrderStatusChange(Guid.CreateVersion7(), null, OrderStatus.PendingPayment, customerId, nowUtc, null));
        return order;
    }

    /// <summary>Uso exclusivo da Infrastructure.</summary>
    public static Order Rehydrate(
        Guid id,
        long number,
        UserId customerId,
        IEnumerable<OrderItem> items,
        ShippingAddress address,
        ShippingSelection shipping,
        OrderGift gift,
        decimal giftWrapPrice,
        string? notes,
        OrderStatus status,
        CancellationReason? cancellationReason,
        DateTime placedAtUtc,
        DateTime paymentDueAtUtc,
        DateTime? paymentReminderSentAtUtc,
        DateTime updatedAtUtc,
        int version) =>
        new(id, number, customerId, items.ToList(), address, shipping, gift, giftWrapPrice, notes, status, cancellationReason,
            placedAtUtc, paymentDueAtUtc, paymentReminderSentAtUtc, updatedAtUtc, version);

    /// <summary>Uso exclusivo da Infrastructure: o número vem da sequência na gravação.</summary>
    public void AssignNumber(long number) => Number = number;

    /// <summary>
    /// Cancelamento (RF-043, RF-046). Permitido em aguardando pagamento e pagamento confirmado; quem chama devolve o
    /// estoque e, se já pago, pede o estorno (arquivo 21 §5.4).
    /// </summary>
    public Result Cancel(CancellationReason reason, UserId? by, DateTime nowUtc, string? note = null)
    {
        if (Status is not (OrderStatus.PendingPayment or OrderStatus.PaymentConfirmed or OrderStatus.Preparing))
        {
            return Result.Failure(OrderErrors.InvalidTransition);
        }

        if (Status == OrderStatus.Preparing && reason != Ordering.CancellationReason.AdminRequest)
        {
            return Result.Failure(OrderErrors.InvalidTransition);
        }

        CancellationReason = reason;
        return MoveTo(OrderStatus.Cancelled, by, nowUtc, note);
    }

    /// <summary>Pagamento aprovado (RF-045). Só sai de aguardando pagamento; o caso do pedido já cancelado é estorno (RN-101).</summary>
    public Result ConfirmPayment(UserId? by, DateTime nowUtc, string? note = null) =>
        Status == OrderStatus.PendingPayment
            ? MoveTo(OrderStatus.PaymentConfirmed, by, nowUtc, note)
            : Result.Failure(OrderErrors.InvalidTransition);

    /// <summary>Lembrete de 24 h (RF-046 CA2): uma vez só.</summary>
    public bool MarkReminderSent(DateTime nowUtc)
    {
        if (Status != OrderStatus.PendingPayment || PaymentReminderSentAtUtc is not null)
        {
            return false;
        }

        PaymentReminderSentAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
        return true;
    }

    public bool IsPaymentOverdue(DateTime nowUtc) => Status == OrderStatus.PendingPayment && nowUtc >= PaymentDueAtUtc;

    private Result MoveTo(OrderStatus status, UserId? by, DateTime nowUtc, string? note)
    {
        _newHistory.Add(new OrderStatusChange(Guid.CreateVersion7(), Status, status, by, nowUtc, note));
        Status = status;
        UpdatedAtUtc = nowUtc;
        return Result.Success();
    }
}

public interface IOrderRepository
{
    /// <summary>Grava pedido, itens e histórico; preenche o número que a sequência deu.</summary>
    Task AddAsync(Order order, string? idempotencyKey, CancellationToken cancellationToken);

    /// <summary>Trava o pedido até o fim da transação. <paramref name="customerId"/> nulo: qualquer cliente (jobs).</summary>
    Task<Order?> GetForUpdateAsync(Guid orderId, UserId? customerId, CancellationToken cancellationToken);

    /// <summary>Concorrência otimista pela versão lida (arquivo 25 §2): falso se outro processo mudou o pedido antes.</summary>
    Task<bool> UpdateAsync(Order order, CancellationToken cancellationToken);

    Task<Guid?> FindByIdempotencyKeyAsync(UserId customerId, string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>Pedidos vencidos, travados e pulando os que outra instância já pegou (SKIP LOCKED).</summary>
    Task<IReadOnlyList<Order>> LockOverdueAsync(DateTime nowUtc, int limit, CancellationToken cancellationToken);

    /// <summary>Pedidos com 24 h sem pagamento e sem lembrete, travados com SKIP LOCKED.</summary>
    Task<IReadOnlyList<Order>> LockDueForReminderAsync(DateTime nowUtc, int limit, CancellationToken cancellationToken);

    Task<decimal> GetGiftWrapPriceAsync(CancellationToken cancellationToken);

    Task SaveGiftWrapPriceAsync(decimal price, DateTime nowUtc, CancellationToken cancellationToken);
}
