namespace ECommerce.Application.Notifications;

/// <summary>Catálogo de e-mails (arquivo 23 §3). Só os de conta existem na Fase 2A.</summary>
public static class EmailTemplates
{
    public const string WelcomeAndConfirmation = "EM-01";
    public const string PasswordReset = "EM-02";
    public const string PasswordChanged = "EM-03";
}

/// <summary>
/// Mensagem do Outbox <see cref="Type"/>. <paramref name="Secrets"/> (tokens dos links) são apagados do payload
/// depois do envio, para não ficarem no banco.
/// </summary>
public sealed record SendEmail(
    string Template,
    string To,
    Guid? RelatedEntityId,
    IReadOnlyDictionary<string, string> Model,
    IReadOnlyDictionary<string, string> Secrets)
{
    public const string Type = "notifications.send-email";
}
