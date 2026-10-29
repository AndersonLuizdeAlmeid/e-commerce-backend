using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using ECommerce.Infrastructure.Notifications;

namespace ECommerce.Api.FunctionalTests.Infrastructure;

/// <summary>Captura os e-mails que o Outbox enviaria; os testes esperam por eles e extraem os links.</summary>
public sealed partial class FakeEmailSender : IEmailSender
{
    private readonly ConcurrentQueue<EmailMessage> _sent = new();

    public IReadOnlyCollection<EmailMessage> Sent => _sent;

    /// <summary>Faz o próximo envio falhar (testa o retry do Outbox).</summary>
    public int FailNext { get; set; }

    public Task<string?> SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        if (FailNext > 0)
        {
            FailNext--;
            throw new InvalidOperationException("Falha simulada do provedor de e-mail.");
        }

        _sent.Enqueue(message);
        return Task.FromResult<string?>($"fake-{Guid.NewGuid():N}");
    }

    public void Clear()
    {
        _sent.Clear();
        FailNext = 0;
    }

    /// <summary>Espera o Outbox processar (ele roda em background a cada 200 ms nos testes).</summary>
    public async Task<EmailMessage> WaitForAsync(string to, string subjectContains, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (DateTime.UtcNow < deadline)
        {
            var match = _sent.FirstOrDefault(m =>
                string.Equals(m.To, to, StringComparison.OrdinalIgnoreCase)
                && m.Subject.Contains(subjectContains, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Nenhum e-mail '{subjectContains}' para {to}. Enviados: {string.Join(" | ", _sent.Select(m => $"{m.To}: {m.Subject}"))}");
    }

    /// <summary>Token do link "...?token=xyz" no corpo em texto.</summary>
    public static string TokenFrom(EmailMessage message)
    {
        var match = TokenRegex().Match(message.TextBody);
        match.Success.ShouldBeTrue($"Link com token não encontrado em: {message.TextBody}");
        return Uri.UnescapeDataString(match.Groups[1].Value);
    }

    [GeneratedRegex(@"[?&]token=([^\s&]+)")]
    private static partial Regex TokenRegex();
}
