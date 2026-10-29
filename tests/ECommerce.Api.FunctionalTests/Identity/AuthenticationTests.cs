using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ECommerce.Api.FunctionalTests.Infrastructure;

namespace ECommerce.Api.FunctionalTests.Identity;

public sealed class AuthenticationTests(ApiFactory factory) : FunctionalTest(factory)
{
    [Fact, Trait("Spec", "RF-001-CA1")]
    public async Task Register_WithValidData_CreatesCustomerLogsInAndSendsWelcomeEmail()
    {
        using var client = new AuthClient(Factory);

        var response = await client.RegisterAsync("maria@loja.teste");

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        response.Headers.Location?.OriginalString.ShouldBe("/api/v1/auth/me");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken);
        body.GetProperty("accessToken").GetString().ShouldNotBeNullOrWhiteSpace();
        body.GetProperty("user").GetProperty("role").GetString().ShouldBe("Customer");
        body.GetProperty("user").GetProperty("emailConfirmed").GetBoolean().ShouldBeFalse();
        body.TryGetProperty("refreshToken", out _).ShouldBeFalse(); // só no cookie

        var setCookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(AuthClient.CookieName, StringComparison.Ordinal));
        setCookie.ShouldContain("httponly", Case.Insensitive);
        setCookie.ShouldContain("samesite=strict", Case.Insensitive);
        setCookie.ShouldContain("path=/api/v1/auth", Case.Insensitive);

        var email = await Factory.Emails.WaitForAsync("maria@loja.teste", "Bem-vindo");
        email.TextBody.ShouldContain("https://loja.teste/confirmar-email?token=");
    }

    [Theory, Trait("Spec", "RF-001-CA2")]
    [InlineData("maria@loja.teste")]
    [InlineData("  MARIA@Loja.Teste ")] // CA4: normalizado antes de comparar
    public async Task Register_WithExistingEmail_ReturnsGenericConflict(string secondEmail)
    {
        using var first = await AuthClient.RegisteredAsync(Factory, "maria@loja.teste");
        using var second = new AuthClient(Factory);

        var response = await second.RegisterAsync(secondEmail);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Problem(response)).GetProperty("code").GetString().ShouldBe("Identity.EmailAlreadyInUse");
    }

    [Theory, Trait("Spec", "RF-001-CA3")]
    [InlineData("curta1A")]
    [InlineData("semnumeroA")]
    [InlineData("SEMMINUSCULA1")]
    [InlineData("semmaiuscula1")]
    public async Task Register_WithWeakPassword_ReturnsValidationErrorOnPassword(string password)
    {
        using var client = new AuthClient(Factory);

        var response = await client.RegisterAsync("maria@loja.teste", password);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Problem(response)).GetProperty("errors").TryGetProperty("password", out _).ShouldBeTrue();
    }

    [Fact, Trait("Spec", "RF-001-CA5")]
    public async Task Register_IgnoresRoleSentInBody()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/v1/auth/register",
            new { fullName = "Maria da Silva", email = "maria@loja.teste", password = AuthClient.ValidPassword, phone = "11987654321", role = "Admin" },
            CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken);
        body.GetProperty("user").GetProperty("role").GetString().ShouldBe("Customer");
    }

    [Fact, Trait("Spec", "RF-001")]
    public async Task Register_WithInvalidCpf_ReturnsValidationErrorOnCpf()
    {
        using var client = new AuthClient(Factory);

        var response = await client.RegisterAsync("maria@loja.teste", cpf: "111.111.111-11");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Problem(response)).GetProperty("errors").TryGetProperty("cpf", out _).ShouldBeTrue();
    }

    [Fact, Trait("Spec", "RF-002-CA1")]
    public async Task Login_WithValidCredentials_ReturnsAccessTokenAndRefreshCookie()
    {
        (await AuthClient.RegisteredAsync(Factory, "maria@loja.teste")).Dispose();
        using var client = new AuthClient(Factory);

        var response = await client.LoginAsync("Maria@Loja.teste");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        client.AccessToken.ShouldNotBeNull();
        client.RefreshCookie.ShouldNotBeNull();

        var me = await client.GetAsync("/api/v1/auth/me");
        me.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await me.Content.ReadFromJsonAsync<JsonElement>(CancellationToken)).GetProperty("fullName").GetString().ShouldBe("Maria da Silva");
    }

    [Theory, Trait("Spec", "RF-002-CA2")]
    [InlineData("maria@loja.teste", "SenhaErrada1")]
    [InlineData("ninguem@loja.teste", AuthClient.ValidPassword)]
    public async Task Login_WithInvalidCredentials_ReturnsSameGenericUnauthorized(string email, string password)
    {
        (await AuthClient.RegisteredAsync(Factory, "maria@loja.teste")).Dispose();
        using var client = new AuthClient(Factory);

        var response = await client.LoginAsync(email, password);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var problem = await Problem(response);
        problem.GetProperty("code").GetString().ShouldBe("Identity.InvalidCredentials");
        problem.GetProperty("title").GetString().ShouldBe("E-mail ou senha inválidos.");
    }

    [Fact, Trait("Spec", "RF-002-CA3")]
    public async Task Login_AfterFiveFailures_LocksAccountEvenWithCorrectPassword()
    {
        (await AuthClient.RegisteredAsync(Factory, "maria@loja.teste")).Dispose();
        using var client = new AuthClient(Factory);

        for (var i = 0; i < 5; i++)
        {
            (await client.LoginAsync("maria@loja.teste", "SenhaErrada1")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        (await client.LoginAsync("maria@loja.teste")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var lockout = await Factory.QuerySingleAsync<DateTime?>("SELECT lockout_end_utc FROM identity.users");
        lockout.ShouldNotBeNull();
    }

    [Fact, Trait("Spec", "RF-002-CA4")]
    public async Task Login_WhenUserIsInactive_ReturnsUnauthorized()
    {
        (await AuthClient.RegisteredAsync(Factory, "maria@loja.teste")).Dispose();
        await Factory.ExecuteSqlAsync("UPDATE identity.users SET is_active = false");
        using var client = new AuthClient(Factory);

        (await client.LoginAsync("maria@loja.teste")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact, Trait("Spec", "RF-003-CA1")]
    public async Task Refresh_RotatesTheRefreshToken()
    {
        using var client = await AuthClient.RegisteredAsync(Factory, "maria@loja.teste");
        var original = client.RefreshCookie;

        var response = await client.RefreshAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        client.RefreshCookie.ShouldNotBeNull();
        client.RefreshCookie.ShouldNotBe(original);
        (await client.GetAsync("/api/v1/auth/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact, Trait("Spec", "RF-003-CA2")]
    public async Task Refresh_WhenRevokedTokenIsReused_RevokesTheWholeFamily()
    {
        using var client = await AuthClient.RegisteredAsync(Factory, "maria@loja.teste");
        var stolen = client.RefreshCookie;
        (await client.RefreshAsync()).StatusCode.ShouldBe(HttpStatusCode.OK);
        var legitimate = client.RefreshCookie;

        client.RefreshCookie = stolen;
        (await client.RefreshAsync()).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        client.RefreshCookie = legitimate;
        (await client.RefreshAsync()).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact, Trait("Spec", "08-CSRF")]
    public async Task Refresh_WithoutCsrfHeader_IsForbidden()
    {
        using var client = await AuthClient.RegisteredAsync(Factory, "maria@loja.teste");

        var response = await client.RefreshAsync(withCsrfHeader: false);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact, Trait("Spec", "RF-004-CA1")]
    public async Task Logout_RevokesRefreshTokenAndClearsCookie()
    {
        using var client = await AuthClient.RegisteredAsync(Factory, "maria@loja.teste");
        var token = client.RefreshCookie;

        var response = await client.LogoutAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        client.RefreshCookie.ShouldBeNull();

        client.RefreshCookie = token;
        (await client.RefreshAsync()).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact, Trait("Spec", "09-401")]
    public async Task Me_WithoutToken_ReturnsUnauthorizedWithBearerChallenge()
    {
        var response = await Client.GetAsync(new Uri("/api/v1/auth/me", UriKind.Relative), CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().ShouldContain("Bearer");
    }

    private static async Task<JsonElement> Problem(HttpResponseMessage response)
    {
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        return await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
    }
}
