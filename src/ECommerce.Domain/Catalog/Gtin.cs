using ECommerce.Domain.Abstractions;

namespace ECommerce.Domain.Catalog;

/// <summary>Código de barras EAN-8, UPC-A (12), EAN-13 ou GTIN-14, com dígito verificador conferido (RF-056 CA4, RF-059).</summary>
public sealed record Gtin
{
    private Gtin(string value) => Value = value;

    public string Value { get; }

    public static Result<Gtin> Create(string? value)
    {
        var digits = value?.Trim() ?? string.Empty;
        if (digits.Length is not (8 or 12 or 13 or 14) || !digits.All(char.IsAsciiDigit) || !CheckDigitMatches(digits))
        {
            return CatalogDomainErrors.InvalidBarcode;
        }

        return new Gtin(digits);
    }

    /// <summary>Uso exclusivo da Infrastructure, para valores já validados no banco.</summary>
    public static Gtin Rehydrate(string value) => new(value);

    public override string ToString() => Value;

    /// <summary>Módulo 10 do GS1: da direita para a esquerda (sem o verificador), pesos 3 e 1 alternados.</summary>
    private static bool CheckDigitMatches(string digits)
    {
        var sum = 0;
        for (var i = digits.Length - 2; i >= 0; i--)
        {
            var weight = (digits.Length - 2 - i) % 2 == 0 ? 3 : 1;
            sum += (digits[i] - '0') * weight;
        }

        return (10 - (sum % 10)) % 10 == digits[^1] - '0';
    }
}
