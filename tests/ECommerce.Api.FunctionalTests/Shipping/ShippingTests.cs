using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ECommerce.Api.FunctionalTests.Catalog;
using ECommerce.Api.FunctionalTests.Infrastructure;
using ECommerce.Application.Abstractions.Caching;
using ECommerce.Domain.Shipping;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace ECommerce.Api.FunctionalTests.Shipping;

public sealed class ShippingTests(ApiFactory factory) : AdminCatalogTest(factory)
{
    private static object Settings(int handlingDays = 2, string origin = "01310-100") => new
    {
        originPostalCode = origin,
        handlingDays,
        fallbackPacPrice = 25m,
        fallbackSedexPrice = 45m,
        boxWeightGrams = 100,
        boxHeightCm = 4m,
        boxWidthCm = 12m,
        boxLengthCm = 16m,
    };

    private static async Task<Guid> VariantAsync(AuthClient admin, string name)
    {
        var product = await ProductInStoreAsync(admin, name);
        return (await GetProductAsync(admin, product)).GetProperty("variants")[0].GetProperty("id").GetGuid();
    }

    private async Task<(AuthClient Admin, Guid Variant)> ConfiguredAsync(string name = "Anel Liso")
    {
        var admin = await AdminAsync();
        (await admin.PutAsync("/api/v1/admin/shipping/settings", Settings())).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await Factory.Services.GetRequiredService<ICacheService>().RemoveByTagAsync("shipping", CancellationToken);
        return (admin, await VariantAsync(admin, name));
    }

    private Task<HttpResponseMessage> QuoteAsync(string postalCode, Guid variant, int quantity = 1) =>
        Client.PostAsJsonAsync("/api/v1/shipping/quotes", new { postalCode, items = new[] { new { variantId = variant, quantity } } }, CancellationToken);

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken);
    }

    [Fact, Trait("Spec", "RF-032")]
    public async Task Quote_ReturnsPacAndSedex_AddingTheHandlingDays()
    {
        var (admin, ring) = await ConfiguredAsync();
        using (admin)
        {
            var quote = await JsonAsync(await QuoteAsync("20040-020", ring));

            quote.GetProperty("isFallback").GetBoolean().ShouldBeFalse();
            var pac = quote.GetProperty("options")[0];
            pac.GetProperty("serviceCode").GetString().ShouldBe("PAC");
            pac.GetProperty("price").GetDecimal().ShouldBe(21.40m);
            pac.GetProperty("deliveryDays").GetInt32().ShouldBe(9);
            quote.GetProperty("options")[1].GetProperty("deliveryDays").GetInt32().ShouldBe(5);
        }
    }

    [Fact, Trait("Spec", "RF-032")]
    public async Task Quote_UsesPriceAndMeasuresFromTheDatabase()
    {
        var (admin, ring) = await ConfiguredAsync();
        using (admin)
        {
            await JsonAsync(await QuoteAsync("20040020", ring, 2));

            var request = Factory.ShippingQuotes.QuoteRequests.ShouldHaveSingleItem();
            request.Origin.Value.ShouldBe("01310100");
            request.Destination.Value.ShouldBe("20040020");
            request.DeclaredValue.ShouldBe(179.80m);
            request.Package.WeightGrams.ShouldBe(140);
            request.Package.Dimensions.ShouldBe(new PackageDimensions(4m, 12m, 16m));
        }
    }

    [Fact, Trait("Spec", "12-SHIPPING-CACHE")]
    public async Task Quote_SamePackage_IsCached_ButAnotherDeclaredValueIsNot()
    {
        var (admin, ring) = await ConfiguredAsync();
        using (admin)
        {
            var expensive = await VariantAsync(admin, "Anel Caro");
            await Factory.ExecuteSqlAsync("UPDATE catalog.product_variants SET price = 500 WHERE id = @expensive", new { expensive });

            await JsonAsync(await QuoteAsync("20040020", ring));
            await JsonAsync(await QuoteAsync("20040020", ring));
            Factory.ShippingQuotes.QuoteRequests.Count.ShouldBe(1);

            await JsonAsync(await QuoteAsync("20040020", expensive));
            Factory.ShippingQuotes.QuoteRequests.Count.ShouldBe(2);
        }
    }

    [Theory, Trait("Spec", "RF-032-CA1")]
    [InlineData("123")]
    [InlineData("abcdefgh")]
    public async Task Quote_InvalidPostalCode_Returns400(string postalCode)
    {
        var (admin, ring) = await ConfiguredAsync();
        using (admin)
        {
            (await QuoteAsync(postalCode, ring)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            Factory.ShippingQuotes.QuoteRequests.ShouldBeEmpty();
        }
    }

    [Fact, Trait("Spec", "RF-032-CA1")]
    public async Task Quote_DestinationRefusedByTheProvider_Returns400_WithoutFallback()
    {
        var (admin, ring) = await ConfiguredAsync();
        using (admin)
        {
            Factory.ShippingQuotes.QuoteFailure = ShippingErrors.InvalidPostalCode;

            (await QuoteAsync("99999999", ring)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }
    }

    [Fact, Trait("Spec", "RF-032-CA2")]
    public async Task Quote_ProviderDown_UsesTheFallback_AndDoesNotCacheIt()
    {
        var (admin, ring) = await ConfiguredAsync();
        using (admin)
        {
            Factory.ShippingQuotes.QuoteFailure = ShippingErrors.QuoteUnavailable;

            var fallback = await JsonAsync(await QuoteAsync("20040020", ring));

            fallback.GetProperty("isFallback").GetBoolean().ShouldBeTrue();
            fallback.GetProperty("options")[0].GetProperty("price").GetDecimal().ShouldBe(25m);
            fallback.GetProperty("options")[0].GetProperty("deliveryDays").ValueKind.ShouldBe(JsonValueKind.Null);
            fallback.GetProperty("options")[1].GetProperty("price").GetDecimal().ShouldBe(45m);

            Factory.ShippingQuotes.QuoteFailure = null;
            var recovered = await JsonAsync(await QuoteAsync("20040020", ring));

            recovered.GetProperty("isFallback").GetBoolean().ShouldBeFalse();
            recovered.GetProperty("options")[0].GetProperty("price").GetDecimal().ShouldBe(21.40m);
        }
    }

    [Fact, Trait("Spec", "RF-058")]
    public async Task Quote_WithoutSettings_Returns503_WithAClearCode()
    {
        using var admin = await AdminAsync();
        var ring = await VariantAsync(admin, "Anel Liso");

        var response = await QuoteAsync("20040020", ring);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync(CancellationToken)).ShouldContain("Shipping.NotConfigured");
    }

    [Fact, Trait("Spec", "RF-032")]
    public async Task Quote_PieceOutOfTheStore_Returns422()
    {
        var (admin, ring) = await ConfiguredAsync();
        using (admin)
        {
            (await QuoteAsync("20040020", Guid.CreateVersion7())).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

            await Factory.ExecuteSqlAsync("UPDATE catalog.product_variants SET is_active = false WHERE id = @ring", new { ring });
            (await QuoteAsync("20040020", ring)).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        }
    }

    [Fact, Trait("Spec", "RF-032")]
    public async Task Quote_WithoutItems_OrOutOfRange_Returns400()
    {
        var response = await Client.PostAsJsonAsync("/api/v1/shipping/quotes", new { postalCode = "20040020", items = Array.Empty<object>() }, CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await QuoteAsync("20040020", Guid.CreateVersion7(), 100)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact, Trait("Spec", "11-SHIPPING")]
    public async Task Quote_OverTheLimit_Returns429()
    {
        using var limited = Factory.WithWebHostBuilder(builder => builder.UseSetting("RateLimiting:ShippingQuote:PermitLimit", "2"));
        using var client = limited.CreateClient();
        var body = new { postalCode = "123", items = new[] { new { variantId = Guid.CreateVersion7(), quantity = 1 } } };

        for (var i = 0; i < 2; i++)
        {
            (await client.PostAsJsonAsync("/api/v1/shipping/quotes", body, CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        (await client.PostAsJsonAsync("/api/v1/shipping/quotes", body, CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact, Trait("Spec", "22-CEP")]
    public async Task PostalAddress_IsPublicAndCached()
    {
        var address = await JsonAsync(await Client.GetAsync("/api/v1/shipping/postal-codes/30130-000", CancellationToken));
        await JsonAsync(await Client.GetAsync("/api/v1/shipping/postal-codes/30130000", CancellationToken));

        address.GetProperty("city").GetString().ShouldBe("São Paulo");
        address.GetProperty("state").GetString().ShouldBe("SP");
        Factory.ShippingQuotes.AddressCalls.ShouldBe(1);
    }

    [Fact, Trait("Spec", "22-CEP")]
    public async Task PostalAddress_Errors_AreNotCached()
    {
        (await Client.GetAsync("/api/v1/shipping/postal-codes/123", CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        Factory.ShippingQuotes.AddressFailure = ShippingErrors.PostalCodeNotFound;
        (await Client.GetAsync("/api/v1/shipping/postal-codes/40000001", CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        Factory.ShippingQuotes.AddressFailure = ShippingErrors.PostalCodeLookupUnavailable;
        (await Client.GetAsync("/api/v1/shipping/postal-codes/40000001", CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);

        Factory.ShippingQuotes.AddressFailure = null;
        (await Client.GetAsync("/api/v1/shipping/postal-codes/40000001", CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact, Trait("Spec", "RF-058")]
    public async Task Settings_StartEmpty_AreSaved_AndReplaced()
    {
        using var admin = await AdminAsync();
        (await admin.GetAsync("/api/v1/admin/shipping/settings")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await admin.PutAsync("/api/v1/admin/shipping/settings", Settings())).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await admin.PutAsync("/api/v1/admin/shipping/settings", Settings(handlingDays: 5, origin: "20040020"))).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var saved = await JsonAsync(await admin.GetAsync("/api/v1/admin/shipping/settings"));
        saved.GetProperty("originPostalCode").GetString().ShouldBe("20040020");
        saved.GetProperty("handlingDays").GetInt32().ShouldBe(5);
        saved.GetProperty("fallbackPacPrice").GetDecimal().ShouldBe(25m);
        saved.GetProperty("boxLengthCm").GetDecimal().ShouldBe(16m);
    }

    [Fact, Trait("Spec", "RF-058")]
    public async Task Settings_Invalid_Returns400()
    {
        using var admin = await AdminAsync();

        (await admin.PutAsync("/api/v1/admin/shipping/settings", Settings(origin: "123"))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.PutAsync("/api/v1/admin/shipping/settings", Settings(handlingDays: -1))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.GetAsync("/api/v1/admin/shipping/settings")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact, Trait("Spec", "RF-058")]
    public async Task Settings_AreOnlyForAdmins()
    {
        (await Client.GetAsync("/api/v1/admin/shipping/settings", CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var customer = await AuthClient.RegisteredAsync(Factory, "cliente@loja.teste");
        (await customer.PutAsync("/api/v1/admin/shipping/settings", Settings())).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
