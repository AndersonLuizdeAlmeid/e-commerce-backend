using ECommerce.Infrastructure;
using ECommerce.Infrastructure.Shipping;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Options;

namespace ECommerce.Api.FunctionalTests.Shipping;

/// <summary>A loja nunca pode vender com frete inventado, nem subir sem o que o provedor real exige.</summary>
public sealed class ShippingConfigurationTests
{
    private static ShippingProviderSettings Resolve(string environment, params (string Key, string Value)[] settings)
    {
        var values = settings.ToDictionary(setting => setting.Key, setting => (string?)setting.Value);
        values["ConnectionStrings:Database"] = "Host=localhost;Database=x";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(configuration, new HostingEnvironment { EnvironmentName = environment });
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<ShippingProviderSettings>>().Value;
    }

    [Theory, Trait("Spec", "22-CONFIG")]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void FakeProvider_OutsideDevelopment_IsRefused(string environment)
    {
        Should.Throw<OptionsValidationException>(() => Resolve(environment, ("Shipping:Provider", "Fake")));
    }

    [Theory, Trait("Spec", "22-CONFIG")]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void FakeProvider_InDevelopment_IsAccepted(string environment)
    {
        Resolve(environment, ("Shipping:Provider", "Fake")).Provider.ShouldBe("Fake");
    }

    [Fact, Trait("Spec", "22-CONFIG")]
    public void RealProvider_WithoutTokenOrContact_IsRefused()
    {
        Should.Throw<OptionsValidationException>(() => Resolve("Production", ("Shipping:Provider", "MelhorEnvio")));
        Should.Throw<OptionsValidationException>(() => Resolve("Production", ("Shipping:Provider", "MelhorEnvio"), ("Shipping:MelhorEnvio:Token", "t")));
    }

    [Fact, Trait("Spec", "22-CONFIG")]
    public void RealProvider_Complete_IsAccepted()
    {
        var settings = Resolve("Production", ("Shipping:Provider", "MelhorEnvio"), ("Shipping:MelhorEnvio:Token", "t"), ("Shipping:MelhorEnvio:UserAgent", "Loja (contato@loja.teste)"));

        settings.MelhorEnvio.Token.ShouldBe("t");
    }

    [Fact, Trait("Spec", "22-CONFIG")]
    public void UnknownProvider_IsRefused()
    {
        Should.Throw<OptionsValidationException>(() => Resolve("Development", ("Shipping:Provider", "Correios")));
    }
}
