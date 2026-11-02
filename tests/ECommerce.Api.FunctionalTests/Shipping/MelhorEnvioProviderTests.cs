using System.Globalization;
using System.Text.Json;
using ECommerce.Application.Abstractions.Shipping;
using ECommerce.Domain.Shipping;
using ECommerce.Infrastructure.Shipping;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace ECommerce.Api.FunctionalTests.Shipping;

/// <summary>
/// O adaptador contra um servidor simulado, com o pipeline de resiliência de verdade. As respostas seguem a documentação
/// do Melhor Envio; quando houver token, entram respostas gravadas do sandbox (arquivo 22 §6).
/// </summary>
public sealed class MelhorEnvioProviderTests : IDisposable
{
    private const string Path = "/api/v2/me/shipment/calculate";

    private static readonly string[] Services = ["1", "2"];

    private readonly WireMockServer _server = WireMockServer.Start();
    private readonly ServiceProvider _services;

    public MelhorEnvioProviderTests()
    {
        var settings = new ShippingProviderSettings
        {
            Provider = ShippingProviderSettings.MelhorEnvioProvider,
            MelhorEnvio = new ShippingProviderSettings.MelhorEnvioSettings
            {
                BaseUrl = _server.Url + "/",
                Token = "token-de-teste",
                UserAgent = "Loja Teste (contato@loja.teste)",
            },
        };

        var collection = new ServiceCollection();
        collection.AddSingleton(Options.Create(settings));
        collection.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        collection.AddMelhorEnvioQuotes();
        _services = collection.BuildServiceProvider();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IShippingQuoteProvider Provider => _services.GetRequiredService<IShippingQuoteProvider>();

    private static ShippingQuoteRequest Request() => new(
        PostalCode.Rehydrate("01310100"),
        PostalCode.Rehydrate("20040020"),
        new ShippingPackage(720, new PackageDimensions(14m, 30.2m, 40m)),
        379.80m);

    private void Respond(int status, string body) =>
        _server.Given(Request_()).RespondWith(Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json").WithBody(body));

    private static IRequestBuilder Request_() => WireMock.RequestBuilders.Request.Create().WithPath(Path).UsingPost();

    [Fact, Trait("Spec", "22-ADAPTER")]
    public async Task Success_UsesTheCustomValues_AndSendsWhatTheApiAsks()
    {
        Respond(200, """
            [
              { "id": 1, "name": "PAC", "price": "25.00", "custom_price": "21.40", "delivery_time": 8, "custom_delivery_time": 7 },
              { "id": 2, "name": "SEDEX", "price": "40.00", "custom_price": "38.90", "delivery_time": 4, "custom_delivery_time": 3 }
            ]
            """);

        var result = await Provider.QuoteAsync(Request(), Ct);

        result.Value.ShouldBe([
            new ShippingQuote(ShippingServices.Pac, ShippingServices.Pac, 21.40m, 7),
            new ShippingQuote(ShippingServices.Sedex, ShippingServices.Sedex, 38.90m, 3),
        ]);

        var sent = _server.LogEntries.ShouldHaveSingleItem().RequestMessage!;
        sent.Headers!["Authorization"].ShouldContain("Bearer token-de-teste");
        sent.Headers["User-Agent"].ShouldContain("Loja Teste (contato@loja.teste)");

        using var body = JsonDocument.Parse(sent.Body!);
        var root = body.RootElement;
        root.GetProperty("from").GetProperty("postal_code").GetString().ShouldBe("01310100");
        root.GetProperty("to").GetProperty("postal_code").GetString().ShouldBe("20040020");
        root.GetProperty("services").GetString()!.Split(',').ShouldBe(Services);
        var package = root.GetProperty("products")[0];
        package.GetProperty("height").GetInt32().ShouldBe(14);
        package.GetProperty("width").GetInt32().ShouldBe(31);
        package.GetProperty("length").GetInt32().ShouldBe(40);
        package.GetProperty("weight").GetDecimal().ShouldBe(0.72m);
        package.GetProperty("insurance_value").GetDecimal().ShouldBe(379.80m);
    }

    [Fact, Trait("Spec", "22-ADAPTER")]
    public async Task ServiceWithError_IsLeftOut()
    {
        Respond(200, """
            [
              { "id": 1, "name": "PAC", "error": "Transportadora não atende este trecho." },
              { "id": 2, "name": "SEDEX", "price": "40.00", "delivery_time": 4 },
              { "id": 17, "name": "Mini Envios", "price": "10.00", "delivery_time": 9 }
            ]
            """);

        var result = await Provider.QuoteAsync(Request(), Ct);

        var only = result.Value.ShouldHaveSingleItem();
        only.ServiceCode.ShouldBe(ShippingServices.Sedex);
        only.Price.ToString(CultureInfo.InvariantCulture).ShouldBe("40.00");
        only.DeliveryDays.ShouldBe(4);
    }

    [Fact, Trait("Spec", "22-ADAPTER")]
    public async Task NoServiceAvailable_IsUnavailable()
    {
        Respond(200, """[ { "id": 1, "name": "PAC", "error": "Não atende." } ]""");

        (await Provider.QuoteAsync(Request(), Ct)).Error.ShouldBe(ShippingErrors.QuoteUnavailable);
    }

    [Fact, Trait("Spec", "RF-032-CA1")]
    public async Task DestinationRefused_IsAnInvalidPostalCode()
    {
        Respond(422, """{ "message": "The given data was invalid.", "errors": { "to.postal_code": ["O campo to.postal_code é inválido."] } }""");

        (await Provider.QuoteAsync(Request(), Ct)).Error.ShouldBe(ShippingErrors.InvalidPostalCode);
    }

    [Theory, Trait("Spec", "RF-032-CA2")]
    [InlineData(401, """{ "message": "Unauthenticated." }""")]
    [InlineData(422, """{ "message": "The given data was invalid.", "errors": { "from.postal_code": ["inválido"] } }""")]
    [InlineData(500, "erro")]
    [InlineData(200, "não é json")]
    [InlineData(200, """{ "objeto": "fora do formato" }""")]
    public async Task ProviderProblems_AreUnavailable(int status, string body)
    {
        Respond(status, body);

        (await Provider.QuoteAsync(Request(), Ct)).Error.ShouldBe(ShippingErrors.QuoteUnavailable);
    }

    [Fact, Trait("Spec", "22-RESILIENCE")]
    public async Task ServerError_IsRetriedOnce()
    {
        Respond(503, "fora do ar");

        (await Provider.QuoteAsync(Request(), Ct)).Error.ShouldBe(ShippingErrors.QuoteUnavailable);

        _server.LogEntries.Count.ShouldBe(2);
    }

    [Fact, Trait("Spec", "22-RESILIENCE")]
    public async Task SlowProvider_StopsWaiting_AndIsUnavailable()
    {
        _server.Given(Request_()).RespondWith(Response.Create().WithStatusCode(200).WithBody("[]").WithDelay(TimeSpan.FromSeconds(6)));
        var started = TimeProvider.System.GetTimestamp();

        var result = await Provider.QuoteAsync(Request(), Ct);

        result.Error.ShouldBe(ShippingErrors.QuoteUnavailable);
        TimeProvider.System.GetElapsedTime(started).ShouldBeLessThan(TimeSpan.FromSeconds(9));
    }

    public void Dispose()
    {
        _services.Dispose();
        _server.Stop();
        _server.Dispose();
    }
}
