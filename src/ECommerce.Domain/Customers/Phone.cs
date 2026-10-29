using ECommerce.Domain.Abstractions;

namespace ECommerce.Domain.Customers;

/// <summary>Telefone brasileiro: DDD + 8 dígitos (fixo) ou 9 dígitos começando com 9 (celular), guardado só com dígitos.</summary>
public sealed record Phone
{
    private Phone(string value) => Value = value;

    public string Value { get; }

    public static Result<Phone> Create(string? value)
    {
        var digits = new string((value ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
        if (digits.StartsWith("55", StringComparison.Ordinal) && digits.Length is 12 or 13)
        {
            digits = digits[2..];
        }

        var validDdd = digits.Length >= 2 && digits[0] != '0' && digits[1] != '0';
        var validLength = digits.Length == 10 || (digits.Length == 11 && digits[2] == '9');

        return validDdd && validLength ? new Phone(digits) : CustomerErrors.InvalidPhone;
    }

    public static Phone Rehydrate(string value) => new(value);

    public override string ToString() => Value;
}
