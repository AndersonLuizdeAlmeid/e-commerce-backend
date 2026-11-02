using ECommerce.Domain.Abstractions;

namespace ECommerce.Domain.Shipping;

public static class ShippingErrors
{
    public static readonly Error InvalidPostalCode = Error.Validation("Shipping.InvalidPostalCode", "CEP inválido: informe os 8 números.");

    public static readonly Error PostalCodeNotFound = Error.NotFound("Shipping.PostalCodeNotFound", "Não encontramos este CEP.");

    public static readonly Error NotConfigured =
        Error.Unavailable("Shipping.NotConfigured", "O cálculo de frete ainda não está configurado. Fale com a MO para saber o frete.");

    public static readonly Error QuoteUnavailable = Error.Unavailable("Shipping.QuoteUnavailable", "Não foi possível consultar o frete agora.");

    public static readonly Error PostalCodeLookupUnavailable =
        Error.Unavailable("Shipping.PostalCodeLookupUnavailable", "Não foi possível consultar o CEP agora. Preencha o endereço à mão.");

    public static readonly Error ItemUnavailable =
        Error.BusinessRule("Shipping.ItemUnavailable", "Uma das peças não está mais disponível para calcular o frete.");
}

/// <summary>CEP: 8 dígitos, com ou sem hífen.</summary>
public sealed record PostalCode
{
    private PostalCode(string value) => Value = value;

    public string Value { get; }

    public static Result<PostalCode> Create(string? value)
    {
        var digits = (value ?? string.Empty).Trim().Replace("-", string.Empty, StringComparison.Ordinal);
        return digits.Length == 8 && digits.All(char.IsAsciiDigit) ? new PostalCode(digits) : ShippingErrors.InvalidPostalCode;
    }

    /// <summary>Uso exclusivo da Infrastructure, para valores já validados no banco.</summary>
    public static PostalCode Rehydrate(string value) => new(value);

    public override string ToString() => Value;
}

public sealed record PackageDimensions(decimal HeightCm, decimal WidthCm, decimal LengthCm);

public sealed record ShippingPackage(int WeightGrams, PackageDimensions Dimensions);

/// <summary>Uma linha da cotação: as medidas são de uma unidade.</summary>
public sealed record PackageItem(int WeightGrams, PackageDimensions Dimensions, int Quantity);

/// <summary>Configuração da loja (RF-058). Os valores de contingência valem quando o provedor está fora (arquivo 22 §5).</summary>
public sealed record ShippingSettings(
    PostalCode Origin,
    int HandlingDays,
    decimal FallbackPacPrice,
    decimal FallbackSedexPrice,
    int BoxWeightGrams,
    PackageDimensions Box);

public sealed record ShippingQuote(string ServiceCode, string ServiceName, decimal Price, int? DeliveryDays);

public static class ShippingServices
{
    public const string Pac = "PAC";
    public const string Sedex = "SEDEX";
}

/// <summary>
/// Monta um pacote só, a estimativa simples do arquivo 22 §3.2: soma os pesos com o da caixa padrão. Peça que cabe na
/// caixa não muda as medidas; a que não cabe, como uma bolsa, aumenta o pacote, e a altura soma as que não couberam.
/// </summary>
public static class PackagingService
{
    public static ShippingPackage Build(int boxWeightGrams, PackageDimensions box, IReadOnlyList<PackageItem> items)
    {
        var weight = boxWeightGrams + items.Sum(item => item.WeightGrams * item.Quantity);
        var oversized = items.Where(item => !Fits(item.Dimensions, box)).ToList();
        if (oversized.Count == 0)
        {
            return new ShippingPackage(weight, box);
        }

        var anyInsideTheBox = oversized.Count < items.Count;
        return new ShippingPackage(
            weight,
            new PackageDimensions(
                oversized.Sum(item => item.Dimensions.HeightCm * item.Quantity) + (anyInsideTheBox ? box.HeightCm : 0m),
                Math.Max(box.WidthCm, oversized.Max(item => item.Dimensions.WidthCm)),
                Math.Max(box.LengthCm, oversized.Max(item => item.Dimensions.LengthCm))));
    }

    /// <summary>A peça pode ser girada: compara a menor medida com a menor, e assim por diante.</summary>
    private static bool Fits(PackageDimensions item, PackageDimensions box)
    {
        decimal[] sortedItem = [item.HeightCm, item.WidthCm, item.LengthCm];
        decimal[] sortedBox = [box.HeightCm, box.WidthCm, box.LengthCm];
        Array.Sort(sortedItem);
        Array.Sort(sortedBox);
        return sortedItem[0] <= sortedBox[0] && sortedItem[1] <= sortedBox[1] && sortedItem[2] <= sortedBox[2];
    }
}

public interface IShippingSettingsRepository
{
    Task SaveAsync(ShippingSettings settings, DateTime nowUtc, CancellationToken cancellationToken);
}
