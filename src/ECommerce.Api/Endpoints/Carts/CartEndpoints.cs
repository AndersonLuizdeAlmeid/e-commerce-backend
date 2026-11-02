using ECommerce.Api.Extensions;
using ECommerce.Application.Abstractions.Authentication;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Application.Carts;
using ECommerce.Domain.Abstractions;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ECommerce.Api.Endpoints.Carts;

internal sealed record CartItemRequest(Guid VariantId, int Quantity);

internal sealed record CartQuantityRequest(int Quantity);

internal sealed record CartLinesRequest(IReadOnlyList<CartItemRequest>? Items)
{
    public IReadOnlyList<CartLine> ToLines() => Items?.Select(item => new CartLine(item.VariantId, item.Quantity)).ToList() ?? [];
}

/// <summary><c>/api/v1/cart</c> — RF-030 e RF-031 (arquivo 07 §6). As escritas devolvem a sacola atualizada.</summary>
internal sealed class CartEndpoints : IEndpointGroup
{
    public void Map(IEndpointRouteBuilder app)
    {
        // Policy no grupo: impossível esquecer em um endpoint novo (arquivo 09 §2).
        var cart = app.MapGroup("/cart").WithTags("Sacola").RequireAuthorization(Policies.Customer);

        cart.MapGet("/", Get).WithName("GetCart")
            .WithSummary("Sacola com os preços atuais e os alertas de estoque (RF-030 CA3)");
        cart.MapPost("/items", Add).WithName("AddCartItem")
            .WithSummary("Adiciona a peça; se já estiver na sacola, soma (RF-030)")
            .WithDescription("Variação inativa ou inexistente → 422 (CA2). Passar de 99 por peça ou de 50 peças diferentes → 422. Não reserva estoque (CA4).")
            .RequireRateLimiting(RateLimitPolicies.CustomerWrite);
        cart.MapPut("/items/{variantId:guid}", SetQuantity).WithName("SetCartItemQuantity")
            .WithSummary("Troca a quantidade da peça (RF-030)")
            .RequireRateLimiting(RateLimitPolicies.CustomerWrite);
        cart.MapDelete("/items/{variantId:guid}", Remove).WithName("RemoveCartItem")
            .WithSummary("Tira a peça da sacola (RF-030)")
            .RequireRateLimiting(RateLimitPolicies.CustomerWrite);
        cart.MapDelete("/", Clear).WithName("ClearCart")
            .WithSummary("Esvazia a sacola (RF-030)")
            .RequireRateLimiting(RateLimitPolicies.CustomerWrite);
        cart.MapPost("/merge", Merge).WithName("MergeCart")
            .WithSummary("Traz a sacola do visitante ao entrar, somando as quantidades (RF-031)")
            .WithDescription("Soma com limite de 99 por peça. O que não entra volta em `skippedVariantIds`: peça fora da loja ou além de 50 peças diferentes.")
            .RequireRateLimiting(RateLimitPolicies.CustomerWrite);

        // Público: o visitante guarda só variação e quantidade, e o catálogo responde o resto (arquivo 16 §6).
        app.MapPost("/catalog/variants/lookup", Lookup)
            .WithTags("Catalog").WithName("LookupCartVariants")
            .WithSummary("Preço, foto e disponibilidade atuais da sacola do visitante")
            .WithDescription("Mesma resposta de `GET /cart`, sem cache. Variação inexistente fica de fora.")
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.PublicRead);
    }

    private static async Task<Results<Ok<CartResponse>, ProblemHttpResult>> Get(
        ICurrentUser currentUser, IQueryHandler<GetCartQuery, CartResponse> handler, CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new GetCartQuery(currentUser.UserId), cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<Results<Ok<CartResponse>, ProblemHttpResult>> Add(
        CartItemRequest request,
        ICurrentUser currentUser,
        ICommandHandler<AddCartItemCommand> handler,
        IQueryHandler<GetCartQuery, CartResponse> cart,
        CancellationToken cancellationToken) =>
        await UpdatedCartAsync(
            handler.Handle(new AddCartItemCommand(currentUser.UserId, request.VariantId, request.Quantity), cancellationToken), currentUser, cart, cancellationToken);

    private static async Task<Results<Ok<CartResponse>, ProblemHttpResult>> SetQuantity(
        Guid variantId,
        CartQuantityRequest request,
        ICurrentUser currentUser,
        ICommandHandler<SetCartItemQuantityCommand> handler,
        IQueryHandler<GetCartQuery, CartResponse> cart,
        CancellationToken cancellationToken) =>
        await UpdatedCartAsync(
            handler.Handle(new SetCartItemQuantityCommand(currentUser.UserId, variantId, request.Quantity), cancellationToken), currentUser, cart, cancellationToken);

    private static async Task<Results<Ok<CartResponse>, ProblemHttpResult>> Remove(
        Guid variantId,
        ICurrentUser currentUser,
        ICommandHandler<RemoveCartItemCommand> handler,
        IQueryHandler<GetCartQuery, CartResponse> cart,
        CancellationToken cancellationToken) =>
        await UpdatedCartAsync(
            handler.Handle(new RemoveCartItemCommand(currentUser.UserId, variantId), cancellationToken), currentUser, cart, cancellationToken);

    private static async Task<Results<NoContent, ProblemHttpResult>> Clear(
        ICurrentUser currentUser, ICommandHandler<ClearCartCommand> handler, CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new ClearCartCommand(currentUser.UserId), cancellationToken);
        return result.IsSuccess ? TypedResults.NoContent() : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<MergeCartResponse>, ProblemHttpResult>> Merge(
        CartLinesRequest request,
        ICurrentUser currentUser,
        ICommandHandler<MergeCartCommand, IReadOnlyList<Guid>> handler,
        IQueryHandler<GetCartQuery, CartResponse> cart,
        CancellationToken cancellationToken)
    {
        var merged = await handler.Handle(new MergeCartCommand(currentUser.UserId, request.ToLines()), cancellationToken);
        if (merged.IsFailure)
        {
            return merged.Error.ToProblem();
        }

        var updated = await cart.Handle(new GetCartQuery(currentUser.UserId), cancellationToken);
        return updated.IsSuccess ? TypedResults.Ok(new MergeCartResponse(updated.Value, merged.Value)) : updated.Error.ToProblem();
    }

    private static async Task<Results<Ok<CartResponse>, ProblemHttpResult>> Lookup(
        CartLinesRequest request, IQueryHandler<LookupCartQuery, CartResponse> handler, CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new LookupCartQuery(request.ToLines()), cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<Results<Ok<CartResponse>, ProblemHttpResult>> UpdatedCartAsync(
        Task<Result> command, ICurrentUser currentUser, IQueryHandler<GetCartQuery, CartResponse> cart, CancellationToken cancellationToken)
    {
        var result = await command;
        if (result.IsFailure)
        {
            return result.Error.ToProblem();
        }

        var updated = await cart.Handle(new GetCartQuery(currentUser.UserId), cancellationToken);
        return updated.ToHttpResult();
    }
}
