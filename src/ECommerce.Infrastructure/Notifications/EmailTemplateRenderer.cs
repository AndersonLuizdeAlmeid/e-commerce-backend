using System.Net;
using ECommerce.Application.Notifications;
using Microsoft.Extensions.Options;

namespace ECommerce.Infrastructure.Notifications;

/// <summary>
/// Templates EM-01..EM-03 em pt-BR, HTML simples com estilos inline + versão texto (arquivo 23 §4).
/// Todo valor vindo do usuário passa por HtmlEncode; tokens vão só no link.
/// </summary>
internal sealed class EmailTemplateRenderer(IOptions<EmailSettings> options)
{
    public EmailMessage Render(SendEmail email)
    {
        var name = Value(email.Model, "name");
        var baseUrl = options.Value.FrontendBaseUrl.TrimEnd('/');
        var token = Value(email.Secrets, "token");

        return email.Template switch
        {
            EmailTemplates.WelcomeAndConfirmation => Build(
                email.To,
                Value(email.Model, "welcome") == "true" ? "Bem-vindo(a)! Confirme seu e-mail" : "Confirme seu e-mail",
                Value(email.Model, "welcome") == "true"
                    ? $"Olá, {name}! Sua conta foi criada. Para finalizar compras, confirme seu e-mail pelo botão abaixo."
                    : $"Olá, {name}! Use o botão abaixo para confirmar seu e-mail.",
                "Confirmar e-mail",
                $"{baseUrl}/confirmar-email?token={Uri.EscapeDataString(token)}",
                "O link vale por 24 horas. Se você não criou esta conta, ignore este e-mail."),

            EmailTemplates.PasswordReset => Build(
                email.To,
                "Redefinição de senha",
                $"Olá, {name}! Recebemos um pedido para redefinir a senha da sua conta.",
                "Criar nova senha",
                $"{baseUrl}/redefinir-senha?token={Uri.EscapeDataString(token)}",
                "O link vale por 1 hora e só pode ser usado uma vez. Se você não pediu, ignore este e-mail: sua senha continua a mesma."),

            EmailTemplates.PasswordChanged => Build(
                email.To,
                "Sua senha foi alterada",
                $"Olá, {name}! A senha da sua conta acabou de ser alterada e as outras sessões foram encerradas.",
                "Acessar minha conta",
                $"{baseUrl}/login",
                "Não foi você? Use \"Esqueci minha senha\" na tela de login imediatamente e fale com o nosso atendimento."),

            _ => throw new InvalidOperationException($"Template de e-mail desconhecido: {email.Template}"),
        };
    }

    private EmailMessage Build(string to, string subject, string intro, string buttonText, string url, string footnote)
    {
        var store = WebUtility.HtmlEncode(options.Value.FromName);
        var html = $"""
            <!doctype html>
            <html lang="pt-BR">
            <body style="margin:0;padding:24px;background:#fafaf9;font-family:Arial,Helvetica,sans-serif;color:#151515">
              <table role="presentation" width="100%" style="max-width:560px;margin:0 auto;background:#ffffff;border:1px solid #e7e5e4;border-radius:8px">
                <tr><td style="padding:32px">
                  <p style="margin:0 0 24px;font-size:18px;font-weight:bold">{store}</p>
                  <h1 style="margin:0 0 16px;font-size:22px">{WebUtility.HtmlEncode(subject)}</h1>
                  <p style="margin:0 0 24px;font-size:15px;line-height:1.5">{WebUtility.HtmlEncode(intro)}</p>
                  <p style="margin:0 0 24px"><a href="{WebUtility.HtmlEncode(url)}" style="display:inline-block;background:#1b3a6b;color:#ffffff;text-decoration:none;padding:12px 20px;border-radius:6px;font-weight:bold">{WebUtility.HtmlEncode(buttonText)}</a></p>
                  <p style="margin:0 0 8px;font-size:13px;color:#5f6368">Se o botão não funcionar, copie e cole este endereço no navegador:<br>{WebUtility.HtmlEncode(url)}</p>
                  <p style="margin:16px 0 0;font-size:13px;color:#5f6368">{WebUtility.HtmlEncode(footnote)}</p>
                </td></tr>
              </table>
            </body>
            </html>
            """;

        var text = $"""
            {subject}

            {intro}

            {buttonText}: {url}

            {footnote}
            """;

        return new EmailMessage(to, subject, html, text);
    }

    private static string Value(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) ? value : string.Empty;
}
