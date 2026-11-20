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

    public static readonly Error InvalidTrackingCode = Error.Validation(
        "Ordering.InvalidTrackingCode", $"O código de rastreio tem de {TrackingCode.MinLength} a {TrackingCode.MaxLength} letras e números, sem espaços.");

    public static readonly Error NotAGift = Error.BusinessRule("Ordering.NotAGift", "Este pedido não é presente: não tem dedicatória.");

    public static readonly Error GiftMessageLocked =
        Error.BusinessRule("Ordering.GiftMessageLocked", "A dedicatória só pode ser corrigida até o pedido ser enviado.");

    public static readonly Error ReturnNoteRequired =
        Error.Validation("Ordering.ReturnNoteRequired", $"Conte o motivo da devolução, em até {Order.ReturnNoteMaxLength} caracteres.");
}

/// <summary>
/// Código de rastreio (entrega 5 §3, P3): Correios usam <c>AA123456789BR</c>, outras transportadoras do Melhor Envio
/// usam outros formatos. Aceita letras e números, gravado em maiúsculas.
/// </summary>
public static class TrackingCode
{
    public const int MinLength = 8;
    public const int MaxLength = 40;

    public static Result<string> Normalize(string? value)
    {
        var code = (value ?? string.Empty).Trim().ToUpperInvariant();
        return code.Length is >= MinLength and <= MaxLength && code.All(char.IsAsciiLetterOrDigit)
            ? code
            : OrderErrors.InvalidTrackingCode;
    }
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

    /// <summary>Devolvido depois de entregue (arrependimento ou troca), com estorno total (entrega 6 §3, P11).</summary>
    Returned,
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
    public const int ReturnNoteMaxLength = 500;
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
        int version,
        string? trackingCode)
    {
        Id = id;
        TrackingCode = trackingCode;
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

    public OrderGift Gift { get; private set; }

    /// <summary>Valor da embalagem congelado no checkout (RF-047 CA2); zero quando não pediu embalagem.</summary>
    public decimal GiftWrapPrice { get; }

    public string? Notes { get; }

    public OrderStatus Status { get; private set; }

    public CancellationReason? CancellationReason { get; private set; }

    /// <summary>Obrigatório a partir de enviado (RF-053 CA3).</summary>
    public string? TrackingCode { get; private set; }

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
            0,
            null);
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
        int version,
        string? trackingCode = null) =>
        new(id, number, customerId, items.ToList(), address, shipping, gift, giftWrapPrice, notes, status, cancellationReason,
            placedAtUtc, paymentDueAtUtc, paymentReminderSentAtUtc, updatedAtUtc, version, trackingCode);

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

    /// <summary>A loja começa a separar (arquivo 04 §4.8): só de pagamento confirmado.</summary>
    public Result StartPreparation(UserId by, DateTime nowUtc) =>
        Status == OrderStatus.PaymentConfirmed
            ? MoveTo(OrderStatus.Preparing, by, nowUtc, null)
            : Result.Failure(OrderErrors.InvalidTransition);

    /// <summary>Enviado (RF-053 CA3): só de em separação, e sempre com o código de rastreio.</summary>
    public Result Ship(string? trackingCode, UserId by, DateTime nowUtc)
    {
        if (Status != OrderStatus.Preparing)
        {
            return Result.Failure(OrderErrors.InvalidTransition);
        }

        var code = Ordering.TrackingCode.Normalize(trackingCode);
        if (code.IsFailure)
        {
            return Result.Failure(code.Error);
        }

        TrackingCode = code.Value;
        return MoveTo(OrderStatus.Shipped, by, nowUtc, $"Rastreio {code.Value}");
    }

    /// <summary>Corrige o rastreio de pedido já enviado (entrega 5 §3, P3): fica no histórico, sem novo e-mail.</summary>
    public Result UpdateTrackingCode(string? trackingCode, UserId by, DateTime nowUtc)
    {
        if (Status != OrderStatus.Shipped)
        {
            return Result.Failure(OrderErrors.InvalidTransition);
        }

        var code = Ordering.TrackingCode.Normalize(trackingCode);
        if (code.IsFailure)
        {
            return Result.Failure(code.Error);
        }

        if (code.Value == TrackingCode)
        {
            return Result.Success();
        }

        var previous = TrackingCode;
        TrackingCode = code.Value;
        return Record(by, nowUtc, $"Rastreio corrigido: {previous} → {code.Value}");
    }

    /// <summary>Entregue: só de enviado.</summary>
    public Result Deliver(UserId by, DateTime nowUtc) =>
        Status == OrderStatus.Shipped
            ? MoveTo(OrderStatus.Delivered, by, nowUtc, null)
            : Result.Failure(OrderErrors.InvalidTransition);

    /// <summary>
    /// Devolução (entrega 6 §3, P11): só de entregue, com o motivo no histórico. Quem chama pede o estorno total; o estoque
    /// não volta sozinho, porque a peça é conferida antes. O prazo de 7 dias do arrependimento não é travado aqui: quem
    /// decide é a loja.
    /// </summary>
    public Result MarkReturned(string? note, UserId by, DateTime nowUtc)
    {
        if (Status != OrderStatus.Delivered)
        {
            return Result.Failure(OrderErrors.InvalidTransition);
        }

        var text = note?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > ReturnNoteMaxLength)
        {
            return Result.Failure(OrderErrors.ReturnNoteRequired);
        }

        return MoveTo(OrderStatus.Returned, by, nowUtc, text);
    }

    /// <summary>
    /// RF-047 CA3: a loja corrige a dedicatória até o pedido ser enviado. O texto não vai para o histórico (CA4): a nota
    /// diz só que mudou.
    /// </summary>
    public Result CorrectGiftMessage(string? message, UserId by, DateTime nowUtc)
    {
        if (!Gift.IsGift)
        {
            return Result.Failure(OrderErrors.NotAGift);
        }

        if (Status is OrderStatus.Shipped or OrderStatus.Delivered or OrderStatus.Cancelled or OrderStatus.Returned)
        {
            return Result.Failure(OrderErrors.GiftMessageLocked);
        }

        var gift = OrderGift.Create(true, Gift.Wrap, message);
        if (gift.IsFailure)
        {
            return Result.Failure(gift.Error);
        }

        if (gift.Value.Message == Gift.Message)
        {
            return Result.Success();
        }

        Gift = gift.Value;
        return Record(by, nowUtc, "Dedicatória corrigida");
    }

    /// <summary>Mudança sem troca de situação (rastreio, dedicatória): uma linha no histórico com a mesma situação.</summary>
    private Result Record(UserId by, DateTime nowUtc, string note)
    {
        _newHistory.Add(new OrderStatusChange(Guid.CreateVersion7(), Status, Status, by, nowUtc, note));
        UpdatedAtUtc = nowUtc;
        return Result.Success();
    }

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
