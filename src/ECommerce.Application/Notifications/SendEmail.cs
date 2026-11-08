namespace ECommerce.Application.Notifications;

/// <summary>Catálogo de e-mails (arquivo 23 §3): os de conta (Fase 2A) e os de pedido (Fase 4).</summary>
public static class EmailTemplates
{
    public const string WelcomeAndConfirmation = "EM-01";
    public const string PasswordReset = "EM-02";
    public const string PasswordChanged = "EM-03";
    public const string OrderPlaced = "EM-04";
    public const string PaymentReminder = "EM-05";
    public const string PaymentConfirmed = "EM-06";
    public const string NewPaidOrder = "EM-07";
    public const string OrderCancelled = "EM-10";

    /// <summary>Aviso à loja de pagamento que precisa de atenção (estorno, contestação).</summary>
    public const string PaymentNeedsAttention = "EM-12";
}

/// <summary>
/// Mensagem do Outbox <see cref="Type"/>. <paramref name="Secrets"/> (tokens dos links) são apagados do payload
/// depois do envio, para não ficarem no banco.
/// </summary>
/// <remarks><paramref name="To"/> igual a <see cref="StoreRecipient"/> vai para o e-mail da loja (<c>Email:StoreAddress</c>).</remarks>
public sealed record SendEmail(
    string Template,
    string To,
    Guid? RelatedEntityId,
    IReadOnlyDictionary<string, string> Model,
    IReadOnlyDictionary<string, string> Secrets)
{
    public const string Type = "notifications.send-email";

    /// <summary>Destinatário simbólico: o endereço da loja só existe na configuração da Infrastructure (entrega 4 §3, P12).</summary>
    public const string StoreRecipient = "@loja";
}
