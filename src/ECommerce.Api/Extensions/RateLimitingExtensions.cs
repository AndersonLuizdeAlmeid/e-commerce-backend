using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.JsonWebTokens;

namespace ECommerce.Api.Extensions;

internal static class RateLimitPolicies
{
    public const string AuthStrict = "auth-strict";
    public const string AuthRefresh = "auth-refresh";
    public const string PublicRead = "public-read";
    public const string CustomerWrite = "customer-write";
}

/// <summary>Seção <c>RateLimiting</c>: valores ajustáveis sem recompilar (arquivo 11 §3).</summary>
internal sealed class RateLimitingSettings
{
    public const string SectionName = "RateLimiting";

    /// <summary>
    /// Segredo enviado pelo servidor do Next.js no header <c>X-Internal-Key</c>: as páginas renderizadas no servidor
    /// chamam a API por muitos visitantes e não podem cair no limite de um IP só (arquivo 11 §3).
    /// </summary>
    public string? InternalKey { get; init; }

    public TokenBucket Global { get; init; } = new(100, 50, 10);
    public FixedWindow AuthStrict { get; init; } = new(5, 60);
    public FixedWindow AuthRefresh { get; init; } = new(20, 60);
    public FixedWindow PublicRead { get; init; } = new(120, 60);
    public TokenBucket CustomerWrite { get; init; } = new(30, 10, 10);

    public sealed record FixedWindow(int PermitLimit, int WindowSeconds);

    public sealed record TokenBucket(int TokenLimit, int TokensPerPeriod, int ReplenishmentSeconds);
}

internal static partial class RateLimitingExtensions
{
    public const string InternalKeyHeader = "X-Internal-Key";

    public static IServiceCollection AddApiRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = configuration.GetSection(RateLimitingSettings.SectionName).Get<RateLimitingSettings>() ?? new RateLimitingSettings();

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = OnRejectedAsync;

            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                IsExempt(context, settings)
                    ? RateLimitPartition.GetNoLimiter("exempt")
                    : RateLimitPartition.GetTokenBucketLimiter(PartitionKey(context), _ => TokenBucket(settings.Global)));

            options.AddPolicy(RateLimitPolicies.AuthStrict, context =>
                RateLimitPartition.GetFixedWindowLimiter(IpKey(context), _ => FixedWindow(settings.AuthStrict)));

            options.AddPolicy(RateLimitPolicies.AuthRefresh, context =>
                RateLimitPartition.GetFixedWindowLimiter(IpKey(context), _ => FixedWindow(settings.AuthRefresh)));

            options.AddPolicy(RateLimitPolicies.PublicRead, context =>
                IsInternal(context, settings)
                    ? RateLimitPartition.GetNoLimiter("internal")
                    : RateLimitPartition.GetSlidingWindowLimiter(IpKey(context), _ => new SlidingWindowRateLimiterOptions
                    {
                        PermitLimit = settings.PublicRead.PermitLimit,
                        Window = TimeSpan.FromSeconds(settings.PublicRead.WindowSeconds),
                        SegmentsPerWindow = 6,
                        QueueLimit = 0,
                    }));

            options.AddPolicy(RateLimitPolicies.CustomerWrite, context =>
                RateLimitPartition.GetTokenBucketLimiter(PartitionKey(context), _ => TokenBucket(settings.CustomerWrite)));
        });

        return services;
    }

    /// <summary>Autenticado: por usuário (sub). Anônimo: por IP (depois do ForwardedHeaders) — arquivo 11 §2.</summary>
    private static string PartitionKey(HttpContext context) =>
        context.User.FindFirstValue(JwtRegisteredClaimNames.Sub) is { } sub ? $"user:{sub}" : IpKey(context);

    private static string IpKey(HttpContext context) => $"ip:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";

    // CA3: health nunca recebe 429.
    private static bool IsExempt(HttpContext context, RateLimitingSettings settings) =>
        context.Request.Path.StartsWithSegments("/health") || IsInternal(context, settings);

    private static bool IsInternal(HttpContext context, RateLimitingSettings settings) =>
        !string.IsNullOrEmpty(settings.InternalKey)
        && context.Request.Headers[InternalKeyHeader] is { Count: 1 } header
        && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(header.ToString()),
            System.Text.Encoding.UTF8.GetBytes(settings.InternalKey));

    private static FixedWindowRateLimiterOptions FixedWindow(RateLimitingSettings.FixedWindow window) => new()
    {
        PermitLimit = window.PermitLimit,
        Window = TimeSpan.FromSeconds(window.WindowSeconds),
        QueueLimit = 0,
    };

    private static TokenBucketRateLimiterOptions TokenBucket(RateLimitingSettings.TokenBucket bucket) => new()
    {
        TokenLimit = bucket.TokenLimit,
        TokensPerPeriod = bucket.TokensPerPeriod,
        ReplenishmentPeriod = TimeSpan.FromSeconds(bucket.ReplenishmentSeconds),
        QueueLimit = 0,
    };

    /// <summary>429 + Retry-After + ProblemDetails (arquivo 11 §4).</summary>
    private static async ValueTask OnRejectedAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var http = context.HttpContext;
        var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var wait) ? (int)Math.Ceiling(wait.TotalSeconds) : 60;
        http.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);

        var logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("RateLimiting");
        LogRejected(logger, http.Request.Path);

        await TypedResults.Problem(
            title: "Muitas requisições",
            detail: $"Limite de requisições excedido. Tente novamente em {retryAfter} segundos.",
            statusCode: StatusCodes.Status429TooManyRequests,
            type: "/errors/rate-limit",
            extensions: new Dictionary<string, object?> { ["code"] = "RateLimit.Exceeded" })
            .ExecuteAsync(http);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rate limit excedido em {Path}")]
    private static partial void LogRejected(ILogger logger, PathString path);
}
