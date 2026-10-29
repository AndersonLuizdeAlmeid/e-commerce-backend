using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ECommerce.Api.FunctionalTests.Infrastructure;

namespace ECommerce.Api.FunctionalTests.Identity;

public sealed class PasswordAndEmailTests(ApiFactory factory) : FunctionalTest(factory)
{
    private const string NewPassword = "NovaSenha@456";

    [Fact, Trait("Spec", "RF-006-CA1")]
    public async Task ChangePassword_EndsOtherSessionsKeepsCurrentAndSendsSecurityEmail()
    {
        using var phone = await AuthClient.RegisteredAsync(Factory, "maria@loja.teste");
        using var laptop = new AuthClient(Factory);
        (await laptop.LoginAsync("maria@loja.teste")).EnsureSuccessStatusCode();

        var response = await phone.PostAsync("/api/v1/auth/change-password", new { currentPassword = AuthClient.ValidPassword, newPassword = NewPassword });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await laptop.RefreshAsync()).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await phone.RefreshAsync()).StatusCode.ShouldBe(HttpStatusCode.OK);
        await Factory.Emails.WaitForAsync("maria@loja.teste", "senha foi alterada");

        using var fresh = new AuthClient(Factory);
        (await fresh.LoginAsync("maria@loja.teste", NewPassword)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact, Trait("Spec", "RF-006-CA1")]
    public async Task ChangePassword_WithWrongCurrentPassword_ReturnsValidationError()
    {
        using var client = await AuthClient.RegisteredAsync(Factory, "maria@loja.teste");

        var response = await client.PostAsync("/api/v1/auth/change-password", new { currentPassword = "Errada@123", newPassword = NewPassword });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken)).GetProperty("code").GetString()
            .ShouldBe("Identity.WrongCurrentPassword");
    }

    [Fact, Trait("Spec", "RF-008-CA1")]
    public async Task ForgotPassword_ForUnknownEmail_ReturnsAcceptedAndSendsNothing()
    {
        var response = await Client.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email = "ninguem@loja.teste" }, CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var queued = await Factory.QuerySingleAsync<long>("SELECT count(*) FROM messaging.outbox_messages");
        queued.ShouldBe(0);
    }

    [Fact, Trait("Spec", "RF-008")]
    public async Task ResetPassword_WithEmailedLink_ChangesPasswordOnceAndEndsSessions()
    {
        using var session = await AuthClient.RegisteredAsync(Factory, "maria@loja.teste");

        (await Client.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email = "maria@loja.teste" }, CancellationToken))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var token = FakeEmailSender.TokenFrom(await Factory.Emails.WaitForAsync("maria@loja.teste", "Redefinição"));

        var reset = await Client.PostAsJsonAsync("/api/v1/auth/reset-password", new { token, newPassword = NewPassword }, CancellationToken);
        reset.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Uso único e sessões antigas encerradas (arquivo 23 §5).
        var again = await Client.PostAsJsonAsync("/api/v1/auth/reset-password", new { token, newPassword = "Outra@7890" }, CancellationToken);
        again.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await session.RefreshAsync()).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var fresh = new AuthClient(Factory);
        (await fresh.LoginAsync("maria@loja.teste", NewPassword)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact, Trait("Spec", "RF-008")]
    public async Task ResetPassword_WithExpiredToken_IsRejected()
    {
        (await AuthClient.RegisteredAsync(Factory, "maria@loja.teste")).Dispose();
        (await Client.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email = "maria@loja.teste" }, CancellationToken)).EnsureSuccessStatusCode();
        var token = FakeEmailSender.TokenFrom(await Factory.Emails.WaitForAsync("maria@loja.teste", "Redefinição"));
        await Factory.ExecuteSqlAsync("UPDATE identity.user_tokens SET expires_at = now() - interval '1 minute' WHERE purpose = 'PasswordReset'");

        var reset = await Client.PostAsJsonAsync("/api/v1/auth/reset-password", new { token, newPassword = NewPassword }, CancellationToken);

        reset.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await reset.Content.ReadFromJsonAsync<JsonElement>(CancellationToken)).GetProperty("code").GetString().ShouldBe("Identity.InvalidToken");
    }

    [Fact, Trait("Spec", "RF-009")]
    public async Task ConfirmEmail_WithEmailedLink_MarksEmailConfirmed()
    {
        using var client = await AuthClient.RegisteredAsync(Factory, "maria@loja.teste");
        var token = FakeEmailSender.TokenFrom(await Factory.Emails.WaitForAsync("maria@loja.teste", "Bem-vindo"));

        var response = await Client.PostAsJsonAsync("/api/v1/auth/confirm-email", new { token }, CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var me = await (await client.GetAsync("/api/v1/auth/me")).Content.ReadFromJsonAsync<JsonElement>(CancellationToken);
        me.GetProperty("emailConfirmed").GetBoolean().ShouldBeTrue();
    }

    [Fact, Trait("Spec", "RF-009-CA1")]
    public async Task ResendConfirmation_IssuesNewLinkAndInvalidatesThePreviousOne()
    {
        using var client = await AuthClient.RegisteredAsync(Factory, "maria@loja.teste");
        var oldToken = FakeEmailSender.TokenFrom(await Factory.Emails.WaitForAsync("maria@loja.teste", "Bem-vindo"));
        Factory.Emails.Clear(); // o assunto de boas-vindas também contém "Confirme seu e-mail"

        (await client.PostAsync("/api/v1/auth/resend-confirmation", null)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var newToken = FakeEmailSender.TokenFrom(await Factory.Emails.WaitForAsync("maria@loja.teste", "Confirme seu e-mail"));

        (await Client.PostAsJsonAsync("/api/v1/auth/confirm-email", new { token = oldToken }, CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Client.PostAsJsonAsync("/api/v1/auth/confirm-email", new { token = newToken }, CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.PostAsync("/api/v1/auth/resend-confirmation", null)).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact, Trait("Spec", "23-OUTBOX")]
    public async Task Outbox_AfterSending_MarksProcessedAndRemovesTokenFromPayload()
    {
        (await AuthClient.RegisteredAsync(Factory, "maria@loja.teste")).Dispose();
        await Factory.Emails.WaitForAsync("maria@loja.teste", "Bem-vindo");
        await Task.Delay(500, CancellationToken); // o commit do lote vem logo depois do envio

        var processed = await Factory.QuerySingleAsync<bool>(
            "SELECT processed_at IS NOT NULL AND NOT (payload ? 'secrets') FROM messaging.outbox_messages");
        processed.ShouldBeTrue();
        var logged = await Factory.QuerySingleAsync<string>("SELECT recipient_masked FROM notifications.email_log");
        logged.ShouldBe("m***@loja.teste");
    }

    [Fact, Trait("Spec", "23-OUTBOX")]
    public async Task Outbox_WhenProviderFails_RetriesLater()
    {
        Factory.Emails.FailNext = 1;

        (await AuthClient.RegisteredAsync(Factory, "maria@loja.teste")).Dispose();
        await Task.Delay(1000, CancellationToken);

        var attempts = await Factory.QuerySingleAsync<int>("SELECT attempts FROM messaging.outbox_messages");
        attempts.ShouldBe(1);
        Factory.Emails.Sent.ShouldBeEmpty();

        await Factory.ExecuteSqlAsync("UPDATE messaging.outbox_messages SET next_attempt_at = now()");
        await Factory.Emails.WaitForAsync("maria@loja.teste", "Bem-vindo");
    }
}
