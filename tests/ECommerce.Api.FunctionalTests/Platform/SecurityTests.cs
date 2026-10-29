using System.Net;
using ECommerce.Api.FunctionalTests.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ECommerce.Api.FunctionalTests.Platform;

public sealed class SecurityTests(ApiFactory factory) : FunctionalTest(factory)
{
    /// <summary>
    /// Arquivo 15 §5: todo endpoint exige token, exceto a lista abaixo. Endpoint público novo precisa entrar aqui de
    /// propósito (e passar por revisão).
    /// </summary>
    private static readonly string[] ExpectedAnonymous =
    [
        "GET /api/v1/catalog/categories",
        "GET /api/v1/catalog/categories/{slug}",
        "GET /api/v1/catalog/filters",
        "GET /api/v1/catalog/products",
        "GET /api/v1/catalog/products/{slug}",
        "GET /api/v1/catalog/sitemap",
        "GET /health/live",
        "GET /health/ready",
        "GET /openapi/{documentName}.json",
        "GET /docs/{documentName?}",
        "POST /api/v1/auth/confirm-email",
        "POST /api/v1/auth/forgot-password",
        "POST /api/v1/auth/login",
        "POST /api/v1/auth/logout",
        "POST /api/v1/auth/refresh",
        "POST /api/v1/auth/register",
        "POST /api/v1/auth/reset-password",
    ];

    [Fact, Trait("Spec", "15-AUTHZ-SWEEP")]
    public void Endpoints_WithoutAllowAnonymous_RequireAuthentication()
    {
        var anonymous = Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"])
                .Select(method => $"{method} {endpoint.RoutePattern.RawText}"))
            .Where(route => !route.StartsWith("OPTIONS", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        anonymous.ShouldBe(ExpectedAnonymous.Order(StringComparer.Ordinal).ToArray(), ignoreOrder: false);
    }

    [Fact, Trait("Spec", "11-CA1")]
    public async Task Login_SixthAttemptInAMinute_IsRateLimitedWithRetryAfter()
    {
        using var strict = Factory.WithWebHostBuilder(builder => builder.UseSetting("RateLimiting:AuthStrict:PermitLimit", "5"));
        using var client = strict.CreateClient();

        for (var i = 0; i < 5; i++)
        {
            var response = await client.PostAsync(new Uri("/api/v1/auth/login", UriKind.Relative), Json("{\"email\":\"x@loja.teste\",\"password\":\"Errada@123\"}"), CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        var limited = await client.PostAsync(new Uri("/api/v1/auth/login", UriKind.Relative), Json("{\"email\":\"x@loja.teste\",\"password\":\"Errada@123\"}"), CancellationToken);

        limited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter.ShouldNotBeNull();
        limited.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        // CA3: health nunca recebe 429.
        (await client.GetAsync(new Uri("/health/live", UriKind.Relative), CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact, Trait("Spec", "17-HEADERS")]
    public async Task ApiResponses_CarrySecurityHeaders()
    {
        var response = await Client.GetAsync(new Uri("/api/v1/catalog/categories", UriKind.Relative), CancellationToken);

        response.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);
        response.Headers.GetValues("X-Frame-Options").ShouldBe(["DENY"]);
        response.Headers.GetValues("Content-Security-Policy").Single().ShouldContain("default-src 'none'");
        response.Headers.Contains("Server").ShouldBeFalse();
    }

    private static StringContent Json(string json) => new(json, System.Text.Encoding.UTF8, "application/json");
}
