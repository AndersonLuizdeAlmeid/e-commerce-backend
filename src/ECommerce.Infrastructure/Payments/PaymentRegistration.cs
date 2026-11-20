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
                // O ambiente de testes da equipe (Staging) pode usar o falso, com "Simular pagamento aprovado"; produção nunca.
                settings => settings.Provider != PaymentSettings.FakeProvider || !environment.IsProduction(),
                "O provedor de pagamento falso não pode ser usado em Production.")
            .Validate(
                settings => settings.Provider != PaymentSettings.MercadoPagoProvider || !string.IsNullOrWhiteSpace(settings.MercadoPago.AccessToken),
                "Payments:MercadoPago:AccessToken é obrigatório com o provedor MercadoPago.")
            .Validate(settings => settings.MaxInstallments is >= 1 and <= 12, "Payments:MaxInstallments vai de 1 a 12.")
            // Produção cobra de verdade (entrega 6 §4.2): credencial de teste e webhook sem assinatura não sobem.
            .Validate(
                settings => !environment.IsProduction() || settings.Provider != PaymentSettings.MercadoPagoProvider
                    || !settings.MercadoPago.AccessToken.StartsWith("TEST-", StringComparison.Ordinal),
                "Em Production, Payments:MercadoPago:AccessToken precisa ser a credencial de produção, não a de teste (TEST-...).")
            .Validate(
                settings => !environment.IsProduction() || settings.Provider != PaymentSettings.MercadoPagoProvider
                    || !string.IsNullOrWhiteSpace(settings.MercadoPago.WebhookSecret),
                "Em Production, Payments:MercadoPago:WebhookSecret é obrigatório: sem ele nenhuma notificação é aceita.")
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
