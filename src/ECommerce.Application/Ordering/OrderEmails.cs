using System.Globalization;
using System.Text.Json;
using Dapper;
using ECommerce.Application.Abstractions.Data;
using ECommerce.Application.Abstractions.Outbox;
using ECommerce.Application.Notifications;
using ECommerce.Domain.Ordering;

namespace ECommerce.Application.Ordering;

/// <summary>Uma linha do resumo do pedido nos e-mails.</summary>
public sealed record OrderEmailLine(string Name, string? Detail, int Quantity, decimal Total);

/// <summary>Valores em reais como a cliente lê: "R$ 1.234,56".</summary>
public static class MoneyFormat
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public static string Brl(decimal value) => value.ToString("C", PtBr);
}

/// <summary>
/// E-mails do pedido (arquivo 23): o modelo leva tudo o que o template mostra, já formatado, porque o pedido pode mudar
/// entre a gravação no Outbox e o envio. A dedicatória vai no e-mail (RF-047 CA4), nunca em log.
/// </summary>
internal static class OrderEmails
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    /// <summary>Aviso à loja (EM-07): mesmo resumo, endereçado ao e-mail da loja.</summary>
    public static Task EnqueueStoreAsync(IUnitOfWork unitOfWork, IOutbox outbox, string template, Order order, CancellationToken cancellationToken) =>
        EnqueueAsync(unitOfWork, outbox, template, order, cancellationToken, toStore: true);

    /// <summary>Pagamento que precisa de ação da loja (EM-12): estorno ou contestação.</summary>
    public static Task EnqueueStoreAlertAsync(IUnitOfWork unitOfWork, IOutbox outbox, Order order, string problem, CancellationToken cancellationToken) =>
        EnqueueAsync(unitOfWork, outbox, EmailTemplates.PaymentNeedsAttention, order, cancellationToken, toStore: true, problem: problem);

    public static async Task EnqueueAsync(
        IUnitOfWork unitOfWork,
        IOutbox outbox,
        string template,
        Order order,
        CancellationToken cancellationToken,
        bool toStore = false,
        string? problem = null,
        bool refund = false)
    {
        var customer = await unitOfWork.Connection.QuerySingleAsync<CustomerRow>(new CommandDefinition(
            """
            SELECT u.email::text AS email, c.full_name
              FROM identity.users u JOIN customers.customers c ON c.id = u.id
             WHERE u.id = @Id
            """,
            new { Id = order.CustomerId.Value },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));

        var lines = order.Items
            .Select(item => new OrderEmailLine(
                item.ProductName,
                string.Join(" · ", new[] { item.ColorName, item.SizeName }.Where(part => !string.IsNullOrWhiteSpace(part))) is { Length: > 0 } detail ? detail : null,
                item.Quantity,
                item.LineTotal))
            .ToList();

        var model = new Dictionary<string, string>
        {
            ["name"] = customer.FullName.Split(' ')[0],
            ["orderId"] = order.Id.ToString(),
            ["orderNumber"] = order.Code,
            ["items"] = JsonSerializer.Serialize(lines, JsonSerializerOptions.Web),
            ["subtotal"] = Money(order.Subtotal),
            ["shipping"] = $"{order.Shipping.ServiceName}: {(order.Shipping.Price == 0 ? "grátis" : Money(order.Shipping.Price))}",
            ["giftWrap"] = order.Gift.Wrap ? (order.GiftWrapPrice == 0 ? "grátis" : Money(order.GiftWrapPrice)) : string.Empty,
            ["giftMessage"] = order.Gift.Message ?? string.Empty,
            ["total"] = Money(order.Total),
            ["dueAt"] = LocalTime(order.PaymentDueAtUtc),
            ["address"] = $"{order.Address.Recipient}, {order.Address.Street}, {order.Address.Number}" +
                (order.Address.Complement is null ? string.Empty : $" ({order.Address.Complement})") +
                $", {order.Address.District}, {order.Address.City}/{order.Address.State}",
            ["cancellationReason"] = order.CancellationReason switch
            {
                CancellationReason.CustomerRequest => "a seu pedido",
                CancellationReason.PaymentTimeout => "porque o pagamento não foi confirmado em 48 horas",
                CancellationReason.AdminRequest => "pela loja",
                CancellationReason.PaymentRejected => "porque o pagamento foi recusado",
                _ => string.Empty,
            },
            ["customerName"] = customer.FullName,
            ["customerEmail"] = customer.Email,
            ["problem"] = problem ?? string.Empty,
            ["refund"] = refund ? "true" : "false",
        };

        await outbox.EnqueueAsync(
            SendEmail.Type,
            new SendEmail(template, toStore ? SendEmail.StoreRecipient : customer.Email, order.Id, model, new Dictionary<string, string>()),
            cancellationToken);
    }

    public static string Money(decimal value) => MoneyFormat.Brl(value);

    /// <summary>Horário de Brasília, como a cliente lê: "10/11/2026 às 14:30".</summary>
    public static string LocalTime(DateTime utc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), SaoPaulo);
        return local.ToString("dd/MM/yyyy 'às' HH:mm", PtBr);
    }

    private static readonly TimeZoneInfo SaoPaulo = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    private sealed class CustomerRow
    {
        public string Email { get; init; } = string.Empty;
        public string FullName { get; init; } = string.Empty;
    }
}
