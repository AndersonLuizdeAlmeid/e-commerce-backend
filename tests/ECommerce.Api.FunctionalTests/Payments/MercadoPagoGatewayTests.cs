using System.Text.Json;
using ECommerce.Application.Abstractions.Payments;
using ECommerce.Domain.Payments;
using ECommerce.Infrastructure.Payments;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace ECommerce.Api.FunctionalTests.Payments;

/// <summary>
/// O adaptador do Mercado Pago contra um servidor simulado, com o pipeline HTTP de verdade. As respostas seguem a
/// documentação da API de pagamentos; quando houver credencial de teste, entram respostas gravadas do sandbox.
/// </summary>
public sealed class MercadoPagoGatewayTests : IDisposable
{
    private const string Secret = "segredo-de-teste";

    private readonly WireMockServer _server = WireMockServer.Start();
    private readonly ServiceProvider _services;

    public MercadoPagoGatewayTests()
    {
        var settings = new PaymentSettings
        {
            Provider = PaymentSettings.MercadoPagoProvider,
            MercadoPago = new PaymentSettings.MercadoPagoSettings { BaseUrl = _server.Url + "/", AccessToken = "TEST-token", WebhookSecret = Secret },
        };

        var collection = new ServiceCollection();
        collection.AddSingleton(Options.Create(settings));
        collection.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        collection.AddMercadoPago();
        _services = collection.BuildServiceProvider();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IPaymentGateway Gateway => _services.GetRequiredService<IPaymentGateway>();

    private IPaymentWebhookVerifier Verifier => _services.GetRequiredService<IPaymentWebhookVerifier>();

    private static CreatePaymentRequest PixRequest() => new(
        "chave-1",
        Guid.Parse("01a1bb3a-9567-7909-acd1-3267efc661cc"),
        "PED-000002",
        108.86m,
        PaymentMethod.Pix,
        new PaymentPayer("maria@loja.teste", "Maria", "da Silva", null),
        new DateTime(2026, 11, 10, 12, 55, 40, DateTimeKind.Utc));

    private const string PixResponse = """
        {
          "id": 1325874632,
          "status": "pending",
          "status_detail": "pending_waiting_transfer",
          "transaction_amount": 108.86,
          "external_reference": "01a1bb3a-9567-7909-acd1-3267efc661cc",
          "date_of_expiration": "2026-11-10T09:55:40.000-03:00",
          "date_approved": null,
          "point_of_interaction": {
            "type": "PIX",
            "transaction_data": { "qr_code": "00020126580014br.gov.bcb.pix", "qr_code_base64": "iVBORw0KGgo=" }
          }
        }
        """;

    private void Respond(string method, string path, int status, string body) =>
        _server.Given(Request.Create().WithPath(path).UsingMethod(method))
            .RespondWith(Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json").WithBody(body));

    [Fact, Trait("Spec", "RF-044-CA2")]
    public async Task Pix_SendsTheExpirationAndIdempotencyKey_AndReadsTheQrCode()
    {
        Respond("POST", "/v1/payments", 201, PixResponse);

        var result = await Gateway.CreatePaymentAsync(PixRequest(), Ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ProviderPaymentId.ShouldBe("1325874632");
        result.Value.Status.ShouldBe(PaymentStatus.Pending);
        result.Value.PixQrCode.ShouldBe("00020126580014br.gov.bcb.pix");
        result.Value.PixQrCodeBase64.ShouldBe("iVBORw0KGgo=");
        result.Value.ExpiresAtUtc.ShouldBe(new DateTime(2026, 11, 10, 12, 55, 40, DateTimeKind.Utc));

        var sent = _server.LogEntries.Single().RequestMessage!;
        var headers = sent.Headers!;
        headers["X-Idempotency-Key"].ShouldContain("chave-1");
        headers["Authorization"].ShouldContain("Bearer TEST-token");
        using var body = JsonDocument.Parse(sent.Body!);
        body.RootElement.GetProperty("payment_method_id").GetString().ShouldBe("pix");
        body.RootElement.GetProperty("transaction_amount").GetDecimal().ShouldBe(108.86m);
        body.RootElement.GetProperty("external_reference").GetString().ShouldBe("01a1bb3a-9567-7909-acd1-3267efc661cc");
        body.RootElement.GetProperty("date_of_expiration").GetString().ShouldBe("2026-11-10T09:55:40.000-03:00");
        body.RootElement.GetProperty("payer").GetProperty("email").GetString().ShouldBe("maria@loja.teste");
        body.RootElement.GetProperty("payer").TryGetProperty("identification", out _).ShouldBeFalse();
    }

    [Fact, Trait("Spec", "RF-044")]
    public async Task Card_SendsTheTokenInstallmentsAndHolderDocument()
    {
        Respond("POST", "/v1/payments", 201, """{"id": 55, "status": "approved", "status_detail": "accredited", "transaction_amount": 108.86}""");

        var result = await Gateway.CreatePaymentAsync(
            PixRequest() with
            {
                Method = PaymentMethod.CreditCard,
                ExpiresAtUtc = null,
                CardToken = "card-token-1",
                PaymentMethodId = "master",
                IssuerId = "24",
                Installments = 3,
                Payer = new PaymentPayer("maria@loja.teste", "Maria", "da Silva", "52998224725", "CPF"),
            },
            Ct);

        result.Value.Status.ShouldBe(PaymentStatus.Approved);
        using var body = JsonDocument.Parse(_server.LogEntries.Single().RequestMessage!.Body!);
        var root = body.RootElement;
        root.GetProperty("token").GetString().ShouldBe("card-token-1");
        root.GetProperty("payment_method_id").GetString().ShouldBe("master");
        root.GetProperty("issuer_id").GetString().ShouldBe("24");
        root.GetProperty("installments").GetInt32().ShouldBe(3);
        root.TryGetProperty("date_of_expiration", out _).ShouldBeFalse();
        var identification = root.GetProperty("payer").GetProperty("identification");
        identification.GetProperty("type").GetString().ShouldBe("CPF");
        identification.GetProperty("number").GetString().ShouldBe("52998224725");
    }

    [Fact, Trait("Spec", "RF-044-CA3")]
    public async Task ClientError_IsARejection_ServerError_IsUnavailable()
    {
        Respond("POST", "/v1/payments", 400, """{"message":"invalid payer","status":400}""");
        (await Gateway.CreatePaymentAsync(PixRequest(), Ct)).Error.ShouldBe(PaymentErrors.ProviderRejected);

        _server.Reset();
        Respond("POST", "/v1/payments", 502, "{}");
        (await Gateway.CreatePaymentAsync(PixRequest(), Ct)).Error.ShouldBe(PaymentErrors.ProviderUnavailable);
    }

    [Theory, Trait("Spec", "entrega-4-P6")]
    [InlineData("approved", null, PaymentStatus.Approved, false)]
    [InlineData("in_process", "pending_contingency", PaymentStatus.Pending, false)]
    [InlineData("rejected", "cc_rejected_other_reason", PaymentStatus.Rejected, false)]
    [InlineData("cancelled", "expired", PaymentStatus.Expired, false)]
    [InlineData("cancelled", "by_collector", PaymentStatus.Cancelled, false)]
    [InlineData("refunded", "refunded", PaymentStatus.Refunded, false)]
    [InlineData("charged_back", "settled", PaymentStatus.Approved, true)]
    public async Task Get_MapsTheStatus(string status, string? detail, PaymentStatus expected, bool disputed)
    {
        Respond("GET", "/v1/payments/77", 200, JsonSerializer.Serialize(new { id = 77, status, status_detail = detail, transaction_amount = 10m }));

        var result = await Gateway.GetPaymentAsync("77", Ct);

        result.Value.Status.ShouldBe(expected);
        result.Value.Disputed.ShouldBe(disputed);
    }

    [Fact]
    public async Task Get_UnknownPayment_IsNotFound()
    {
        Respond("GET", "/v1/payments/404404", 404, """{"message":"Payment not found"}""");

        (await Gateway.GetPaymentAsync("404404", Ct)).Error.ShouldBe(PaymentErrors.PaymentNotFound);
    }

    private static Dictionary<string, string> Headers(string signature, string requestId = "req-1") =>
        new() { ["x-signature"] = signature, ["x-request-id"] = requestId };

    [Fact, Trait("Spec", "RF-045")]
    public void Webhook_WithValidSignature_IsAccepted()
    {
        var check = Verifier.Verify(
            Headers(MercadoPagoSignature.Header(Secret, "1325874632", "req-1", "1794141954")),
            new Dictionary<string, string> { ["data.id"] = "1325874632", ["type"] = "payment" },
            """{"id":998877,"type":"payment","action":"payment.updated","data":{"id":"1325874632"}}""");

        check.SignatureValid.ShouldBeTrue();
        check.Notification!.ProviderPaymentId.ShouldBe("1325874632");
        check.Notification.EventId.ShouldBe("998877");
    }

    [Fact, Trait("Spec", "21-§8")]
    public void Webhook_WithWrongSignature_OrWithoutSecret_IsRefused()
    {
        var body = """{"id":1,"type":"payment","data":{"id":"5"}}""";
        var query = new Dictionary<string, string> { ["data.id"] = "5" };

        Verifier.Verify(Headers(MercadoPagoSignature.Header("outro-segredo", "5", "req-1", "1")), query, body).SignatureValid.ShouldBeFalse();
        Verifier.Verify(Headers(MercadoPagoSignature.Header(Secret, "6", "req-1", "1")), query, body).SignatureValid.ShouldBeFalse();
        Verifier.Verify(Headers("ts=1,v1=zz"), query, body).SignatureValid.ShouldBeFalse();
        Verifier.Verify(new Dictionary<string, string>(), query, body).SignatureValid.ShouldBeFalse();
    }

    [Fact, Trait("Spec", "RF-045")]
    public void Webhook_WithoutRequestId_IsSignedWithoutThatPart()
    {
        var check = Verifier.Verify(
            new Dictionary<string, string> { ["x-signature"] = MercadoPagoSignature.Header(Secret, "123456", null, "1794141954") },
            new Dictionary<string, string> { ["data.id"] = "123456", ["type"] = "payment" },
            """{"id":1,"type":"payment","data":{"id":"123456"}}""");

        check.SignatureValid.ShouldBeTrue();
        check.Notification!.ProviderPaymentId.ShouldBe("123456");
    }

    [Fact, Trait("Spec", "RF-045")]
    public void Webhook_WithTheIdOnlyInTheBody_IsAccepted()
    {
        var check = Verifier.Verify(
            Headers(MercadoPagoSignature.Header(Secret, "777", "req-9", "1794141954"), "req-9"),
            new Dictionary<string, string>(),
            """{"id":2,"type":"payment","data":{"id":"777"}}""");

        check.SignatureValid.ShouldBeTrue();
        check.Notification!.ProviderPaymentId.ShouldBe("777");
    }

    [Fact]
    public void Webhook_AboutSomethingElse_IsIgnored()
    {
        var check = Verifier.Verify(
            Headers(MercadoPagoSignature.Header(Secret, "abc", "req-1", "1")),
            new Dictionary<string, string> { ["data.id"] = "ABC" },
            """{"id":2,"type":"merchant_order","data":{"id":"ABC"}}""");

        check.SignatureValid.ShouldBeTrue();
        check.Notification.ShouldBeNull();
    }

    public void Dispose()
    {
        _server.Stop();
        _server.Dispose();
        _services.Dispose();
    }
}
