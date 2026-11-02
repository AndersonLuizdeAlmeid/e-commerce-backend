using ECommerce.Api.Extensions;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Application.Catalog.Admin;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace ECommerce.Api.Endpoints.Admin;

internal sealed record FeaturedRequest(Guid? ProductId, Guid? ColorId);

internal sealed record OccasionRequest(string Name, string? Text, DateOnly StartsOn, DateOnly EndsOn, bool IsActive, IReadOnlyList<Guid>? ProductIds)
{
    public OccasionInput ToInput() => new(Name, Text, StartsOn, EndsOn, IsActive, ProductIds ?? []);
}

internal sealed record UpdateOccasionRequest(int Version, OccasionRequest Occasion);

/// <summary><c>/api/v1/admin/showcase</c> e <c>/occasions</c> — RF-025 (arquivo 07 §9).</summary>
internal sealed class AdminShowcaseEndpoints : IEndpointGroup
{
    public void Map(IEndpointRouteBuilder app)
    {
        var showcase = app.MapGroup("/admin/showcase").WithTags("Admin · Catálogo").RequireAuthorization(Policies.Admin);

        showcase.MapGet("/featured", GetFeatured).WithName("AdminGetFeatured")
            .WithSummary("Peça em destaque da home; `isAvailable` falso avisa que a home mostra outra (RF-025)");
        showcase.MapPut("/featured", SetFeatured).WithName("AdminSetFeatured")
            .WithSummary("Escolhe a peça em destaque e, se quiser, o acabamento (RF-025)")
            .WithDescription("`productId` nulo tira o destaque: a home passa a mostrar a novidade mais recente com estoque.")
            .RequireRateLimiting(RateLimitPolicies.AdminWrite);

        var occasions = app.MapGroup("/admin/occasions").WithTags("Admin · Catálogo").RequireAuthorization(Policies.Admin);

        occasions.MapGet("/", ListOccasions).WithName("AdminListOccasions").WithSummary("Todas as ocasiões, de qualquer período (RF-025)");
        occasions.MapPost("/", CreateOccasion).WithName("AdminCreateOccasion")
            .WithSummary("Cria ocasião (RF-025)")
            .WithDescription("Período ativo sobreposto a outro → 409 (CA2). Datas inclusivas, no horário de Brasília (CA3).")
            .RequireRateLimiting(RateLimitPolicies.AdminWrite);
        occasions.MapPut("/{id:guid}", UpdateOccasion).WithName("AdminUpdateOccasion")
            .WithSummary("Edita ocasião, inclusive a lista ordenada de peças (RF-025)")
            .WithDescription("Período ativo sobreposto a outro → 409. `version` diferente da atual → 409.")
            .RequireRateLimiting(RateLimitPolicies.AdminWrite);
        occasions.MapDelete("/{id:guid}", DeleteOccasion).WithName("AdminDeleteOccasion")
            .WithSummary("Exclui ocasião (RF-025)")
            .RequireRateLimiting(RateLimitPolicies.AdminWrite);
        occasions.MapPut("/{id:guid}/image", SetImage).WithName("AdminSetOccasionImage")
            .WithSummary("Envia a imagem: JPEG, PNG, WebP ou HEIC, até 15 MB (RF-061)")
            .WithMetadata(new RequestSizeLimitAttribute(AdminCollectionEndpoints.MaxImageRequestBytes))
            .DisableAntiforgery() // Autenticação por Bearer, não por cookie: não há o que forjar.
            .RequireRateLimiting(RateLimitPolicies.AdminWrite);
        occasions.MapDelete("/{id:guid}/image", RemoveImage).WithName("AdminRemoveOccasionImage")
            .WithSummary("Remove a imagem (RF-061)")
            .RequireRateLimiting(RateLimitPolicies.AdminWrite);
    }

    private static async Task<Results<Ok<AdminFeaturedResponse>, NoContent, ProblemHttpResult>> GetFeatured(
        IQueryHandler<GetAdminFeaturedQuery, AdminFeaturedResponse?> handler, CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new GetAdminFeaturedQuery(), cancellationToken);
        if (result.IsFailure)
        {
            return result.Error.ToProblem();
        }

        return result.Value is null ? TypedResults.NoContent() : TypedResults.Ok(result.Value);
    }

    private static Task<Results<NoContent, ProblemHttpResult>> SetFeatured(
        FeaturedRequest request, ICommandHandler<SetFeaturedCommand> handler, CancellationToken cancellationToken) =>
        AdminCategoryEndpoints.NoContentOrProblem(handler.Handle(new SetFeaturedCommand(request.ProductId, request.ColorId), cancellationToken));

    private static async Task<Results<Ok<IReadOnlyList<AdminOccasionResponse>>, ProblemHttpResult>> ListOccasions(
        IQueryHandler<ListAdminOccasionsQuery, IReadOnlyList<AdminOccasionResponse>> handler, CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new ListAdminOccasionsQuery(), cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<Results<Created<CreatedResponse>, ProblemHttpResult>> CreateOccasion(
        OccasionRequest request, ICommandHandler<CreateOccasionCommand, Guid> handler, CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new CreateOccasionCommand(request.ToInput()), cancellationToken);
        return result.IsSuccess
            ? TypedResults.Created($"/api/v1/admin/occasions/{result.Value}", new CreatedResponse(result.Value))
            : result.Error.ToProblem();
    }

    private static Task<Results<NoContent, ProblemHttpResult>> UpdateOccasion(
        Guid id, UpdateOccasionRequest request, ICommandHandler<UpdateOccasionCommand> handler, CancellationToken cancellationToken) =>
        AdminCategoryEndpoints.NoContentOrProblem(handler.Handle(new UpdateOccasionCommand(id, request.Version, request.Occasion.ToInput()), cancellationToken));

    private static Task<Results<NoContent, ProblemHttpResult>> DeleteOccasion(
        Guid id, ICommandHandler<DeleteOccasionCommand> handler, CancellationToken cancellationToken) =>
        AdminCategoryEndpoints.NoContentOrProblem(handler.Handle(new DeleteOccasionCommand(id), cancellationToken));

    private static async Task<Results<Ok<ImageUrlResponse>, ProblemHttpResult>> SetImage(
        Guid id, IFormFile file, ICommandHandler<SetOccasionImageCommand, string> handler, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, cancellationToken);

        var result = await handler.Handle(new SetOccasionImageCommand(id, buffer.ToArray()), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(new ImageUrlResponse(result.Value)) : result.Error.ToProblem();
    }

    private static Task<Results<NoContent, ProblemHttpResult>> RemoveImage(
        Guid id, ICommandHandler<RemoveOccasionImageCommand> handler, CancellationToken cancellationToken) =>
        AdminCategoryEndpoints.NoContentOrProblem(handler.Handle(new RemoveOccasionImageCommand(id), cancellationToken));
}
