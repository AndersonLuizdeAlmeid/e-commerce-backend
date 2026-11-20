using ECommerce.Infrastructure;
using ECommerce.Infrastructure.Payments;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Options;

namespace ECommerce.Api.FunctionalTests.Payments;

/// <summary>Produção cobra de verdade (entrega 6 §4.2): credencial de teste ou webhook sem assinatura não sobem.</summary>
public sealed class PaymentConfigurationTests
{
    private static PaymentSettings Resolve(string environment, params (string Key, string Value)[] settings)
    {
        var values = settings.ToDictionary(setting => setting.Key, setting => (string?)setting.Value);
        values["ConnectionStrings:Database"] = "Host=localhost;Database=x";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(configuration, new HostingEnvironment { EnvironmentName = environment });
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<PaymentSettings>>().Value;
    }

    private static (string, string)[] MercadoPago(string accessToken, string? webhookSecret = "assinatura") =>
        webhookSecret is null
            ? [("Payments:Provider", "MercadoPago"), ("Payments:MercadoPago:AccessToken", accessToken)]
            : [("Payments:Provider", "MercadoPago"), ("Payments:MercadoPago:AccessToken", accessToken), ("Payments:MercadoPago:WebhookSecret", webhookSecret)];

    [Fact, Trait("Spec", "ENT6-4.2")]
    public void TestCredential_InProduction_IsRefused()
    {
        Should.Throw<OptionsValidationException>(() => Resolve("Production", MercadoPago("TEST-123")))
            .Message.ShouldContain("TEST-");
    }

    [Fact, Trait("Spec", "ENT6-4.2")]
    public void TestCredential_InStaging_IsAccepted() =>
        Resolve("Staging", MercadoPago("TEST-123")).MercadoPago.AccessToken.ShouldBe("TEST-123");

    [Fact, Trait("Spec", "ENT6-4.2")]
    public void Production_WithoutWebhookSecret_IsRefused() =>
        Should.Throw<OptionsValidationException>(() => Resolve("Production", MercadoPago("APP_USR-123", webhookSecret: null)));

    [Fact, Trait("Spec", "ENT6-4.2")]
    public void FakeProvider_InProduction_IsRefused() =>
        Should.Throw<OptionsValidationException>(() => Resolve("Production", ("Payments:Provider", "Fake")));

    // Ambiente de testes da equipe: pagamento simulado até as credenciais do sandbox entrarem.
    [Fact, Trait("Spec", "ENT6-4.2")]
    public void FakeProvider_InStaging_IsAccepted() =>
        Resolve("Staging", ("Payments:Provider", "Fake")).Provider.ShouldBe("Fake");

    [Fact, Trait("Spec", "ENT6-4.2")]
    public void Production_WithRealCredentialAndSecret_IsAccepted() =>
        Resolve("Production", MercadoPago("APP_USR-123")).MercadoPago.AccessToken.ShouldBe("APP_USR-123");
}
