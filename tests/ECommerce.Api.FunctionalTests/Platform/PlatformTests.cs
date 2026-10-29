using System.Net;
using ECommerce.Api.FunctionalTests.Infrastructure;

namespace ECommerce.Api.FunctionalTests.Platform;

public sealed class PlatformTests(ApiFactory factory) : FunctionalTest(factory)
{
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task HealthEndpoints_WhenDatabaseIsMigrated_ReturnOk(string path)
    {
        var response = await Client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact, Trait("Spec", "CORS-CA1")]
    public async Task Preflight_FromAllowedOrigin_ReturnsCorsHeadersWithCredentials()
    {
        using var request = Preflight(ApiFactory.AllowedOrigin);

        var response = await Client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        response.Headers.GetValues("Access-Control-Allow-Origin").ShouldBe([ApiFactory.AllowedOrigin]);
        response.Headers.GetValues("Access-Control-Allow-Credentials").ShouldBe(["true"]);
    }

    [Fact, Trait("Spec", "CORS-CA2")]
    public async Task Preflight_FromUnknownOrigin_ReturnsNoCorsHeaders()
    {
        using var request = Preflight("https://site-malicioso.exemplo");

        var response = await Client.SendAsync(request, TestContext.Current.CancellationToken);

        response.Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();
    }

    [Fact]
    public async Task Request_WithCorrelationId_EchoesItInResponse()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.Add("X-Correlation-Id", "abc-123");

        var response = await Client.SendAsync(request, TestContext.Current.CancellationToken);

        response.Headers.GetValues("X-Correlation-Id").ShouldBe(["abc-123"]);
    }

    [Fact]
    public async Task Request_WithUnsafeCorrelationId_GetsANewOne()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", "<script>");

        var response = await Client.SendAsync(request, TestContext.Current.CancellationToken);

        response.Headers.GetValues("X-Correlation-Id").Single().ShouldNotBe("<script>");
    }

    private static HttpRequestMessage Preflight(string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/catalog/categories");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "GET");
        return request;
    }
}
