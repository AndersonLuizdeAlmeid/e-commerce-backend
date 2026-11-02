using ECommerce.Domain.Shipping;

namespace ECommerce.Domain.UnitTests.Shipping;

public sealed class ShippingPricingTests
{
    private static readonly DateOnly Today = new(2026, 11, 2);
    private static readonly ShippingQuote Pac = new(ShippingServices.Pac, ShippingServices.Pac, 20m, 7);
    private static readonly ShippingQuote Sedex = new(ShippingServices.Sedex, ShippingServices.Sedex, 40m, 3);

    private static ShippingRule Rule(
        ShippingRuleEffect effect,
        decimal? value = null,
        int priority = 10,
        string name = "Regra",
        bool isActive = true,
        decimal? minSubtotal = null,
        string[]? states = null,
        PostalCodeRange[]? ranges = null,
        string[]? services = null,
        DateOnly? validFrom = null,
        DateOnly? validTo = null) =>
        ShippingRule.Create(
            Guid.CreateVersion7(),
            new ShippingRuleContent(name, priority, isActive, validFrom, validTo, minSubtotal, states ?? [], ranges ?? [], services ?? [], effect, value)).Value;

    private static PricedQuotes Apply(decimal subtotal, string destination, params ShippingRule[] rules) =>
        ShippingPricingService.Apply([Pac, Sedex], rules, new ShippingPricingContext(subtotal, PostalCode.Create(destination).Value, Today));

    private static decimal PriceOf(PricedQuotes quotes, string service) => quotes.Options.Single(o => o.Quote.ServiceCode == service).Price;

    [Fact, Trait("Spec", "RF-058")]
    public void NoRules_KeepsThePrices()
    {
        var result = Apply(100m, "01310100");

        PriceOf(result, ShippingServices.Pac).ShouldBe(20m);
        result.Options[0].AppliedRules.ShouldBeEmpty();
        result.FreeShippingRemaining.ShouldBeNull();
    }

    [Theory, Trait("Spec", "RF-058")]
    [InlineData(ShippingRuleEffect.PercentDiscount, 50, 10)]
    [InlineData(ShippingRuleEffect.FixedDiscount, 5, 15)]
    [InlineData(ShippingRuleEffect.FixedDiscount, 50, 0)]
    [InlineData(ShippingRuleEffect.Surcharge, 3, 23)]
    public void Effects_ChangeThePrice_WithFloorAtZero(ShippingRuleEffect effect, int value, int expected)
    {
        var result = Apply(100m, "01310100", Rule(effect, value, name: "Promoção"));

        PriceOf(result, ShippingServices.Pac).ShouldBe(expected);
        result.Options[0].AppliedRules.ShouldBe(["Promoção"]);
        result.Options[0].Quote.Price.ShouldBe(20m);
    }

    [Fact, Trait("Spec", "RF-058")]
    public void Price_IsRoundedToCents()
    {
        var quotes = ShippingPricingService.Apply(
            [new ShippingQuote(ShippingServices.Pac, ShippingServices.Pac, 21.45m, 7)],
            [Rule(ShippingRuleEffect.PercentDiscount, 33.33m)],
            new ShippingPricingContext(100m, PostalCode.Create("01310100").Value, Today));

        quotes.Options[0].Price.ShouldBe(14.30m);
    }

    [Fact, Trait("Spec", "RF-058")]
    public void Rules_ApplyInPriorityOrder_AndAccumulate()
    {
        var result = Apply(
            100m,
            "01310100",
            Rule(ShippingRuleEffect.Surcharge, 10m, priority: 20, name: "Embalagem"),
            Rule(ShippingRuleEffect.PercentDiscount, 50m, priority: 10, name: "Metade"));

        PriceOf(result, ShippingServices.Pac).ShouldBe(20m);
        result.Options[0].AppliedRules.ShouldBe(["Metade", "Embalagem"]);
    }

    [Fact, Trait("Spec", "RF-058")]
    public void FreeShipping_EndsTheChain_ButKeepsWhatCameBefore()
    {
        var result = Apply(
            100m,
            "01310100",
            Rule(ShippingRuleEffect.Surcharge, 3m, priority: 1, name: "Embalagem"),
            Rule(ShippingRuleEffect.FreeShipping, priority: 2, name: "Grátis"),
            Rule(ShippingRuleEffect.Surcharge, 9m, priority: 3, name: "Depois"));

        PriceOf(result, ShippingServices.Pac).ShouldBe(0m);
        result.Options[0].AppliedRules.ShouldBe(["Embalagem", "Grátis"]);
    }

    [Fact, Trait("Spec", "RF-058")]
    public void HideService_RemovesOnlyThatService()
    {
        var result = Apply(100m, "01310100", Rule(ShippingRuleEffect.HideService, services: [ShippingServices.Sedex]));

        result.Options.ShouldHaveSingleItem().Quote.ServiceCode.ShouldBe(ShippingServices.Pac);
    }

    [Fact, Trait("Spec", "RF-058")]
    public void Services_LimitWhereTheRuleApplies()
    {
        var result = Apply(100m, "01310100", Rule(ShippingRuleEffect.FreeShipping, services: [ShippingServices.Pac]));

        PriceOf(result, ShippingServices.Pac).ShouldBe(0m);
        PriceOf(result, ShippingServices.Sedex).ShouldBe(40m);
    }

    [Theory, Trait("Spec", "RF-058")]
    [InlineData("299.00", true)]
    [InlineData("298.99", false)]
    public void MinSubtotal_IsInclusive(string subtotal, bool free)
    {
        var result = Apply(decimal.Parse(subtotal, System.Globalization.CultureInfo.InvariantCulture), "01310100", Rule(ShippingRuleEffect.FreeShipping, minSubtotal: 299m));

        (PriceOf(result, ShippingServices.Pac) == 0m).ShouldBe(free);
    }

    [Theory, Trait("Spec", "RF-058")]
    [InlineData("01310100", true)]
    [InlineData("20040020", true)]
    [InlineData("30130000", false)]
    public void States_FollowThePostalCode(string destination, bool applies)
    {
        var result = Apply(100m, destination, Rule(ShippingRuleEffect.FreeShipping, states: ["SP", "RJ"]));

        (PriceOf(result, ShippingServices.Pac) == 0m).ShouldBe(applies);
    }

    [Theory, Trait("Spec", "RF-058")]
    [InlineData("01000000", true)]
    [InlineData("01999999", true)]
    [InlineData("02000000", false)]
    [InlineData("00999999", false)]
    public void Ranges_AreInclusive(string destination, bool applies)
    {
        var range = PostalCodeRange.Create("01000-000", "01999-999").Value;

        var result = Apply(100m, destination, Rule(ShippingRuleEffect.FreeShipping, ranges: [range]));

        (PriceOf(result, ShippingServices.Pac) == 0m).ShouldBe(applies);
    }

    [Fact, Trait("Spec", "RF-058")]
    public void Conditions_MustAllBeMet()
    {
        var rule = Rule(ShippingRuleEffect.FreeShipping, minSubtotal: 100m, states: ["SP"]);

        PriceOf(Apply(100m, "01310100", rule), ShippingServices.Pac).ShouldBe(0m);
        PriceOf(Apply(99m, "01310100", rule), ShippingServices.Pac).ShouldBe(20m);
        PriceOf(Apply(100m, "20040020", rule), ShippingServices.Pac).ShouldBe(20m);
    }

    [Theory, Trait("Spec", "RF-058")]
    [InlineData(-1, 1, true)]
    [InlineData(0, 0, true)]
    [InlineData(1, 5, false)]
    [InlineData(-5, -1, false)]
    public void Period_IsInclusive(int fromDays, int toDays, bool applies)
    {
        var rule = Rule(ShippingRuleEffect.FreeShipping, validFrom: Today.AddDays(fromDays), validTo: Today.AddDays(toDays));

        (PriceOf(Apply(100m, "01310100", rule), ShippingServices.Pac) == 0m).ShouldBe(applies);
    }

    [Fact, Trait("Spec", "RF-058")]
    public void InactiveRule_IsIgnored()
    {
        PriceOf(Apply(100m, "01310100", Rule(ShippingRuleEffect.FreeShipping, isActive: false)), ShippingServices.Pac).ShouldBe(20m);
    }

    [Fact, Trait("Spec", "26-FRETE-GRATIS")]
    public void Remaining_ShowsTheNearestFreeShipping()
    {
        var result = Apply(
            250m,
            "01310100",
            Rule(ShippingRuleEffect.FreeShipping, minSubtotal: 299m),
            Rule(ShippingRuleEffect.FreeShipping, minSubtotal: 500m));

        result.FreeShippingRemaining.ShouldBe(49m);
    }

    [Fact, Trait("Spec", "26-FRETE-GRATIS")]
    public void Remaining_IsEmpty_WhenAlreadyFree_OrWhenTheRuleDoesNotReachThisCustomer()
    {
        Apply(300m, "01310100", Rule(ShippingRuleEffect.FreeShipping, minSubtotal: 299m)).FreeShippingRemaining.ShouldBeNull();
        Apply(100m, "30130000", Rule(ShippingRuleEffect.FreeShipping, minSubtotal: 299m, states: ["SP"])).FreeShippingRemaining.ShouldBeNull();
        Apply(100m, "01310100", Rule(ShippingRuleEffect.FreeShipping, minSubtotal: 299m, validTo: Today.AddDays(-1))).FreeShippingRemaining.ShouldBeNull();
    }

    [Fact, Trait("Spec", "26-FRETE-GRATIS")]
    public void Remaining_IgnoresAServiceThatWasHidden()
    {
        var result = Apply(
            100m,
            "01310100",
            Rule(ShippingRuleEffect.HideService, services: [ShippingServices.Pac]),
            Rule(ShippingRuleEffect.FreeShipping, minSubtotal: 299m, services: [ShippingServices.Pac]));

        result.FreeShippingRemaining.ShouldBeNull();
    }

    [Theory, Trait("Spec", "22-UF")]
    [InlineData("01310100", "SP")]
    [InlineData("19999999", "SP")]
    [InlineData("20040020", "RJ")]
    [InlineData("29000000", "ES")]
    [InlineData("30130000", "MG")]
    [InlineData("40000000", "BA")]
    [InlineData("69300000", "RR")]
    [InlineData("69400000", "AM")]
    [InlineData("70000000", "DF")]
    [InlineData("72800000", "GO")]
    [InlineData("73000000", "DF")]
    [InlineData("73700000", "GO")]
    [InlineData("76800000", "RO")]
    [InlineData("80000000", "PR")]
    [InlineData("88000000", "SC")]
    [InlineData("99999999", "RS")]
    public void State_ComesFromThePostalCode(string postalCode, string state)
    {
        BrazilianStates.FromPostalCode(PostalCode.Create(postalCode).Value).ShouldBe(state);
    }

    [Fact, Trait("Spec", "22-UF")]
    public void State_UnknownPrefix_IsNull()
    {
        BrazilianStates.FromPostalCode(PostalCode.Create("00000000").Value).ShouldBeNull();
    }

    private static ShippingRuleContent Content(
        string name = "Regra", ShippingRuleEffect effect = ShippingRuleEffect.FreeShipping, decimal? value = null, string[]? states = null, string[]? services = null, decimal? minSubtotal = null, DateOnly? from = null, DateOnly? to = null) =>
        new(name, 1, true, from, to, minSubtotal, states ?? [], [], services ?? [], effect, value);

    [Fact, Trait("Spec", "RF-058")]
    public void Create_RefusesInvalidContent()
    {
        Check(Content(name: "x"), ShippingRuleErrors.InvalidName);
        Check(Content(effect: ShippingRuleEffect.FreeShipping, value: 1m), ShippingRuleErrors.InvalidEffectValue);
        Check(Content(effect: ShippingRuleEffect.HideService, value: 1m), ShippingRuleErrors.InvalidEffectValue);
        Check(Content(effect: ShippingRuleEffect.PercentDiscount), ShippingRuleErrors.InvalidEffectValue);
        Check(Content(effect: ShippingRuleEffect.PercentDiscount, value: 101m), ShippingRuleErrors.InvalidEffectValue);
        Check(Content(effect: ShippingRuleEffect.Surcharge, value: 0m), ShippingRuleErrors.InvalidEffectValue);
        Check(Content(states: ["XX"]), ShippingRuleErrors.InvalidState);
        Check(Content(services: ["JADLOG"]), ShippingRuleErrors.InvalidService);
        Check(Content(minSubtotal: -1m), ShippingRuleErrors.InvalidMinSubtotal);
        Check(Content(from: Today, to: Today.AddDays(-1)), ShippingRuleErrors.InvalidPeriod);

        static void Check(ShippingRuleContent content, ECommerce.Domain.Abstractions.Error expected) =>
            ShippingRule.Create(Guid.CreateVersion7(), content).Error.ShouldBe(expected);
    }

    [Theory, Trait("Spec", "RF-058")]
    [InlineData("123", "20000000")]
    [InlineData("20000000", "abc")]
    [InlineData("20000000", "10000000")]
    public void Range_Invalid_IsRefused(string from, string to)
    {
        PostalCodeRange.Create(from, to).Error.ShouldBe(ShippingRuleErrors.InvalidPostalCodeRange);
    }
}
