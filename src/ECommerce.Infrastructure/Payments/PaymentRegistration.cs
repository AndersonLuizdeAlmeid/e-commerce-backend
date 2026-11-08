using ECommerce.Application.Abstractions.Payments;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

namespace ECommerce.Infrastructure.Payments;

public static class PaymentRegistration
{
    internal static void AddPayments(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var section = configuration.GetSection(PaymentSettings.SectionName);
        services.AddOptions<PaymentSettings>()
            .Bind(section)
            .Validate(
                settings => settings.Provider is PaymentSettings.FakeProvider or PaymentSettings.MercadoPagoProvider,
                "Payments:Provider deve ser 'Fake' ou 'MercadoPago'.")
            .Validate(
                settings => settings.Provider != PaymentSettings.FakeProvider || environment.IsDevelopment() || environment.IsEnvironment("Testing"),
                "O provedor de pagamento falso não pode ser usado fora de Development.")
            .Validate(
                settings => settings.Provider != PaymentSettings.MercadoPagoProvider || !string.IsNullOrWhiteSpace(settings.MercadoPago.AccessToken),
                "Payments:MercadoPago:AccessToken é obrigatório com o provedor MercadoPago.")
            .Validate(settings => settings.MaxInstallments is >= 1 and <= 12, "Payments:MaxInstallments vai de 1 a 12.")
            .ValidateOnStart();

        if (section["Provider"] == PaymentSettings.FakeProvider)
        {
            services.AddSingleton<FakePaymentGateway>();
            services.AddSingleton<IPaymentGateway>(sp => sp.GetRequiredService<FakePaymentGateway>());
            services.AddSingleton<IPaymentWebhookVerifier, RejectingWebhookVerifier>();
        }
        else
        {
            services.AddMercadoPago();
        }
    }

    /// <summary>Público para os testes do adaptador apontarem para um servidor simulado.</summary>
    public static IServiceCollection AddMercadoPago(this IServiceCollection services)
    {
        // Sem nova tentativa automática na criação: quem repete é a cliente, com a mesma chave de idempotência.
        services.AddHttpClient<IPaymentGateway, MercadoPagoGateway>((sp, client) =>
                client.BaseAddress = new Uri(sp.GetRequiredService<IOptions<PaymentSettings>>().Value.MercadoPago.BaseUrl))
            .AddStandardResilienceHandler(options =>
            {
                options.Retry.MaxRetryAttempts = 1;
                options.Retry.ShouldHandle = args => ValueTask.FromResult(
                    args.AttemptNumber == 0 && args.Outcome.Result?.RequestMessage?.Method == HttpMethod.Get
                    && (args.Outcome.Exception is not null || (int?)args.Outcome.Result?.StatusCode >= 500));
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(25);
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
            });
        services.AddSingleton<IPaymentWebhookVerifier, MercadoPagoWebhookVerifier>();
        return services;
    }
}
