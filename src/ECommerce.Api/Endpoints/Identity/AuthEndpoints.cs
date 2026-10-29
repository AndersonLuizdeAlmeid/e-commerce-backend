using ECommerce.Api.Extensions;
using ECommerce.Application.Abstractions.Authentication;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Application.Identity;
using ECommerce.Application.Identity.ChangePassword;
using ECommerce.Application.Identity.ConfirmEmail;
using ECommerce.Application.Identity.ForgotPassword;
using ECommerce.Application.Identity.GetMe;
using ECommerce.Application.Identity.Login;
using ECommerce.Application.Identity.Logout;
using ECommerce.Application.Identity.RefreshSession;
using ECommerce.Application.Identity.Register;
using ECommerce.Application.Identity.ResetPassword;
using ECommerce.Domain.Abstractions;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ECommerce.Api.Endpoints.Identity;

// Requests próprios por caso de uso (arquivo 17, mass assignment): ninguém manda role, id ou IP pelo corpo.
internal sealed record RegisterRequest(string FullName, string Email, string Password, string Phone, string? Cpf);

internal sealed record LoginRequest(string Email, string Password);

internal sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

internal sealed record ForgotPasswordRequest(string Email);

internal sealed record ResetPasswordRequest(string Token, string NewPassword);

internal sealed record ConfirmEmailRequest(string Token);

/// <summary><c>/api/v1/auth</c> (arquivo 07 §2).</summary>
internal sealed class AuthEndpoints : IEndpointGroup
{
    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/auth").WithTags("Auth");

        group.MapPost("/register", Register)
            .WithName("Register").WithSummary("Cadastro de cliente, já com a sessão iniciada (RF-001)")
            .AllowAnonymous().RequireRateLimiting(RateLimitPolicies.AuthStrict);

        group.MapPost("/login", Login)
            .WithName("Login").WithSummary("Login (RF-002)")
            .AllowAnonymous().RequireRateLimiting(RateLimitPolicies.AuthStrict);

        group.MapPost("/refresh", Refresh)
            .WithName("RefreshSession").WithSummary("Renova a sessão pelo cookie, com rotação (RF-003)")
            .WithDescription("Exige o header `X-Requested-With: ecommerce-web`.")
            .AllowAnonymous().RequireRateLimiting(RateLimitPolicies.AuthRefresh)
            .AddEndpointFilter<CookieCsrfFilter>();

        group.MapPost("/logout", Logout)
            .WithName("Logout").WithSummary("Encerra a sessão do cookie (RF-004)")
            .WithDescription("Funciona mesmo com o access token expirado. Exige o header `X-Requested-With: ecommerce-web`.")
            .AllowAnonymous().RequireRateLimiting(RateLimitPolicies.AuthRefresh)
            .AddEndpointFilter<CookieCsrfFilter>();

        group.MapPost("/change-password", ChangePassword)
            .WithName("ChangePassword").WithSummary("Troca de senha; encerra as outras sessões (RF-006)")
            .RequireRateLimiting(RateLimitPolicies.AuthStrict);

        group.MapPost("/forgot-password", ForgotPassword)
            .WithName("ForgotPassword").WithSummary("Envia o link de redefinição; sempre 202 (RF-008)")
            .AllowAnonymous().RequireRateLimiting(RateLimitPolicies.AuthStrict);

        group.MapPost("/reset-password", ResetPassword)
            .WithName("ResetPassword").WithSummary("Redefine a senha pelo link do e-mail (RF-008)")
            .AllowAnonymous().RequireRateLimiting(RateLimitPolicies.AuthStrict);

        group.MapPost("/confirm-email", ConfirmEmail)
            .WithName("ConfirmEmail").WithSummary("Confirma o e-mail pelo link (RF-009)")
            .AllowAnonymous().RequireRateLimiting(RateLimitPolicies.AuthRefresh);

        group.MapPost("/resend-confirmation", ResendConfirmation)
            .WithName("ResendEmailConfirmation").WithSummary("Reenvia o link de confirmação (RF-009 CA1)")
            .RequireRateLimiting(RateLimitPolicies.AuthStrict);

        group.MapGet("/me", GetMe)
            .WithName("GetMe").WithSummary("Usuário logado");
    }

    private static async Task<Results<Created<SessionResponse>, ProblemHttpResult>> Register(
        RegisterRequest request,
        HttpContext http,
        ICommandHandler<RegisterCommand, AuthSession> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(
            new RegisterCommand(request.FullName, request.Email, request.Password, request.Phone, request.Cpf, ClientIp(http)),
            cancellationToken);

        if (result.IsFailure)
        {
            return result.Error.ToProblem();
        }

        RefreshTokenCookie.Write(http, result.Value);
        return TypedResults.Created("/api/v1/auth/me", SessionResponse.From(result.Value));
    }

    private static async Task<Results<Ok<SessionResponse>, ProblemHttpResult>> Login(
        LoginRequest request,
        HttpContext http,
        ICommandHandler<LoginCommand, AuthSession> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new LoginCommand(request.Email, request.Password, ClientIp(http)), cancellationToken);
        return WithSession(http, result);
    }

    private static async Task<Results<Ok<SessionResponse>, ProblemHttpResult>> Refresh(
        HttpContext http,
        ICommandHandler<RefreshSessionCommand, AuthSession> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new RefreshSessionCommand(RefreshTokenCookie.Read(http), ClientIp(http)), cancellationToken);
        if (result.IsFailure)
        {
            RefreshTokenCookie.Delete(http);
        }

        return WithSession(http, result);
    }

    private static async Task<NoContent> Logout(
        HttpContext http,
        ICommandHandler<LogoutCommand> handler,
        CancellationToken cancellationToken)
    {
        await handler.Handle(new LogoutCommand(RefreshTokenCookie.Read(http)), cancellationToken);
        RefreshTokenCookie.Delete(http);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<SessionResponse>, ProblemHttpResult>> ChangePassword(
        ChangePasswordRequest request,
        HttpContext http,
        ICurrentUser currentUser,
        ICommandHandler<ChangePasswordCommand, AuthSession> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(
            new ChangePasswordCommand(currentUser.UserId, request.CurrentPassword, request.NewPassword, ClientIp(http)),
            cancellationToken);
        return WithSession(http, result);
    }

    private static async Task<Accepted> ForgotPassword(
        ForgotPasswordRequest request,
        ICommandHandler<ForgotPasswordCommand> handler,
        CancellationToken cancellationToken)
    {
        // CA1: a resposta é a mesma exista ou não a conta.
        await handler.Handle(new ForgotPasswordCommand(request.Email), cancellationToken);
        return TypedResults.Accepted((string?)null);
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> ResetPassword(
        ResetPasswordRequest request,
        ICommandHandler<ResetPasswordCommand> handler,
        CancellationToken cancellationToken) =>
        ToNoContent(await handler.Handle(new ResetPasswordCommand(request.Token, request.NewPassword), cancellationToken));

    private static async Task<Results<NoContent, ProblemHttpResult>> ConfirmEmail(
        ConfirmEmailRequest request,
        ICommandHandler<ConfirmEmailCommand> handler,
        CancellationToken cancellationToken) =>
        ToNoContent(await handler.Handle(new ConfirmEmailCommand(request.Token), cancellationToken));

    private static async Task<Results<Accepted, ProblemHttpResult>> ResendConfirmation(
        ICurrentUser currentUser,
        ICommandHandler<ResendEmailConfirmationCommand> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new ResendEmailConfirmationCommand(currentUser.UserId), cancellationToken);
        return result.IsSuccess ? TypedResults.Accepted((string?)null) : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<MeResponse>, ProblemHttpResult>> GetMe(
        ICurrentUser currentUser,
        IQueryHandler<GetMeQuery, MeResponse> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new GetMeQuery(currentUser.UserId), cancellationToken);
        return result.ToHttpResult();
    }

    private static Results<Ok<SessionResponse>, ProblemHttpResult> WithSession(HttpContext http, Result<AuthSession> result)
    {
        if (result.IsFailure)
        {
            return result.Error.ToProblem();
        }

        RefreshTokenCookie.Write(http, result.Value);
        return TypedResults.Ok(SessionResponse.From(result.Value));
    }

    private static Results<NoContent, ProblemHttpResult> ToNoContent(Result result) =>
        result.IsSuccess ? TypedResults.NoContent() : result.Error.ToProblem();

    private static string? ClientIp(HttpContext http) => http.Connection.RemoteIpAddress?.ToString();
}
