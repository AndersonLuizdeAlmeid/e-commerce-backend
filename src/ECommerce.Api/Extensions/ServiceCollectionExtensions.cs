using System.Diagnostics;
using ECommerce.Api.Middlewares;
using Serilog;
using Serilog.Formatting.Compact;

namespace ECommerce.Api.Extensions;

internal static class ServiceCollectionExtensions
{
    /// <summary>Serilog: console legível em Development, JSON estruturado nos demais (arquivo 14 §1).</summary>
    public static IServiceCollection AddStructuredLogging(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment) =>
        services.AddSerilog((serviceProvider, logger) =>
        {
            logger
                .ReadFrom.Configuration(configuration)
                .ReadFrom.Services(serviceProvider)
                .Enrich.FromLogContext()
                .Enrich.WithProperty("Environment", environment.EnvironmentName);

            if (environment.IsDevelopment())
            {
                logger.WriteTo.Console(formatProvider: System.Globalization.CultureInfo.InvariantCulture);
            }
            else
            {
                logger.WriteTo.Console(new RenderedCompactJsonFormatter());
            }
        });

    /// <summary>ProblemDetails (RFC 9457) sempre com traceId e correlationId (arquivo 13 §4).</summary>
    public static IServiceCollection AddApiProblemDetails(this IServiceCollection services) =>
        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            var http = context.HttpContext;
            context.ProblemDetails.Instance ??= http.Request.Path;
            context.ProblemDetails.Extensions["traceId"] = Activity.Current?.Id ?? http.TraceIdentifier;

            if (http.Items.TryGetValue(CorrelationIdMiddleware.ItemKey, out var correlationId))
            {
                context.ProblemDetails.Extensions["correlationId"] = correlationId;
            }
        });
}
