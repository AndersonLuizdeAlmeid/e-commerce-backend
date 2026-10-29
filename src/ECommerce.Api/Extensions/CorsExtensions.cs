using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Options;

namespace ECommerce.Api.Extensions;

internal sealed class CorsSettings
{
    public const string SectionName = "Cors";

#pragma warning disable CA1819 // binding de configuração
    public string[] AllowedOrigins { get; init; } = [];
#pragma warning restore CA1819
}

/// <summary>Política "Frontend" (arquivo 10).</summary>
internal static class CorsExtensions
{
    public const string FrontendPolicy = "Frontend";

    public static IServiceCollection AddFrontendCors(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddOptions<CorsSettings>()
            .Bind(configuration.GetSection(CorsSettings.SectionName))
            .Validate(
                settings => environment.IsDevelopment() || settings.AllowedOrigins.Length > 0,
                "Cors:AllowedOrigins não pode ser vazio fora de Development.")
            .ValidateOnStart();

        services.AddCors();
        services.AddOptions<CorsOptions>()
            .Configure<IOptions<CorsSettings>>((cors, settings) => cors.AddPolicy(FrontendPolicy, policy => policy
                .WithOrigins(settings.Value.AllowedOrigins)
                .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE")
                .WithHeaders("Authorization", "Content-Type", "Idempotency-Key", "X-Correlation-Id", "X-Requested-With")
                .WithExposedHeaders("Location", "X-Correlation-Id", "Retry-After", "X-Pagination-Total")
                .AllowCredentials()
                .SetPreflightMaxAge(TimeSpan.FromMinutes(10))));

        return services;
    }
}
