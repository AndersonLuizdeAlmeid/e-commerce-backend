using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using ECommerce.Application.Notifications;
using ECommerce.Application.Ordering;
using Microsoft.Extensions.Options;

namespace ECommerce.Infrastructure.Notifications;

/// <summary>
/// Templates em pt-BR, HTML simples com estilos inline + versão texto (arquivo 23 §4): conta (EM-01..03) e pedido
/// (EM-04, EM-05, EM-10).
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

            EmailTemplates.OrderPlaced => BuildOrder(
                email,
                $"Recebemos seu pedido {Value(email.Model, "orderNumber")}",
                $"Olá, {name}! Seu pedido está separado para você. O pagamento precisa ser feito até {Value(email.Model, "dueAt")}; depois disso o pedido é cancelado e as peças voltam para a loja.",
                "Ver pedido e pagar",
                "Qualquer dúvida, responda este e-mail ou fale com a MO pelo WhatsApp."),

            EmailTemplates.PaymentReminder => BuildOrder(
                email,
                $"Seu pedido {Value(email.Model, "orderNumber")} ainda espera o pagamento",
                $"Olá, {name}! Suas peças continuam separadas até {Value(email.Model, "dueAt")}. Depois disso o pedido é cancelado e elas voltam para a loja.",
                "Pagar agora",
                "Se você já pagou, pode ignorar este e-mail: a confirmação chega em instantes."),

            EmailTemplates.OrderCancelled => BuildOrder(
                email,
                $"Pedido {Value(email.Model, "orderNumber")} cancelado",
                $"Olá, {name}! Seu pedido foi cancelado {Value(email.Model, "cancellationReason")}. As peças voltaram para a loja.",
                "Ver pedido",
                "Se quiser as peças, é só fazer um novo pedido. Qualquer dúvida, fale com a MO."),

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

    /// <summary>E-mail de pedido: o texto, o resumo das peças e dos valores, o endereço e, se houver, a dedicatória.</summary>
    private EmailMessage BuildOrder(SendEmail email, string subject, string intro, string buttonText, string footnote)
    {
        var baseUrl = options.Value.FrontendBaseUrl.TrimEnd('/');
        var url = $"{baseUrl}/conta/pedidos/{Uri.EscapeDataString(Value(email.Model, "orderId"))}";
        var lines = JsonSerializer.Deserialize<List<OrderEmailLine>>(Value(email.Model, "items"), JsonSerializerOptions.Web) ?? [];

        var rows = new StringBuilder();
        var textRows = new StringBuilder();
        foreach (var line in lines)
        {
            var detail = line.Detail is null ? string.Empty : $"<br><span style=\"color:#5f6368;font-size:13px\">{WebUtility.HtmlEncode(line.Detail)}</span>";
            rows.Append(CultureInfo.InvariantCulture, $"""
                <tr><td style="padding:6px 0;font-size:14px">{line.Quantity}× {WebUtility.HtmlEncode(line.Name)}{detail}</td><td style="padding:6px 0;font-size:14px;text-align:right;white-space:nowrap">{WebUtility.HtmlEncode(MoneyFormat.Brl(line.Total))}</td></tr>
                """);
            textRows.Append(CultureInfo.InvariantCulture, $"{line.Quantity}x {line.Name}{(line.Detail is null ? string.Empty : $" ({line.Detail})")}: {MoneyFormat.Brl(line.Total)}\n");
        }

        var totals = new List<(string Label, string Value)>
        {
            ("Peças", Value(email.Model, "subtotal")),
            ("Frete", Value(email.Model, "shipping")),
        };
        if (Value(email.Model, "giftWrap") is { Length: > 0 } wrap)
        {
            totals.Add(("Embalagem para presente", wrap));
        }

        totals.Add(("Total", Value(email.Model, "total")));
        foreach (var (label, value) in totals)
        {
            var weight = label == "Total" ? "font-weight:bold;" : string.Empty;
            rows.Append(CultureInfo.InvariantCulture, $"""
                <tr><td style="padding:6px 0;font-size:14px;border-top:1px solid #e7e5e4;{weight}">{WebUtility.HtmlEncode(label)}</td><td style="padding:6px 0;font-size:14px;text-align:right;border-top:1px solid #e7e5e4;{weight}">{WebUtility.HtmlEncode(value)}</td></tr>
                """);
            textRows.Append(CultureInfo.InvariantCulture, $"{label}: {value}\n");
        }

        var giftMessage = Value(email.Model, "giftMessage");
        var giftHtml = giftMessage.Length == 0
            ? string.Empty
            : $"""<p style="margin:0 0 16px;font-size:14px;line-height:1.5"><strong>Dedicatória</strong><br>{WebUtility.HtmlEncode(giftMessage).Replace("\n", "<br>", StringComparison.Ordinal)}</p>""";
        var address = Value(email.Model, "address");
        var giftText = giftMessage.Length == 0 ? string.Empty : $"Dedicatória: {giftMessage.Replace('\n', ' ')}";

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
                  <table role="presentation" width="100%" style="margin:0 0 24px;border-collapse:collapse">{rows}</table>
                  <p style="margin:0 0 16px;font-size:14px;line-height:1.5"><strong>Entrega</strong><br>{WebUtility.HtmlEncode(address)}</p>
                  {giftHtml}
                  <p style="margin:0 0 24px"><a href="{WebUtility.HtmlEncode(url)}" style="display:inline-block;background:#1a1a17;color:#ffffff;text-decoration:none;padding:12px 20px;border-radius:6px;font-weight:bold">{WebUtility.HtmlEncode(buttonText)}</a></p>
                  <p style="margin:16px 0 0;font-size:13px;color:#5f6368">{WebUtility.HtmlEncode(footnote)}</p>
                </td></tr>
              </table>
            </body>
            </html>
            """;

        var text = $"""
            {subject}

            {intro}

            {textRows}
            Entrega: {address}
            {giftText}
            {buttonText}: {url}

            {footnote}
            """;

        return new EmailMessage(email.To, subject, html, text);
    }

    private static string Value(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) ? value : string.Empty;
}
