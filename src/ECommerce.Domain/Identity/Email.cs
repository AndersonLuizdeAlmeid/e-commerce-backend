using System.Net.Mail;
using ECommerce.Domain.Abstractions;

namespace ECommerce.Domain.Identity;

/// <summary>E-mail válido, normalizado (trim + minúsculas) e com no máximo 254 caracteres (RF-001 CA4).</summary>
public sealed record Email
{
    public const int MaxLength = 254;

    private Email(string value) => Value = value;

    public string Value { get; }

    public static Result<Email> Create(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;

        if (normalized.Length is 0 or > MaxLength
            || !MailAddress.TryCreate(normalized, out var address)
            || address.Address != normalized
            || !address.Host.Contains('.', StringComparison.Ordinal))
        {
            return IdentityErrors.InvalidEmail;
        }

        return new Email(normalized);
    }

    /// <summary>Uso exclusivo da Infrastructure, para valores já validados no banco.</summary>
    public static Email Rehydrate(string value) => new(value);

    /// <summary>"maria@loja.com" → "m***@loja.com" (logs e e-mail_log, LGPD).</summary>
    public string Masked()
    {
        var at = Value.IndexOf('@', StringComparison.Ordinal);
        return at <= 1 ? "***" + Value[at..] : Value[0] + "***" + Value[at..];
    }

    public override string ToString() => Value;
}
