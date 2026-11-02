using ECommerce.Api.Extensions;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Application.Catalog.Admin;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace ECommerce.Api.Endpoints.Admin;

internal sealed record CollectionRequest(
    string Name,
    string? Slug,
    string? Description,
    string? SeoTitle,
    string? SeoDescription,
    string CardTheme,
    int Position)
{
    public CollectionInput ToInput() => new(Name, Slug, Description, SeoTitle, SeoDescription, CardTheme, Position);
}

internal sealed record UpdateCollectionRequest(int Version, CollectionRequest Collection);

internal sealed record ProductIdsRequest(IReadOnlyList<Guid> ProductIds);

internal sealed record AddedResponse(int Added);

internal sealed record ImageUrlResponse(string ImageUrl);

/// <summary><c>/api/v1/admin/collections</c> — RF-024 (arquivo 07 §9).</summary>
internal sealed class AdminCollectionEndpoints : IEndpointGroup
{
    /// <summary>Um pouco acima do limite da foto, para o envelope multipart.</summary>
    internal const long MaxImageRequestBytes = UploadProductImageCommand.MaxBytes + (1024 * 1024);

    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/collections").WithTags("Admin · Catálogo").RequireAuthorization(Policies.Admin);

        group.MapGet("/", List).WithName("AdminListCollections").WithSummary("Todas as coleções, ativas e inativas (RF-024)");
        group.MapPost("/", Create).WithName("AdminCreateCollection")
            .WithSummary("Cria coleção (RF-024)")
            .WithDescription("`slug` vazio gera a partir do nome. `cardTheme`: Ivory ou Graphite.")
            .RequireRateLimiting(RateLimitPolicies.AdminWrite);
        group.MapPut("/{id:guid}", Update).WithName("AdminUpdateCollection")
            .WithSummary("Edita coleção (RF-024)")
            .WithDescription("Trocar o slug cria redirect 301 do antigo (CA4). `version` diferente da atual → 409.")
            .RequireRateLimiting(RateLimitPolicies.AdminWrite);
        group.MapPatch("/{id:guid}/status", SetStatus).WithName("AdminSetCollectionStatus")
            .WithSummary("Ativa ou desativa (RF-024)")
            .RequireRateLimiting(RateLimitPolicies.AdminWrite);
        group.MapDelete("/{id:guid}", Delete).WithName("AdminDeleteCollection")
            .WithSummary("Exclui a coleção; as peças continuam na loja (RF-024 CA6)")
            .RequireRateLimiting(RateLimitPolicies.AdminWrite);
        group.MapPost("/{id:guid}/products", AddProducts).WithName("AdminAddCollectionProducts")
            .WithSummary("Inclui peças em lote; a que já está fica como está (RF-024 CA5)")
            .RequireRateLimiting(RateLimitPolicies.AdminWrite);
        group.MapDelete("/{id:guid}/products", RemoveProducts).WithName("AdminRemoveCollectionProducts")
            .WithSummary("Remove peças em lote (RF-024)")
            .RequireRateLimiting(RateLimitPolicies.AdminWrite);
        group.MapPut("/{id:guid}/image", SetImage).WithName("AdminSetCollectionImage")
            .WithSummary("Envia a capa: JPEG, PNG, WebP ou HEIC, até 15 MB (RF-061)")
            .WithMetadata(new RequestSizeLimitAttribute(MaxImageRequestBytes))
            .DisableAntiforgery() // Autenticação por Bearer, não por cookie: não há o que forjar.
            .RequireRateLimiting(RateLimitPolicies.AdminWrite);
        group.MapDelete("/{id:guid}/image", RemoveImage).WithName("AdminRemoveCollectionImage")
            .WithSummary("Remove a capa (RF-061)")
            .RequireRateLimiting(RateLimitPolicies.AdminWrite);
    }

    private static async Task<Results<Ok<IReadOnlyList<AdminCollectionResponse>>, ProblemHttpResult>> List(
        IQueryHandler<ListAdminCollectionsQuery, IReadOnlyList<AdminCollectionResponse>> handler, CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new ListAdminCollectionsQuery(), cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<Results<Created<CreatedResponse>, ProblemHttpResult>> Create(
        CollectionRequest request, ICommandHandler<CreateCollectionCommand, Guid> handler, CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new CreateCollectionCommand(request.ToInput()), cancellationToken);
        return result.IsSuccess
            ? TypedResults.Created($"/api/v1/admin/collections/{result.Value}", new CreatedResponse(result.Value))
            : result.Error.ToProblem();
    }

    private static Task<Results<NoContent, ProblemHttpResult>> Update(
        Guid id, UpdateCollectionRequest request, ICommandHandler<UpdateCollectionCommand> handler, CancellationToken cancellationToken) =>
        AdminCategoryEndpoints.NoContentOrProblem(handler.Handle(new UpdateCollectionCommand(id, request.Version, request.Collection.ToInput()), cancellationToken));

    private static Task<Results<NoContent, ProblemHttpResult>> SetStatus(
        Guid id, StatusRequest request, ICommandHandler<SetCollectionStatusCommand> handler, CancellationToken cancellationToken) =>
        AdminCategoryEndpoints.NoContentOrProblem(handler.Handle(new SetCollectionStatusCommand(id, request.IsActive), cancellationToken));

    private static Task<Results<NoContent, ProblemHttpResult>> Delete(
        Guid id, ICommandHandler<DeleteCollectionCommand> handler, CancellationToken cancellationToken) =>
        AdminCategoryEndpoints.NoContentOrProblem(handler.Handle(new DeleteCollectionCommand(id), cancellationToken));

    private static async Task<Results<Ok<AddedResponse>, ProblemHttpResult>> AddProducts(
        Guid id, ProductIdsRequest request, ICommandHandler<AddCollectionProductsCommand, int> handler, CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new AddCollectionProductsCommand(id, request.ProductIds), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(new AddedResponse(result.Value)) : result.Error.ToProblem();
    }

    private static Task<Results<NoContent, ProblemHttpResult>> RemoveProducts(
        Guid id, [FromBody] ProductIdsRequest request, ICommandHandler<RemoveCollectionProductsCommand> handler, CancellationToken cancellationToken) =>
        AdminCategoryEndpoints.NoContentOrProblem(handler.Handle(new RemoveCollectionProductsCommand(id, request.ProductIds), cancellationToken));

    private static async Task<Results<Ok<ImageUrlResponse>, ProblemHttpResult>> SetImage(
        Guid id, IFormFile file, ICommandHandler<SetCollectionImageCommand, string> handler, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, cancellationToken);

        var result = await handler.Handle(new SetCollectionImageCommand(id, buffer.ToArray()), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(new ImageUrlResponse(result.Value)) : result.Error.ToProblem();
    }

    private static Task<Results<NoContent, ProblemHttpResult>> RemoveImage(
        Guid id, ICommandHandler<RemoveCollectionImageCommand> handler, CancellationToken cancellationToken) =>
        AdminCategoryEndpoints.NoContentOrProblem(handler.Handle(new RemoveCollectionImageCommand(id), cancellationToken));
}
