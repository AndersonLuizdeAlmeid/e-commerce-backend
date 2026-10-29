using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ECommerce.Api.FunctionalTests.Infrastructure;

/// <summary>
/// Cliente HTTP que controla o cookie de refresh "na mão" (HandleCookies = false), para os testes poderem
/// reenviar um cookie antigo (reuso) ou simular dois aparelhos.
/// </summary>
public sealed class AuthClient(ApiFactory factory) : IDisposable
{
    public const string ValidPassword = "Senha@123";
    public const string CookieName = "ecommerce_refresh";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public HttpClient Http { get; } = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    public string? AccessToken { get; private set; }
    public string? RefreshCookie { get; set; }

    public Task<HttpResponseMessage> RegisterAsync(string email, string password = ValidPassword, string? cpf = null, string fullName = "Maria da Silva") =>
        SendAndCaptureAsync(HttpMethod.Post, "/api/v1/auth/register",
            new { fullName, email, password, phone = "(11) 98765-4321", cpf });

    public Task<HttpResponseMessage> LoginAsync(string email, string password = ValidPassword) =>
        SendAndCaptureAsync(HttpMethod.Post, "/api/v1/auth/login", new { email, password });

    public Task<HttpResponseMessage> RefreshAsync(bool withCsrfHeader = true) =>
        SendAndCaptureAsync(HttpMethod.Post, "/api/v1/auth/refresh", null, withCsrfHeader);

    public Task<HttpResponseMessage> LogoutAsync() =>
        SendAndCaptureAsync(HttpMethod.Post, "/api/v1/auth/logout", null, csrf: true);

    public Task<HttpResponseMessage> PostAsync(string path, object? body) => SendAndCaptureAsync(HttpMethod.Post, path, body);

    public Task<HttpResponseMessage> PutAsync(string path, object? body) => SendAndCaptureAsync(HttpMethod.Put, path, body);

    public Task<HttpResponseMessage> GetAsync(string path) => SendAndCaptureAsync(HttpMethod.Get, path, null);

    /// <summary>Cadastra e devolve o cliente já logado.</summary>
    public static async Task<AuthClient> RegisteredAsync(ApiFactory factory, string email, string? cpf = null)
    {
        var client = new AuthClient(factory);
        var response = await client.RegisterAsync(email, cpf: cpf);
        response.EnsureSuccessStatusCode();
        return client;
    }

    private async Task<HttpResponseMessage> SendAndCaptureAsync(HttpMethod method, string path, object? body, bool csrf = false)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (AccessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
        }

        if (RefreshCookie is not null)
        {
            request.Headers.Add("Cookie", $"{CookieName}={RefreshCookie}");
        }

        if (csrf)
        {
            request.Headers.Add("X-Requested-With", "ecommerce-web");
        }

        var response = await Http.SendAsync(request, Ct);
        await CaptureSessionAsync(response);
        return response;
    }

    private async Task CaptureSessionAsync(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            foreach (var cookie in cookies.Where(c => c.StartsWith(CookieName + "=", StringComparison.Ordinal)))
            {
                var value = cookie[(CookieName.Length + 1)..].Split(';')[0];
                RefreshCookie = value.Length == 0 ? null : value;
            }
        }

        if (response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType == "application/json")
        {
            var json = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
            if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("accessToken", out var token))
            {
                AccessToken = token.GetString();
            }
        }
    }

    public void Dispose() => Http.Dispose();
}
