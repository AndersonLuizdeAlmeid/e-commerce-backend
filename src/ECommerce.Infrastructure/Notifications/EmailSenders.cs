using System.Net.Http.Headers;
using System.Net.Http.Json;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace ECommerce.Infrastructure.Notifications;

public sealed record EmailMessage(string To, string Subject, string HtmlBody, string TextBody);

/// <summary>Porta de envio (ADR-010). Devolve o id do provedor, quando houver.</summary>
public interface IEmailSender
{
    Task<string?> SendAsync(EmailMessage message, CancellationToken cancellationToken);
}

/// <summary>Seção <c>Email</c> (arquivo 23).</summary>
public sealed class EmailSettings
{
    public const string SectionName = "Email";

    /// <summary><c>Smtp</c> (Mailpit em dev) ou <c>Resend</c> (produção, arquivo 20 D04).</summary>
    public string Provider { get; init; } = "Smtp";

    public string FromAddress { get; init; } = "nao-responda@loja.local";
    public string FromName { get; init; } = "Loja";
    public string? ReplyTo { get; init; }

    /// <summary>Base dos links dos e-mails (confirmar e-mail, redefinir senha).</summary>
    public string FrontendBaseUrl { get; init; } = "http://localhost:3000";

    public SmtpSettings Smtp { get; init; } = new();
    public ResendSettings Resend { get; init; } = new();

    public sealed class SmtpSettings
    {
        public string Host { get; init; } = "localhost";
        public int Port { get; init; } = 1025;
        public bool UseStartTls { get; init; }
        public string? UserName { get; init; }
        public string? Password { get; init; }
    }

    public sealed class ResendSettings
    {
        public string BaseUrl { get; init; } = "https://api.resend.com/";
        public string ApiKey { get; init; } = string.Empty;
    }
}

/// <summary>SMTP com MailKit — em desenvolvimento aponta para o Mailpit, que só captura (nada sai de verdade).</summary>
internal sealed class SmtpEmailSender(IOptions<EmailSettings> options) : IEmailSender
{
    public async Task<string?> SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        using var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(settings.FromName, settings.FromAddress));
        mime.To.Add(MailboxAddress.Parse(message.To));
        if (!string.IsNullOrWhiteSpace(settings.ReplyTo))
        {
            mime.ReplyTo.Add(MailboxAddress.Parse(settings.ReplyTo));
        }

        mime.Subject = message.Subject;
        mime.Body = new BodyBuilder { HtmlBody = message.HtmlBody, TextBody = message.TextBody }.ToMessageBody();

        using var client = new SmtpClient();
        var security = settings.Smtp.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.None;
        await client.ConnectAsync(settings.Smtp.Host, settings.Smtp.Port, security, cancellationToken);
        if (!string.IsNullOrEmpty(settings.Smtp.UserName))
        {
            await client.AuthenticateAsync(settings.Smtp.UserName, settings.Smtp.Password ?? string.Empty, cancellationToken);
        }

        await client.SendAsync(mime, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);

        return mime.MessageId;
    }
}

/// <summary>API HTTP do Resend (arquivo 20, D04). Exige domínio verificado.</summary>
internal sealed class ResendEmailSender(HttpClient httpClient, IOptions<EmailSettings> options) : IEmailSender
{
    public async Task<string?> SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        using var request = new HttpRequestMessage(HttpMethod.Post, "emails")
        {
            Content = JsonContent.Create(new
            {
                from = $"{settings.FromName} <{settings.FromAddress}>",
                to = new[] { message.To },
                subject = message.Subject,
                html = message.HtmlBody,
                text = message.TextBody,
                reply_to = settings.ReplyTo,
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.Resend.ApiKey);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Resend respondeu {(int)response.StatusCode}: {body}");
        }

        var result = await response.Content.ReadFromJsonAsync<ResendResponse>(cancellationToken);
        return result?.Id;
    }

    private sealed record ResendResponse(string? Id);
}
