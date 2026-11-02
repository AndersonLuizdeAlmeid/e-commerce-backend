using ECommerce.Api.Extensions;
using ECommerce.Application.Abstractions.Authentication;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Application.Abstractions.Paging;
using ECommerce.Application.Inventory;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ECommerce.Api.Endpoints.Admin;

internal sealed record StockMovementRequest(string Type, int Quantity, string Reason);

internal sealed record StockScanRequest(string Barcode, int? Quantity, string? Reason);

/// <summary><c>/api/v1/admin/stock</c> — RF-052 e RF-059 (arquivo 07 §9).</summary>
internal sealed class AdminStockEndpoints : IEndpointGroup
{
    public void Map(IEndpointRouteBuilder app)
    {
        var stock = app.MapGroup("/admin/stock").WithTags("Admin · Estoque").RequireAuthorization(Policies.Admin);

        stock.MapGet("/", List).WithName("AdminListStock")
            .WithSummary("Saldo por variação, com busca por nome, SKU ou código de barras (RF-052)")
            .WithDescription("`lowStock` traz só o que está à venda e no limite de estoque baixo ou abaixo dele.");
        stock.MapGet("/{variantId:guid}/movements", ListMovements).WithName("AdminListStockMovements")
            .WithSummary("Histórico de movimentações da variação, da mais recente para a mais antiga (RF-052)");
        stock.MapPost("/{variantId:guid}/movements", Move).WithName("AdminMoveStock")
            .WithSummary("Entrada, saída ou ajuste, com motivo (RF-052)")
            .WithDescription("`type`: `In`, `Out` ou `Adjustment`. `quantity` é o que entra ou sai; em `Adjustment`, o novo saldo. Saldo negativo → 422 (CA1).")
            .RequireRateLimiting(RateLimitPolicies.AdminWrite);
        stock.MapPost("/scan", Scan).WithName("AdminScanStock")
            .WithSummary("Entrada de estoque por leitura de código de barras (RF-059)")
            .WithDescription("Cada chamada soma de novo: é assim que se conta peça por peça. Dígito errado → 400. Código sem variação → 404 (`Catalog.BarcodeNotFound`).")
            .RequireRateLimiting(RateLimitPolicies.AdminWrite);
    }

    private static async Task<Results<Ok<PagedResponse<StockItemResponse>>, ProblemHttpResult>> List(
        IQueryHandler<ListAdminStockQuery, PagedResponse<StockItemResponse>> handler,
        CancellationToken cancellationToken,
        int page = 1,
        int pageSize = ListAdminStockQuery.DefaultPageSize,
        string? search = null,
        bool lowStock = false)
    {
        var result = await handler.Handle(new ListAdminStockQuery(page, pageSize, search, lowStock), cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<Results<Ok<PagedResponse<StockMovementResponse>>, ProblemHttpResult>> ListMovements(
        Guid variantId,
        IQueryHandler<ListStockMovementsQuery, PagedResponse<StockMovementResponse>> handler,
        CancellationToken cancellationToken,
        int page = 1,
        int pageSize = ListStockMovementsQuery.DefaultPageSize)
    {
        var result = await handler.Handle(new ListStockMovementsQuery(variantId, page, pageSize), cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<Results<Ok<StockLevelResponse>, ProblemHttpResult>> Move(
        Guid variantId,
        StockMovementRequest request,
        ICurrentUser currentUser,
        ICommandHandler<MoveStockCommand, StockLevelResponse> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(
            new MoveStockCommand(variantId, request.Type, request.Quantity, request.Reason, currentUser.UserId), cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<Results<Ok<StockScanResponse>, ProblemHttpResult>> Scan(
        StockScanRequest request,
        ICurrentUser currentUser,
        ICommandHandler<ScanStockCommand, StockScanResponse> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(
            new ScanStockCommand(request.Barcode, request.Quantity ?? 1, request.Reason, currentUser.UserId), cancellationToken);
        return result.ToHttpResult();
    }
}
