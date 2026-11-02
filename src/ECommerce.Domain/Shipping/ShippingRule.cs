using System.Globalization;
using ECommerce.Domain.Abstractions;

namespace ECommerce.Domain.Shipping;

public static class ShippingRuleErrors
{
    public static readonly Error NotFound = Error.NotFound("Shipping.RuleNotFound", "Regra de frete não encontrada.");

    public static readonly Error InvalidName = Error.Validation("Shipping.InvalidRuleName", "Dê um nome de 2 a 120 caracteres à regra.");

    public static readonly Error InvalidEffectValue = Error.Validation(
        "Shipping.InvalidRuleValue", "Informe o valor da regra: desconto de 0,01 a 100% ou valor maior que zero. Frete grátis e ocultar serviço não têm valor.");

    public static readonly Error InvalidPeriod = Error.Validation("Shipping.InvalidRulePeriod", "O fim da vigência não pode ser antes do início.");

    public static readonly Error InvalidState = Error.Validation("Shipping.InvalidRuleState", "Informe as UFs com duas letras, como SP e RJ.");

    public static readonly Error InvalidPostalCodeRange = Error.Validation("Shipping.InvalidRuleRange", "Faixa de CEP inválida: informe início e fim, com 8 números cada.");

    public static readonly Error InvalidService = Error.Validation("Shipping.InvalidRuleService", "Os serviços são PAC e SEDEX.");

    public static readonly Error InvalidMinSubtotal = Error.Validation("Shipping.InvalidRuleSubtotal", "O subtotal mínimo não pode ser negativo.");
}

public enum ShippingRuleEffect
{
    FreeShipping,
    PercentDiscount,
    FixedDiscount,
    Surcharge,
    HideService,
}

/// <summary>Faixa de CEP inclusiva, guardada como <c>01000000-19999999</c>.</summary>
public sealed record PostalCodeRange(string From, string To)
{
    public static Result<PostalCodeRange> Create(string? from, string? to)
    {
        var start = PostalCode.Create(from);
        var end = PostalCode.Create(to);
        return start.IsFailure || end.IsFailure || string.CompareOrdinal(start.Value.Value, end.Value.Value) > 0
            ? ShippingRuleErrors.InvalidPostalCodeRange
            : new PostalCodeRange(start.Value.Value, end.Value.Value);
    }

    /// <summary>Uso exclusivo da Infrastructure.</summary>
    public static PostalCodeRange Rehydrate(string stored)
    {
        var parts = stored.Split('-');
        return new PostalCodeRange(parts[0], parts[1]);
    }

    public string ToStored() => $"{From}-{To}";

    public bool Contains(PostalCode postalCode) =>
        string.CompareOrdinal(postalCode.Value, From) >= 0 && string.CompareOrdinal(postalCode.Value, To) <= 0;
}

public sealed record ShippingRuleContent(
    string Name,
    int Priority,
    bool IsActive,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    decimal? MinSubtotal,
    IReadOnlyList<string> States,
    IReadOnlyList<PostalCodeRange> PostalCodeRanges,
    IReadOnlyList<string> ServiceCodes,
    ShippingRuleEffect Effect,
    decimal? EffectValue);

/// <summary>
/// Regra comercial aplicada sobre a cotação (arquivo 22 §3.3, RF-058). Condição vazia vale para todos. Em
/// <see cref="States"/> e nas faixas, basta atender a uma: o que restringe é uma condição contra a outra.
/// </summary>
public sealed class ShippingRule
{
    private ShippingRule(Guid id, ShippingRuleContent content)
    {
        Id = id;
        Content = content;
    }

    public Guid Id { get; }

    public ShippingRuleContent Content { get; }

    public static Result<ShippingRule> Create(Guid id, ShippingRuleContent content)
    {
        var name = content.Name?.Trim() ?? string.Empty;
        if (name.Length is < 2 or > 120)
        {
            return ShippingRuleErrors.InvalidName;
        }

        var validValue = content.Effect switch
        {
            ShippingRuleEffect.FreeShipping or ShippingRuleEffect.HideService => content.EffectValue is null,
            ShippingRuleEffect.PercentDiscount => content.EffectValue is > 0m and <= 100m,
            _ => content.EffectValue is > 0m,
        };
        if (!validValue)
        {
            return ShippingRuleErrors.InvalidEffectValue;
        }

        if (content.ValidFrom is { } from && content.ValidTo is { } to && to < from)
        {
            return ShippingRuleErrors.InvalidPeriod;
        }

        if (content.MinSubtotal is < 0m)
        {
            return ShippingRuleErrors.InvalidMinSubtotal;
        }

        if (content.States.Any(state => !BrazilianStates.IsValid(state)))
        {
            return ShippingRuleErrors.InvalidState;
        }

        if (content.ServiceCodes.Any(code => code is not (ShippingServices.Pac or ShippingServices.Sedex)))
        {
            return ShippingRuleErrors.InvalidService;
        }

        return new ShippingRule(id, content with { Name = name });
    }

    /// <summary>Uso exclusivo da Infrastructure.</summary>
    public static ShippingRule Rehydrate(Guid id, ShippingRuleContent content) => new(id, content);

    /// <summary>Tudo menos o subtotal: é o que permite dizer quanto falta para a regra valer.</summary>
    internal bool MatchesWhereAndWhen(ShippingPricingContext context, string serviceCode)
    {
        var rule = Content;
        return rule.IsActive
            && (rule.ValidFrom is null || context.Today >= rule.ValidFrom)
            && (rule.ValidTo is null || context.Today <= rule.ValidTo)
            && (rule.ServiceCodes.Count == 0 || rule.ServiceCodes.Contains(serviceCode))
            && (rule.States.Count == 0 || (BrazilianStates.FromPostalCode(context.Destination) is { } state && rule.States.Contains(state)))
            && (rule.PostalCodeRanges.Count == 0 || rule.PostalCodeRanges.Any(range => range.Contains(context.Destination)));
    }

    internal bool Matches(ShippingPricingContext context, string serviceCode) =>
        MatchesWhereAndWhen(context, serviceCode) && (Content.MinSubtotal is null || context.Subtotal >= Content.MinSubtotal);
}

/// <summary>
/// UF pelo CEP, em tabela fixa: uma regra de preço não pode falhar porque outro serviço caiu (entrega 3 §3, P6).
/// Faixas dos Correios.
/// </summary>
public static class BrazilianStates
{
    private static readonly (int From, int To, string State)[] Ranges =
    [
        (01000, 19999, "SP"), (20000, 28999, "RJ"), (29000, 29999, "ES"), (30000, 39999, "MG"), (40000, 48999, "BA"),
        (49000, 49999, "SE"), (50000, 56999, "PE"), (57000, 57999, "AL"), (58000, 58999, "PB"), (59000, 59999, "RN"),
        (60000, 63999, "CE"), (64000, 64999, "PI"), (65000, 65999, "MA"), (66000, 68899, "PA"), (68900, 68999, "AP"),
        (69000, 69299, "AM"), (69300, 69399, "RR"), (69400, 69899, "AM"), (69900, 69999, "AC"), (70000, 72799, "DF"),
        (72800, 72999, "GO"), (73000, 73699, "DF"), (73700, 76799, "GO"), (76800, 76999, "RO"), (77000, 77999, "TO"),
        (78000, 78899, "MT"), (79000, 79999, "MS"), (80000, 87999, "PR"), (88000, 89999, "SC"), (90000, 99999, "RS"),
    ];

    private static readonly HashSet<string> All = Ranges.Select(range => range.State).ToHashSet();

    public static bool IsValid(string state) => All.Contains(state);

    public static string? FromPostalCode(PostalCode postalCode)
    {
        var prefix = int.Parse(postalCode.Value.AsSpan(0, 5), CultureInfo.InvariantCulture);
        foreach (var (from, to, state) in Ranges)
        {
            if (prefix >= from && prefix <= to)
            {
                return state;
            }
        }

        return null;
    }
}

public sealed record ShippingPricingContext(decimal Subtotal, PostalCode Destination, DateOnly Today);

public sealed record PricedQuote(ShippingQuote Quote, decimal Price, IReadOnlyList<string> AppliedRules);

/// <summary><see cref="FreeShippingRemaining"/>: quanto falta para o frete ficar grátis, quando há regra para isso.</summary>
public sealed record PricedQuotes(IReadOnlyList<PricedQuote> Options, decimal? FreeShippingRemaining);

/// <summary>
/// Aplica as regras por prioridade crescente, acumulando, com piso em zero. Frete grátis e ocultar serviço encerram a
/// cadeia daquele serviço (entrega 3 §3, P11).
/// </summary>
public static class ShippingPricingService
{
    public static PricedQuotes Apply(IReadOnlyList<ShippingQuote> quotes, IReadOnlyList<ShippingRule> rules, ShippingPricingContext context)
    {
        var ordered = rules.OrderBy(rule => rule.Content.Priority).ThenBy(rule => rule.Id).ToList();
        var options = new List<PricedQuote>();
        foreach (var quote in quotes)
        {
            if (Price(quote, ordered, context) is { } priced)
            {
                options.Add(priced);
            }
        }

        return new PricedQuotes(options, FreeShippingRemaining(options, ordered, context));
    }

    private static PricedQuote? Price(ShippingQuote quote, List<ShippingRule> rules, ShippingPricingContext context)
    {
        var price = quote.Price;
        var applied = new List<string>();
        foreach (var rule in rules.Where(rule => rule.Matches(context, quote.ServiceCode)))
        {
            var value = rule.Content.EffectValue ?? 0m;
            switch (rule.Content.Effect)
            {
                case ShippingRuleEffect.HideService:
                    return null;
                case ShippingRuleEffect.FreeShipping:
                    applied.Add(rule.Content.Name);
                    return new PricedQuote(quote, 0m, applied);
                case ShippingRuleEffect.PercentDiscount:
                    price -= price * value / 100m;
                    break;
                case ShippingRuleEffect.FixedDiscount:
                    price -= value;
                    break;
                case ShippingRuleEffect.Surcharge:
                    price += value;
                    break;
            }

            price = Math.Max(price, 0m);
            applied.Add(rule.Content.Name);
        }

        return new PricedQuote(quote, decimal.Round(price, 2, MidpointRounding.AwayFromZero), applied);
    }

    /// <summary>Com algum serviço já grátis, não há o que prometer. Considera só os serviços que continuam à venda.</summary>
    private static decimal? FreeShippingRemaining(List<PricedQuote> options, List<ShippingRule> rules, ShippingPricingContext context)
    {
        if (options.Any(option => option.Price == 0m))
        {
            return null;
        }

        var thresholds = rules
            .Where(rule => rule.Content.Effect == ShippingRuleEffect.FreeShipping && rule.Content.MinSubtotal > context.Subtotal)
            .Where(rule => options.Any(option => rule.MatchesWhereAndWhen(context, option.Quote.ServiceCode)))
            .Select(rule => rule.Content.MinSubtotal!.Value)
            .ToList();

        return thresholds.Count == 0 ? null : thresholds.Min() - context.Subtotal;
    }
}

public interface IShippingRuleRepository
{
    Task AddAsync(ShippingRule rule, DateTime nowUtc, CancellationToken cancellationToken);

    /// <summary>Falso quando a regra não existe.</summary>
    Task<bool> UpdateAsync(ShippingRule rule, DateTime nowUtc, CancellationToken cancellationToken);

    /// <summary>Falso quando a regra não existe.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);
}
