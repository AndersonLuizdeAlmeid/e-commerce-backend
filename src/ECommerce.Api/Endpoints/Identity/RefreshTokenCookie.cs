using ECommerce.Api.Extensions;
using ECommerce.Application.Identity;
using Microsoft.Extensions.Options;

namespace ECommerce.Api.Endpoints.Identity;

/// <summary>Seção <c>Auth:RefreshCookie</c>.</summary>
internal sealed class RefreshCookieSettings
{
    public const string SectionName = "Auth:RefreshCookie";

    public string Name { get; init; } = "ecommerce_refresh";

    /// <summary>Só <c>false</c> nos testes (TestServer é http); localhost no navegador aceita cookie Secure.</summary>
    public bool Secure { get; init; } = true;
}

/// <summary>
/// Refresh token em cookie <c>HttpOnly; Secure; SameSite=Strict; Path=/api/v1/auth</c> (arquivo 08 §1): o
/// JavaScript da loja nunca o enxerga, e ele só vai para as rotas de autenticação.
/// </summary>
internal static class RefreshTokenCookie
{
    public const string Path = "/api/v1/auth";

    public static string? Read(HttpContext context) =>
        context.Request.Cookies.TryGetValue(Settings(context).Name, out var value) ? value : null;

    public static void Write(HttpContext context, AuthSession session)
    {
        var settings = Settings(context);
        context.Response.Cookies.Append(settings.Name, session.RefreshToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = settings.Secure,
            SameSite = SameSiteMode.Strict,
            Path = Path,
            Expires = new DateTimeOffset(session.RefreshTokenExpiresAtUtc, TimeSpan.Zero),
            IsEssential = true,
        });
    }

    public static void Delete(HttpContext context)
    {
        var settings = Settings(context);
        context.Response.Cookies.Delete(settings.Name, new CookieOptions
        {
            HttpOnly = true,
            Secure = settings.Secure,
            SameSite = SameSiteMode.Strict,
            Path = Path,
        });
    }

    private static RefreshCookieSettings Settings(HttpContext context) =>
        context.RequestServices.GetRequiredService<IOptions<RefreshCookieSettings>>().Value;
}

/// <summary>
/// CSRF nas rotas que usam o cookie (refresh e logout, arquivo 08 §5): além do SameSite=Strict, exige o header
/// <c>X-Requested-With: ecommerce-web</c> e, quando houver <c>Origin</c>, que ele esteja na lista do CORS.
/// </summary>
internal sealed class CookieCsrfFilter(IOptions<CorsSettings> cors) : IEndpointFilter
{
    public const string HeaderName = "X-Requested-With";
    public const string HeaderValue = "ecommerce-web";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.HttpContext.Request;
        var origin = request.Headers.Origin.ToString();

        var validHeader = string.Equals(request.Headers[HeaderName].ToString(), HeaderValue, StringComparison.Ordinal);
        var validOrigin = string.IsNullOrEmpty(origin) || cors.Value.AllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase);

        if (!validHeader || !validOrigin)
        {
            return TypedResults.Problem(
                title: "Requisição recusada.",
                statusCode: StatusCodes.Status403Forbidden,
                type: "/errors/identity.csrf",
                extensions: new Dictionary<string, object?> { ["code"] = "Identity.Csrf" });
        }

        return await next(context);
    }
}
