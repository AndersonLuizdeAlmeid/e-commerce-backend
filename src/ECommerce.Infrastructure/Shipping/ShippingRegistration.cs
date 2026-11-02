using ECommerce.Application.Abstractions.Shipping;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

namespace ECommerce.Infrastructure.Shipping;

public static class ShippingRegistration
{
    /// <summary>Tempo de cada tentativa (arquivo 22 §5). O circuito exige uma janela de pelo menos o dobro.</summary>
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(3);

    internal static void AddShipping(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var section = configuration.GetSection(ShippingProviderSettings.SectionName);
        services.AddOptions<ShippingProviderSettings>()
            .Bind(section)
            .Validate(
                settings => settings.Provider is ShippingProviderSettings.FakeProvider or ShippingProviderSettings.MelhorEnvioProvider,
                "Shipping:Provider deve ser 'Fake' ou 'MelhorEnvio'.")
            .Validate(
                settings => settings.Provider != ShippingProviderSettings.FakeProvider || environment.IsDevelopment() || environment.IsEnvironment("Testing"),
                "O provedor de frete falso não pode ser usado fora de Development.")
            .Validate(
                settings => settings.Provider != ShippingProviderSettings.MelhorEnvioProvider
                    || (!string.IsNullOrWhiteSpace(settings.MelhorEnvio.Token) && !string.IsNullOrWhiteSpace(settings.MelhorEnvio.UserAgent)),
                "Shipping:MelhorEnvio:Token e Shipping:MelhorEnvio:UserAgent são obrigatórios com o provedor MelhorEnvio.")
            .ValidateOnStart();

        if (section["Provider"] == ShippingProviderSettings.FakeProvider)
        {
            services.AddSingleton<IShippingQuoteProvider, FakeQuoteProvider>();
        }
        else
        {
            services.AddMelhorEnvioQuotes();
        }

        services.AddHttpClient<IPostalCodeLookup, ViaCepLookup>((sp, client) =>
            client.BaseAddress = new Uri(sp.GetRequiredService<IOptions<ShippingProviderSettings>>().Value.ViaCepBaseUrl))
            .AddStandardResilienceHandler(Resilience);
    }

    /// <summary>Público para os testes do adaptador trocarem o provedor falso pelo real, apontando para um servidor simulado.</summary>
    public static IServiceCollection AddMelhorEnvioQuotes(this IServiceCollection services)
    {
        services.AddHttpClient<IShippingQuoteProvider, MelhorEnvioQuoteProvider>((sp, client) =>
            client.BaseAddress = new Uri(sp.GetRequiredService<IOptions<ShippingProviderSettings>>().Value.MelhorEnvio.BaseUrl))
            .AddStandardResilienceHandler(Resilience);
        return services;
    }

    private static void Resilience(HttpStandardResilienceOptions options)
    {
        options.AttemptTimeout.Timeout = AttemptTimeout;
        options.Retry.MaxRetryAttempts = 1;
        options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(8);
        options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
    }
}
