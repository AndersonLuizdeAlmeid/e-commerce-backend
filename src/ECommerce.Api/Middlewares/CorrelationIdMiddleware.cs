using System.Text.RegularExpressions;
using Serilog.Context;

namespace ECommerce.Api.Middlewares;

/// <summary>Lê ou gera o X-Correlation-Id e o propaga para logs e resposta (arquivo 14 §2).</summary>
internal sealed partial class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-Id";
    public const string ItemKey = "CorrelationId";

    public async Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers[HeaderName].ToString();
        var correlationId = SafeCorrelationId().IsMatch(incoming) ? incoming : Guid.NewGuid().ToString("N");

        context.Items[ItemKey] = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty(ItemKey, correlationId))
        {
            await next(context);
        }
    }

    // Aceita apenas valores curtos e seguros para não injetar lixo nos logs.
    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$")]
    private static partial Regex SafeCorrelationId();
}
