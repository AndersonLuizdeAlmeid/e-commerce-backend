using ECommerce.Api.Extensions;
using ECommerce.Application.Abstractions.Authentication;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Application.Ordering;
using ECommerce.Domain.Abstractions;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ECommerce.Api.Endpoints.Admin;

internal sealed record TrackingCodeRequest(string? TrackingCode);

internal sealed record ReasonRequest(string? Reason);

internal sealed record GiftMessageRequest(string? Message);

internal sealed record ReturnRequest(string? Note);

/// <summary><c>/api/v1/admin/orders</c> — RF-053 e RF-047 CA3 (arquivo 07 §9, entrega 5 fatia 5.1).</summary>
internal sealed class AdminOrderEndpoints : IEndpointGroup
{
    public void Map(IEndpointRouteBuilder app)
    {
        var orders = app.MapGroup("/admin/orders").WithTags("Admin · Pedidos").RequireAuthorization(Policies.Admin);

        orders.MapGet("/", List).WithName("AdminListOrders")
            .WithSummary("Pedidos com filtros e contagem por situação (RF-053)")
            .WithDescription(
                "`status`: uma situação do pedido. `from`/`to`: dias (AAAA-MM-DD) no horário de Brasília, pela data do pedido. " +
                "`search`: número (`PED-000123` ou `123`), nome ou e-mail. `paymentMethod`: `Pix`, `CreditCard` ou `Manual`, " +
                "a forma com que o pedido foi pago. `statusCounts` usa os mesmos filtros, menos a situação.");
        orders.MapGet("/{id:guid}", Get).WithName("AdminGetOrder")
            .WithSummary("Detalhe com cliente, histórico, pagamentos e as ações possíveis agora");

        orders.MapPost("/{id:guid}/start-preparation", StartPreparation).WithName("AdminStartOrderPreparation")
            .WithSummary("Pago → em separação")
            .WithDescription("Outra situação → 422 `Ordering.InvalidTransition` (CA2).")
            .RequireRateLimiting(RateLimitPolicies.AdminWrite);
        orders.MapPost("/{id:guid}/ship", Ship).WithName("AdminShipOrder")
            .WithSummary("Em separação → enviado, com o código de rastreio (CA3); manda o EM-08")
            .WithDescription("Rastreio fora do formato → 400 no campo `trackingCode`. Outra situação → 422.")
            .RequireRateLimiting(RateLimitPolicies.AdminWrite);
        orders.MapPut("/{id:guid}/tracking", UpdateTracking).WithName("AdminUpdateOrderTracking")
            .WithSummary("Corrige o rastreio de pedido enviado, sem novo e-mail")
            .RequireRateLimiting(RateLimitPolicies.AdminWrite);
        orders.MapPost("/{id:guid}/deliver", Deliver).WithName("AdminDeliverOrder")
            .WithSummary("Enviado → entregue; manda o EM-09")
            .RequireRateLimiting(RateLimitPolicies.AdminWrite);
        orders.MapPost("/{id:guid}/cancel", Cancel).WithName("AdminCancelOrder")
            .WithSummary("Cancela pela loja, com motivo; devolve o estoque e estorna o que foi pago")
            .WithDescription("Permitido até em separação. O motivo fica só no histórico; a cliente recebe o EM-10 \"pela loja\".")
            .RequireRateLimiting(RateLimitPolicies.AdminWrite);
        orders.MapPost("/{id:guid}/confirm-payment", ConfirmPayment).WithName("AdminConfirmOrderPayment")
            .WithSummary("Confirma o pagamento à mão, com motivo obrigatório (CA1)")
            .WithDescription("Exceção para pagamento conciliado fora do sistema: grava um pagamento `Manual` e cancela o PIX aberto.")
            .RequireRateLimiting(RateLimitPolicies.AdminWrite);
        orders.MapPost("/{id:guid}/return", Return).WithName("AdminReturnOrder")
            .WithSummary("Entregue → devolvido, com o motivo; estorna o total pelo Outbox")
            .WithDescription(
                "Arrependimento ou troca (entrega 6 §3, P11). A cliente recebe o EM-13 quando o estorno é aceito; pagamento confirmado à mão " +
                "vai para os estornos pendentes. O estoque não volta sozinho: a peça é conferida e entra por ajuste.")
            .RequireRateLimiting(RateLimitPolicies.AdminWrite);
        orders.MapPut("/{id:guid}/gift-message", CorrectGiftMessage).WithName("AdminCorrectGiftMessage")
            .WithSummary("Corrige a dedicatória até o pedido ser enviado (RF-047 CA3)")
            .RequireRateLimiting(RateLimitPolicies.AdminWrite);
    }

    private static async Task<Results<Ok<AdminOrderListResponse>, ProblemHttpResult>> List(
        IQueryHandler<ListAdminOrdersQuery, AdminOrderListResponse> handler,
        CancellationToken cancellationToken,
        int page = 1,
        int pageSize = ListAdminOrdersQuery.DefaultPageSize,
        string? status = null,
        DateOnly? from = null,
        DateOnly? to = null,
        string? search = null,
        string? paymentMethod = null,
        bool gift = false)
    {
        var result = await handler.Handle(
            new ListAdminOrdersQuery(page, pageSize, Blank(status), from, to, Blank(search), Blank(paymentMethod), gift), cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<Results<Ok<AdminOrderResponse>, ProblemHttpResult>> Get(
        Guid id, IQueryHandler<GetAdminOrderQuery, AdminOrderResponse> handler, CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new GetAdminOrderQuery(id), cancellationToken);
        return result.ToHttpResult();
    }

    private static Task<Results<Ok<AdminOrderResponse>, ProblemHttpResult>> StartPreparation(
        Guid id,
        ICurrentUser currentUser,
        ICommandHandler<StartOrderPreparationCommand> handler,
        IQueryHandler<GetAdminOrderQuery, AdminOrderResponse> order,
        CancellationToken cancellationToken) =>
        ThenGet(id, handler.Handle(new StartOrderPreparationCommand(id, currentUser.UserId), cancellationToken), order, cancellationToken);

    private static Task<Results<Ok<AdminOrderResponse>, ProblemHttpResult>> Ship(
        Guid id,
        TrackingCodeRequest request,
        ICurrentUser currentUser,
        ICommandHandler<ShipOrderCommand> handler,
        IQueryHandler<GetAdminOrderQuery, AdminOrderResponse> order,
        CancellationToken cancellationToken) =>
        ThenGet(id, handler.Handle(new ShipOrderCommand(id, request.TrackingCode, currentUser.UserId), cancellationToken), order, cancellationToken);

    private static Task<Results<Ok<AdminOrderResponse>, ProblemHttpResult>> UpdateTracking(
        Guid id,
        TrackingCodeRequest request,
        ICurrentUser currentUser,
        ICommandHandler<UpdateOrderTrackingCommand> handler,
        IQueryHandler<GetAdminOrderQuery, AdminOrderResponse> order,
        CancellationToken cancellationToken) =>
        ThenGet(id, handler.Handle(new UpdateOrderTrackingCommand(id, request.TrackingCode, currentUser.UserId), cancellationToken), order, cancellationToken);

    private static Task<Results<Ok<AdminOrderResponse>, ProblemHttpResult>> Deliver(
        Guid id,
        ICurrentUser currentUser,
        ICommandHandler<DeliverOrderCommand> handler,
        IQueryHandler<GetAdminOrderQuery, AdminOrderResponse> order,
        CancellationToken cancellationToken) =>
        ThenGet(id, handler.Handle(new DeliverOrderCommand(id, currentUser.UserId), cancellationToken), order, cancellationToken);

    private static Task<Results<Ok<AdminOrderResponse>, ProblemHttpResult>> Cancel(
        Guid id,
        ReasonRequest request,
        ICurrentUser currentUser,
        ICommandHandler<AdminCancelOrderCommand> handler,
        IQueryHandler<GetAdminOrderQuery, AdminOrderResponse> order,
        CancellationToken cancellationToken) =>
        ThenGet(id, handler.Handle(new AdminCancelOrderCommand(id, request.Reason, currentUser.UserId), cancellationToken), order, cancellationToken);

    private static Task<Results<Ok<AdminOrderResponse>, ProblemHttpResult>> ConfirmPayment(
        Guid id,
        ReasonRequest request,
        ICurrentUser currentUser,
        ICommandHandler<ConfirmOrderPaymentCommand> handler,
        IQueryHandler<GetAdminOrderQuery, AdminOrderResponse> order,
        CancellationToken cancellationToken) =>
        ThenGet(id, handler.Handle(new ConfirmOrderPaymentCommand(id, request.Reason, currentUser.UserId), cancellationToken), order, cancellationToken);

    private static Task<Results<Ok<AdminOrderResponse>, ProblemHttpResult>> CorrectGiftMessage(
        Guid id,
        GiftMessageRequest request,
        ICurrentUser currentUser,
        ICommandHandler<CorrectGiftMessageCommand> handler,
        IQueryHandler<GetAdminOrderQuery, AdminOrderResponse> order,
        CancellationToken cancellationToken) =>
        ThenGet(id, handler.Handle(new CorrectGiftMessageCommand(id, request.Message, currentUser.UserId), cancellationToken), order, cancellationToken);

    private static Task<Results<Ok<AdminOrderResponse>, ProblemHttpResult>> Return(
        Guid id,
        ReturnRequest request,
        ICurrentUser currentUser,
        ICommandHandler<ReturnOrderCommand> handler,
        IQueryHandler<GetAdminOrderQuery, AdminOrderResponse> order,
        CancellationToken cancellationToken) =>
        ThenGet(id, handler.Handle(new ReturnOrderCommand(id, request.Note, currentUser.UserId), cancellationToken), order, cancellationToken);

    /// <summary>Toda escrita responde o pedido atualizado, para a tela não consultar de novo.</summary>
    private static async Task<Results<Ok<AdminOrderResponse>, ProblemHttpResult>> ThenGet(
        Guid id, Task<Result> command, IQueryHandler<GetAdminOrderQuery, AdminOrderResponse> order, CancellationToken cancellationToken)
    {
        var result = await command;
        if (result.IsFailure)
        {
            return result.Error.ToProblem();
        }

        return (await order.Handle(new GetAdminOrderQuery(id), cancellationToken)).ToHttpResult();
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
