using ECommerce.Domain.Abstractions;

namespace ECommerce.Domain.Customers;

/// <summary>CPF com dígitos verificadores válidos; guardado só com os 11 dígitos.</summary>
public sealed record Cpf
{
    private Cpf(string value) => Value = value;

    public string Value { get; }

    /// <summary>Aceita com ou sem máscara ("123.456.789-09" ou "12345678909").</summary>
    public static Result<Cpf> Create(string? value)
    {
        var digits = new string((value ?? string.Empty).Where(char.IsAsciiDigit).ToArray());

        if (digits.Length != 11 || digits.Distinct().Count() == 1
            || CheckDigit(digits, 9) != digits[9] - '0'
            || CheckDigit(digits, 10) != digits[10] - '0')
        {
            return CustomerErrors.InvalidCpf;
        }

        return new Cpf(digits);
    }

    public static Cpf Rehydrate(string value) => new(value);

    /// <summary>"***.456.789-**" (arquivo 17 §5).</summary>
    public string Masked() => $"***.{Value[3..6]}.{Value[6..9]}-**";

    private static int CheckDigit(string digits, int length)
    {
        var sum = 0;
        for (var i = 0; i < length; i++)
        {
            sum += (digits[i] - '0') * (length + 1 - i);
        }

        var remainder = sum % 11;
        return remainder < 2 ? 0 : 11 - remainder;
    }

    public override string ToString() => Masked();
}
