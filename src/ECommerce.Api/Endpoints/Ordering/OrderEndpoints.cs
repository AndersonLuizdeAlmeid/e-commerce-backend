using ECommerce.Api.Extensions;
using ECommerce.Application.Abstractions.Authentication;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Application.Abstractions.Paging;
using ECommerce.Application.Ordering;
using ECommerce.Domain.Abstractions;
using ECommerce.Domain.Ordering;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace ECommerce.Api.Endpoints.Ordering;

internal sealed record PlaceOrderGiftRequest(bool Wrap, string? Message);

internal sealed record PlaceOrderRequest(Guid AddressId, string ShippingServiceCode, string? Notes, PlaceOrderGiftRequest? Gift);

internal sealed record OrderingSettingsRequest(decimal GiftWrapPrice);

/// <summary><c>/api/v1/orders</c> — checkout e pedidos do cliente (arquivo 07 §7); valor da embalagem no painel.</summary>
internal sealed class OrderEndpoints : IEndpointGroup
{
    public void Map(IEndpointRouteBuilder app)
    {
        // Policy no grupo: impossível esquecer em um endpoint novo (arquivo 09 §2). Admin não compra (Q06).
        var orders = app.MapGroup("/orders").WithTags("Pedidos").RequireAuthorization(Policies.Customer);

        orders.MapPost("/", Place).WithName("PlaceOrder")
            .WithSummary("Fecha o pedido com a sacola guardada na conta (RF-040, RF-047)")
            .WithDescription(
                "Recalcula o frete e trava o estoque; peça sem saldo → 409 com `items`. Sacola vazia, endereço que não é " +
                "do cliente ou e-mail não confirmado → 422. O cabeçalho `Idempotency-Key` repete a resposta do pedido já " +
                "criado com a mesma chave (200) em vez de criar outro (CA7).")
            .RequireRateLimiting(RateLimitPolicies.CustomerWrite);
        orders.MapGet("/", List).WithName("ListMyOrders")
            .WithSummary("Meus pedidos, mais recentes primeiro (RF-041)");
        orders.MapGet("/{id:guid}", Get).WithName("GetMyOrder")
            .WithSummary("Detalhe do pedido (RF-042)")
            .WithDescription("Pedido de outro cliente → 404.");
        orders.MapPost("/{id:guid}/cancel", Cancel).WithName("CancelMyOrder")
            .WithSummary("Cancela o pedido que ainda espera pagamento (RF-043)")
            .WithDescription("Devolve o estoque e envia o EM-10. Pedido já pago → 422 até a fatia de estornos.")
            .RequireRateLimiting(RateLimitPolicies.CustomerWrite);
        orders.MapGet("/settings", Settings).WithName("GetOrderingSettings")
            .WithSummary("Valor atual da embalagem para presente (RF-047 CA2)");

        var admin = app.MapGroup("/admin/ordering/settings").WithTags("Admin · Pedidos").RequireAuthorization(Policies.Admin);
        admin.MapGet("/", Settings).WithName("AdminGetOrderingSettings")
            .WithSummary("Valor da embalagem para presente");
        admin.MapPut("/", SaveSettings).WithName("AdminSaveOrderingSettings")
            .WithSummary("Muda o valor da embalagem; pedidos já feitos não mudam (RF-047 CA2)")
            .RequireRateLimiting(RateLimitPolicies.AdminWrite);
    }

    private static async Task<Results<Created<PlacedOrderResponse>, Ok<PlacedOrderResponse>, ProblemHttpResult>> Place(
        PlaceOrderRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        ICurrentUser currentUser,
        ICommandHandler<PlaceOrderCommand, PlacedOrderResponse> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(
            new PlaceOrderCommand(
                currentUser.UserId,
                request.AddressId,
                request.ShippingServiceCode ?? string.Empty,
                request.Gift is not null,
                request.Gift?.Wrap ?? false,
                request.Gift?.Message,
                request.Notes,
                idempotencyKey),
            cancellationToken);

        if (result.IsFailure)
        {
            return result.Error is OutOfStockError outOfStock
                ? result.Error.ToProblem(extensions: [new("items", outOfStock.Items)])
                : result.Error.ToProblem();
        }

        return result.Value.Replayed
            ? TypedResults.Ok(result.Value)
            : TypedResults.Created($"/api/v1/orders/{result.Value.Id}", result.Value);
    }

    private static async Task<Results<Ok<PagedResponse<OrderSummaryResponse>>, ProblemHttpResult>> List(
        ICurrentUser currentUser,
        IQueryHandler<ListMyOrdersQuery, PagedResponse<OrderSummaryResponse>> handler,
        CancellationToken cancellationToken,
        int page = 1,
        int pageSize = 10)
    {
        var result = await handler.Handle(new ListMyOrdersQuery(currentUser.UserId, page, pageSize), cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<Results<Ok<OrderResponse>, ProblemHttpResult>> Get(
        Guid id, ICurrentUser currentUser, IQueryHandler<GetMyOrderQuery, OrderResponse> handler, CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new GetMyOrderQuery(currentUser.UserId, id), cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> Cancel(
        Guid id, ICurrentUser currentUser, ICommandHandler<CancelMyOrderCommand> handler, CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new CancelMyOrderCommand(currentUser.UserId, id), cancellationToken);
        return result.IsSuccess ? TypedResults.NoContent() : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<OrderingSettingsResponse>, ProblemHttpResult>> Settings(
        IQueryHandler<GetOrderingSettingsQuery, OrderingSettingsResponse> handler, CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new GetOrderingSettingsQuery(), cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> SaveSettings(
        OrderingSettingsRequest request, ICommandHandler<SaveOrderingSettingsCommand> handler, CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new SaveOrderingSettingsCommand(request.GiftWrapPrice), cancellationToken);
        return result.IsSuccess ? TypedResults.NoContent() : result.Error.ToProblem();
    }
}
