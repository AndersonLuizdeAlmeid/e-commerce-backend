using ECommerce.Api.Extensions;
using ECommerce.Application.Abstractions.Authentication;
using ECommerce.Application.Abstractions.Caching;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Application.Abstractions.Paging;
using ECommerce.Application.Dashboard;
using ECommerce.Application.Identity.AdminUsers;
using ECommerce.Application.Payments;
using ECommerce.Domain.Abstractions;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ECommerce.Api.Endpoints.Admin;

internal sealed record RefundNoteRequest(string? Note);

internal sealed record UserStatusRequest(bool IsActive);

internal sealed record UserRoleRequest(string? Role);

/// <summary>
/// <c>/api/v1/admin</c> — estornos pendentes (5.2), usuários (RF-054, 5.3) e dashboard (RF-055, 5.4), arquivo 07 §9.
/// </summary>
internal sealed class AdminBackofficeEndpoints : IEndpointGroup
{
    public void Map(IEndpointRouteBuilder app)
    {
        var refunds = app.MapGroup("/admin/payments").WithTags("Admin · Estornos").RequireAuthorization(Policies.Admin);
        refunds.MapGet("/refunds/pending", ListPendingRefunds).WithName("AdminListPendingRefunds")
            .WithSummary("Dinheiro preso: estorno recusado, estorno que não saiu e pagamento manual de pedido cancelado")
            .WithDescription("`kind`: `RefundFailed`, `RefundNotDone` ou `ManualPayment`. `actions` diz o que vale para cada linha.");
        refunds.MapPost("/{id:guid}/refund", RetryRefund).WithName("AdminRetryRefund")
            .WithSummary("Pede o estorno de novo pelo Outbox")
            .WithDescription("Pagamento fora da fila → 422 `Payments.NothingToRefund`.")
            .RequireRateLimiting(RateLimitPolicies.AdminWrite);
        refunds.MapPost("/{id:guid}/sync", SyncPayment).WithName("AdminSyncPayment")
            .WithSummary("Consulta a plataforma: estorno feito pelo painel do Mercado Pago tira o pagamento da fila")
            .RequireRateLimiting(RateLimitPolicies.AdminWrite);
        refunds.MapPost("/{id:guid}/mark-refunded", MarkRefunded).WithName("AdminMarkPaymentRefunded")
            .WithSummary("A loja devolveu à mão um pagamento confirmado à mão; manda o EM-13")
            .WithDescription("Só para pagamento `Manual` (422 `Payments.ManualOnly` nos outros). `note` diz como foi devolvido.")
            .RequireRateLimiting(RateLimitPolicies.AdminWrite);

        var users = app.MapGroup("/admin/users").WithTags("Admin · Usuários").RequireAuthorization(Policies.Admin);
        users.MapGet("/", ListUsers).WithName("AdminListUsers")
            .WithSummary("Usuários, mais recentes primeiro (RF-054)")
            .WithDescription("`role`: `Customer` ou `Admin`. `search`: nome ou e-mail.");
        users.MapPatch("/{id:guid}/status", SetUserStatus).WithName("AdminSetUserStatus")
            .WithSummary("Ativa ou desativa a conta; desativar encerra as sessões na hora")
            .WithDescription("A própria conta → 422 `Identity.CannotChangeSelf` (CA1). A última administradora ativa → 422 `Identity.LastAdmin` (CA2).")
            .RequireRateLimiting(RateLimitPolicies.AdminWrite);
        users.MapPatch("/{id:guid}/role", SetUserRole).WithName("AdminSetUserRole")
            .WithSummary("Promove ou rebaixa; as sessões caem para o papel novo valer")
            .WithDescription("`role`: `Customer` ou `Admin`. Mesmas regras do status (CA1, CA2).")
            .RequireRateLimiting(RateLimitPolicies.AdminWrite);

        app.MapGet("/admin/dashboard", Dashboard).WithTags("Admin · Dashboard").RequireAuthorization(Policies.Admin)
            .WithName("AdminDashboard")
            .WithSummary("Indicadores do período e o que espera a loja agora (RF-055)")
            .WithDescription(
                "`from`/`to`: dias (AAAA-MM-DD) no horário de Brasília; sem datas, os últimos 30 dias. Faturamento = pagamentos " +
                "aprovados no período que continuam aprovados; o estornado vem à parte.");
    }

    private static async Task<Results<Ok<IReadOnlyList<PendingRefundResponse>>, ProblemHttpResult>> ListPendingRefunds(
        IQueryHandler<ListPendingRefundsQuery, IReadOnlyList<PendingRefundResponse>> handler, CancellationToken cancellationToken) =>
        (await handler.Handle(new ListPendingRefundsQuery(), cancellationToken)).ToHttpResult();

    private static async Task<Results<NoContent, ProblemHttpResult>> RetryRefund(
        Guid id, ICommandHandler<RetryRefundCommand> handler, CancellationToken cancellationToken) =>
        NoContentOr(await handler.Handle(new RetryRefundCommand(id), cancellationToken));

    private static async Task<Results<NoContent, ProblemHttpResult>> SyncPayment(
        Guid id, ICommandHandler<SyncPaymentWithProviderCommand> handler, CancellationToken cancellationToken) =>
        NoContentOr(await handler.Handle(new SyncPaymentWithProviderCommand(id), cancellationToken));

    private static async Task<Results<NoContent, ProblemHttpResult>> MarkRefunded(
        Guid id, RefundNoteRequest request, ICurrentUser currentUser, ICommandHandler<MarkPaymentRefundedCommand> handler, CancellationToken cancellationToken) =>
        NoContentOr(await handler.Handle(new MarkPaymentRefundedCommand(id, request.Note, currentUser.UserId), cancellationToken));

    private static async Task<Results<Ok<PagedResponse<AdminUserResponse>>, ProblemHttpResult>> ListUsers(
        ICurrentUser currentUser,
        IQueryHandler<ListAdminUsersQuery, PagedResponse<AdminUserResponse>> handler,
        CancellationToken cancellationToken,
        int page = 1,
        int pageSize = ListAdminUsersQuery.DefaultPageSize,
        string? role = null,
        bool? isActive = null,
        string? search = null)
    {
        var result = await handler.Handle(
            new ListAdminUsersQuery(
                currentUser.UserId, page, pageSize, string.IsNullOrWhiteSpace(role) ? null : role.Trim(), isActive,
                string.IsNullOrWhiteSpace(search) ? null : search.Trim()),
            cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<Results<Ok<AdminUserResponse>, ProblemHttpResult>> SetUserStatus(
        Guid id,
        UserStatusRequest request,
        ICurrentUser currentUser,
        ICommandHandler<SetUserActiveCommand> handler,
        IQueryHandler<GetAdminUserQuery, AdminUserResponse> user,
        ICacheService cache,
        CancellationToken cancellationToken) =>
        await ThenGetUserAsync(
            id, currentUser, await handler.Handle(new SetUserActiveCommand(id, request.IsActive, currentUser.UserId), cancellationToken), user, cache, cancellationToken);

    private static async Task<Results<Ok<AdminUserResponse>, ProblemHttpResult>> SetUserRole(
        Guid id,
        UserRoleRequest request,
        ICurrentUser currentUser,
        ICommandHandler<SetUserRoleCommand> handler,
        IQueryHandler<GetAdminUserQuery, AdminUserResponse> user,
        ICacheService cache,
        CancellationToken cancellationToken) =>
        await ThenGetUserAsync(
            id, currentUser, await handler.Handle(new SetUserRoleCommand(id, request.Role ?? string.Empty, currentUser.UserId), cancellationToken), user, cache, cancellationToken);

    /// <summary>
    /// Depois do commit, apaga o stamp em cache: o token antigo cai no próximo request, sem esperar o minuto do cache
    /// (entrega 5 §3, P7).
    /// </summary>
    private static async Task<Results<Ok<AdminUserResponse>, ProblemHttpResult>> ThenGetUserAsync(
        Guid id, ICurrentUser currentUser, Result result, IQueryHandler<GetAdminUserQuery, AdminUserResponse> user, ICacheService cache, CancellationToken cancellationToken)
    {
        if (result.IsFailure)
        {
            return result.Error.ToProblem();
        }

        await cache.RemoveByTagAsync($"user:{id}", cancellationToken);
        return (await user.Handle(new GetAdminUserQuery(currentUser.UserId, id), cancellationToken)).ToHttpResult();
    }

    private static async Task<Results<Ok<DashboardResponse>, ProblemHttpResult>> Dashboard(
        IQueryHandler<GetDashboardQuery, DashboardResponse> handler, CancellationToken cancellationToken, DateOnly? from = null, DateOnly? to = null) =>
        (await handler.Handle(new GetDashboardQuery(from, to), cancellationToken)).ToHttpResult();

    private static Results<NoContent, ProblemHttpResult> NoContentOr(Result result) =>
        result.IsSuccess ? TypedResults.NoContent() : result.Error.ToProblem();
}
