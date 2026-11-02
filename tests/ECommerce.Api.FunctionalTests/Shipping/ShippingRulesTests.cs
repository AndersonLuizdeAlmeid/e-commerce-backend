using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ECommerce.Api.FunctionalTests.Catalog;
using ECommerce.Api.FunctionalTests.Infrastructure;
using ECommerce.Application.Abstractions.Caching;
using ECommerce.Domain.Shipping;
using Microsoft.Extensions.DependencyInjection;

namespace ECommerce.Api.FunctionalTests.Shipping;

public sealed class ShippingRulesTests(ApiFactory factory) : AdminCatalogTest(factory)
{
    private const string Rules = "/api/v1/admin/shipping/rules";

    private static readonly string[] OnlyPac = ["PAC"];
    private static readonly string[] OnlySedex = ["SEDEX"];
    private static readonly string[] Rio = ["rj"];
    private static readonly string[] SoutheastTyped = ["sp", "RJ", "SP"];

    private async Task<(AuthClient Admin, Guid Variant)> ConfiguredAsync()
    {
        var admin = await AdminAsync();
        var settings = new { originPostalCode = "01310100", handlingDays = 0, fallbackPacPrice = 25m, fallbackSedexPrice = 45m, boxWeightGrams = 100, boxHeightCm = 4m, boxWidthCm = 12m, boxLengthCm = 16m };
        (await admin.PutAsync("/api/v1/admin/shipping/settings", settings)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await Factory.Services.GetRequiredService<ICacheService>().RemoveByTagAsync("shipping", CancellationToken);
        var product = await ProductInStoreAsync(admin, "Anel Liso");
        return (admin, (await GetProductAsync(admin, product)).GetProperty("variants")[0].GetProperty("id").GetGuid());
    }

    private async Task<JsonElement> QuoteAsync(Guid variant, int quantity = 1, string postalCode = "20040020")
    {
        var response = await Client.PostAsJsonAsync("/api/v1/shipping/quotes", new { postalCode, items = new[] { new { variantId = variant, quantity } } }, CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken);
    }

    private static async Task<Guid> CreateRuleAsync(AuthClient admin, object rule)
    {
        var response = await admin.PostAsync(Rules, rule);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken)).GetProperty("id").GetGuid();
    }

    private static object FreeAbove(decimal minSubtotal, string name = "Grátis acima de um valor", bool isActive = true) =>
        new { name, priority = 10, isActive, minSubtotal, effect = "FreeShipping" };

    [Fact, Trait("Spec", "RF-058")]
    public async Task FreeShippingRule_DependsOnTheSubtotal_WithTheSamePackageCache()
    {
        var (admin, ring) = await ConfiguredAsync();
        using (admin)
        {
            await CreateRuleAsync(admin, FreeAbove(150m));

            var below = await QuoteAsync(ring);
            var pacBelow = below.GetProperty("options")[0];
            pacBelow.GetProperty("price").GetDecimal().ShouldBe(21.40m);
            pacBelow.GetProperty("appliedRules").GetArrayLength().ShouldBe(0);
            below.GetProperty("freeShippingRemaining").GetDecimal().ShouldBe(60.10m);

            var above = await QuoteAsync(ring, 2);
            var pacAbove = above.GetProperty("options")[0];
            pacAbove.GetProperty("price").GetDecimal().ShouldBe(0m);
            pacAbove.GetProperty("originalPrice").GetDecimal().ShouldBe(21.40m);
            pacAbove.GetProperty("appliedRules")[0].GetString().ShouldBe("Grátis acima de um valor");
            above.GetProperty("freeShippingRemaining").ValueKind.ShouldBe(JsonValueKind.Null);
        }
    }

    [Fact, Trait("Spec", "12-SHIPPING-CACHE")]
    public async Task Rules_ApplyAfterTheCache_AndAtOnce()
    {
        var (admin, ring) = await ConfiguredAsync();
        using (admin)
        {
            (await QuoteAsync(ring)).GetProperty("options")[0].GetProperty("price").GetDecimal().ShouldBe(21.40m);

            var rule = await CreateRuleAsync(admin, new { name = "Metade", priority = 1, isActive = true, effect = "PercentDiscount", effectValue = 50m });
            (await QuoteAsync(ring)).GetProperty("options")[0].GetProperty("price").GetDecimal().ShouldBe(10.70m);

            (await admin.DeleteAsync($"{Rules}/{rule}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
            (await QuoteAsync(ring)).GetProperty("options")[0].GetProperty("price").GetDecimal().ShouldBe(21.40m);

            Factory.ShippingQuotes.QuoteRequests.Count.ShouldBe(1);
        }
    }

    [Fact, Trait("Spec", "RF-058")]
    public async Task Rules_FollowTheStateAndRangeOfThePostalCode()
    {
        var (admin, ring) = await ConfiguredAsync();
        using (admin)
        {
            await CreateRuleAsync(admin, new { name = "Só RJ", priority = 1, isActive = true, states = Rio, effect = "FreeShipping", serviceCodes = OnlyPac });
            await CreateRuleAsync(admin, new { name = "Faixa BH", priority = 2, isActive = true, postalCodeRanges = new[] { new { from = "30000-000", to = "30999-999" } }, effect = "HideService", serviceCodes = OnlySedex });

            var rio = (await QuoteAsync(ring, postalCode: "20040020")).GetProperty("options");
            rio[0].GetProperty("price").GetDecimal().ShouldBe(0m);
            rio[1].GetProperty("price").GetDecimal().ShouldBe(38.90m);

            var bh = (await QuoteAsync(ring, postalCode: "30130000")).GetProperty("options");
            bh.GetArrayLength().ShouldBe(1);
            bh[0].GetProperty("serviceCode").GetString().ShouldBe("PAC");
            bh[0].GetProperty("price").GetDecimal().ShouldBe(21.40m);
        }
    }

    [Fact, Trait("Spec", "RF-058")]
    public async Task Rules_RespectTheDatesInBrasilia_AndTheActiveFlag()
    {
        var (admin, ring) = await ConfiguredAsync();
        using (admin)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-3));
            var formatted = today.AddDays(5).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            await CreateRuleAsync(admin, new { name = "Futura", priority = 1, isActive = true, validFrom = formatted, effect = "FreeShipping" });
            await CreateRuleAsync(admin, FreeAbove(0m, "Desligada", isActive: false));

            (await QuoteAsync(ring)).GetProperty("options")[0].GetProperty("price").GetDecimal().ShouldBe(21.40m);

            var current = today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            await CreateRuleAsync(admin, new { name = "Hoje", priority = 2, isActive = true, validFrom = current, validTo = current, effect = "FreeShipping" });
            (await QuoteAsync(ring)).GetProperty("options")[0].GetProperty("appliedRules")[0].GetString().ShouldBe("Hoje");
        }
    }

    [Fact, Trait("Spec", "RF-032-CA2")]
    public async Task Rules_AlsoApplyToTheFallback()
    {
        var (admin, ring) = await ConfiguredAsync();
        using (admin)
        {
            await CreateRuleAsync(admin, new { name = "Grátis PAC", priority = 1, isActive = true, effect = "FreeShipping", serviceCodes = OnlyPac });
            Factory.ShippingQuotes.QuoteFailure = ShippingErrors.QuoteUnavailable;

            var quote = await QuoteAsync(ring);

            quote.GetProperty("isFallback").GetBoolean().ShouldBeTrue();
            quote.GetProperty("options")[0].GetProperty("price").GetDecimal().ShouldBe(0m);
            quote.GetProperty("options")[1].GetProperty("price").GetDecimal().ShouldBe(45m);
        }
    }

    [Fact, Trait("Spec", "RF-058")]
    public async Task Rule_IsListedEditedAndDeleted()
    {
        using var admin = await AdminAsync();
        var id = await CreateRuleAsync(admin, new
        {
            name = "  Sul e Sudeste  ",
            priority = 5,
            isActive = true,
            validFrom = "2026-11-01",
            validTo = "2026-11-30",
            minSubtotal = 100m,
            states = SoutheastTyped,
            postalCodeRanges = new[] { new { from = "01000-000", to = "19999999" } },
            serviceCodes = OnlyPac,
            effect = "PercentDiscount",
            effectValue = 50m,
        });

        var listed = (await (await admin.GetAsync(Rules)).Content.ReadFromJsonAsync<JsonElement>(CancellationToken))[0];
        listed.GetProperty("id").GetGuid().ShouldBe(id);
        listed.GetProperty("name").GetString().ShouldBe("Sul e Sudeste");
        listed.GetProperty("validTo").GetString().ShouldBe("2026-11-30");
        listed.GetProperty("states").EnumerateArray().Select(s => s.GetString()).ShouldBe(["SP", "RJ"]);
        listed.GetProperty("postalCodeRanges")[0].GetProperty("from").GetString().ShouldBe("01000000");
        listed.GetProperty("postalCodeRanges")[0].GetProperty("to").GetString().ShouldBe("19999999");
        listed.GetProperty("effectValue").GetDecimal().ShouldBe(50m);

        (await admin.PutAsync($"{Rules}/{id}", new { name = "Embalagem", priority = 1, isActive = false, effect = "Surcharge", effectValue = 3m })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var edited = (await (await admin.GetAsync(Rules)).Content.ReadFromJsonAsync<JsonElement>(CancellationToken))[0];
        edited.GetProperty("name").GetString().ShouldBe("Embalagem");
        edited.GetProperty("isActive").GetBoolean().ShouldBeFalse();
        edited.GetProperty("states").GetArrayLength().ShouldBe(0);
        edited.GetProperty("validTo").ValueKind.ShouldBe(JsonValueKind.Null);

        (await admin.DeleteAsync($"{Rules}/{id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await admin.DeleteAsync($"{Rules}/{id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact, Trait("Spec", "RF-058")]
    public async Task Rule_Unknown_Returns404_OnEdit()
    {
        using var admin = await AdminAsync();

        (await admin.PutAsync($"{Rules}/{Guid.CreateVersion7()}", FreeAbove(10m))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    public static TheoryData<string> InvalidRules => new()
    {
        """{"name":"x","priority":1,"isActive":true,"effect":"FreeShipping"}""",
        """{"name":"Regra","priority":-1,"isActive":true,"effect":"FreeShipping"}""",
        """{"name":"Regra","priority":1,"isActive":true,"effect":"Sorteio"}""",
        """{"name":"Regra","priority":1,"isActive":true,"effect":"freeshipping"}""",
        """{"name":"Regra","priority":1,"isActive":true,"effect":"PercentDiscount"}""",
        """{"name":"Regra","priority":1,"isActive":true,"effect":"PercentDiscount","effectValue":101}""",
        """{"name":"Regra","priority":1,"isActive":true,"effect":"FreeShipping","effectValue":10}""",
        """{"name":"Regra","priority":1,"isActive":true,"effect":"FreeShipping","states":["ZZ"]}""",
        """{"name":"Regra","priority":1,"isActive":true,"effect":"FreeShipping","serviceCodes":["JADLOG"]}""",
        """{"name":"Regra","priority":1,"isActive":true,"effect":"FreeShipping","minSubtotal":-1}""",
        """{"name":"Regra","priority":1,"isActive":true,"effect":"FreeShipping","validFrom":"2026-11-30","validTo":"2026-11-01"}""",
        """{"name":"Regra","priority":1,"isActive":true,"effect":"FreeShipping","postalCodeRanges":[{"from":"99999999","to":"10000000"}]}""",
    };

    [Theory, Trait("Spec", "RF-058")]
    [MemberData(nameof(InvalidRules))]
    public async Task Rule_Invalid_Returns400(string body)
    {
        using var admin = await AdminAsync();
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

        (await admin.PostContentAsync(Rules, content)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact, Trait("Spec", "RF-058")]
    public async Task Rules_AreOnlyForAdmins()
    {
        (await Client.GetAsync(Rules, CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var customer = await AuthClient.RegisteredAsync(Factory, "cliente@loja.teste");
        (await customer.PostAsync(Rules, FreeAbove(10m))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
