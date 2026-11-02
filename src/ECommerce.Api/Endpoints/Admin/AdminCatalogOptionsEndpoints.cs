using ECommerce.Api.Extensions;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Application.Catalog.Admin;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ECommerce.Api.Endpoints.Admin;

internal sealed record ColorRequest(string Name, string Hex);

internal sealed record SizeRequest(string Name, short SortOrder);

/// <summary><c>/api/v1/admin/colors</c> e <c>/sizes</c> — RF-057 (arquivo 07 §9).</summary>
internal sealed class AdminCatalogOptionsEndpoints : IEndpointGroup
{
    public void Map(IEndpointRouteBuilder app)
    {
        var colors = app.MapGroup("/admin/colors").WithTags("Admin · Catálogo").RequireAuthorization(Policies.Admin);
        colors.MapGet("/", ListColors).WithName("AdminListColors").WithSummary("Cores e acabamentos (RF-057)");
        colors.MapPost("/", CreateColor).WithName("AdminCreateColor").WithSummary("Cria cor ou acabamento (RF-057)")
            .RequireRateLimiting(RateLimitPolicies.AdminWrite);
        colors.MapPut("/{id:guid}", UpdateColor).WithName("AdminUpdateColor").WithSummary("Edita cor ou acabamento (RF-057)")
            .RequireRateLimiting(RateLimitPolicies.AdminWrite);
        colors.MapDelete("/{id:guid}", DeleteColor).WithName("AdminDeleteColor").WithSummary("Remove, se não estiver em uso (RF-057)")
            .RequireRateLimiting(RateLimitPolicies.AdminWrite);

        var sizes = app.MapGroup("/admin/sizes").WithTags("Admin · Catálogo").RequireAuthorization(Policies.Admin);
        sizes.MapGet("/", ListSizes).WithName("AdminListSizes").WithSummary("Tamanhos, aros e comprimentos (RF-057)");
        sizes.MapPost("/", CreateSize).WithName("AdminCreateSize").WithSummary("Cria tamanho (RF-057)")
            .RequireRateLimiting(RateLimitPolicies.AdminWrite);
        sizes.MapPut("/{id:guid}", UpdateSize).WithName("AdminUpdateSize").WithSummary("Edita tamanho e ordem (RF-057)")
            .RequireRateLimiting(RateLimitPolicies.AdminWrite);
        sizes.MapDelete("/{id:guid}", DeleteSize).WithName("AdminDeleteSize").WithSummary("Remove, se não estiver em uso (RF-057)")
            .RequireRateLimiting(RateLimitPolicies.AdminWrite);
    }

    private static async Task<Results<Ok<IReadOnlyList<AdminColorResponse>>, ProblemHttpResult>> ListColors(
        IQueryHandler<ListAdminColorsQuery, IReadOnlyList<AdminColorResponse>> handler, CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new ListAdminColorsQuery(), cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<Results<Created<CreatedResponse>, ProblemHttpResult>> CreateColor(
        ColorRequest request, ICommandHandler<CreateColorCommand, Guid> handler, CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new CreateColorCommand(request.Name, request.Hex), cancellationToken);
        return result.IsSuccess
            ? TypedResults.Created($"/api/v1/admin/colors/{result.Value}", new CreatedResponse(result.Value))
            : result.Error.ToProblem();
    }

    private static Task<Results<NoContent, ProblemHttpResult>> UpdateColor(
        Guid id, ColorRequest request, ICommandHandler<UpdateColorCommand> handler, CancellationToken cancellationToken) =>
        AdminCategoryEndpoints.NoContentOrProblem(handler.Handle(new UpdateColorCommand(id, request.Name, request.Hex), cancellationToken));

    private static Task<Results<NoContent, ProblemHttpResult>> DeleteColor(
        Guid id, ICommandHandler<DeleteColorCommand> handler, CancellationToken cancellationToken) =>
        AdminCategoryEndpoints.NoContentOrProblem(handler.Handle(new DeleteColorCommand(id), cancellationToken));

    private static async Task<Results<Ok<IReadOnlyList<AdminSizeResponse>>, ProblemHttpResult>> ListSizes(
        IQueryHandler<ListAdminSizesQuery, IReadOnlyList<AdminSizeResponse>> handler, CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new ListAdminSizesQuery(), cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<Results<Created<CreatedResponse>, ProblemHttpResult>> CreateSize(
        SizeRequest request, ICommandHandler<CreateSizeCommand, Guid> handler, CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new CreateSizeCommand(request.Name, request.SortOrder), cancellationToken);
        return result.IsSuccess
            ? TypedResults.Created($"/api/v1/admin/sizes/{result.Value}", new CreatedResponse(result.Value))
            : result.Error.ToProblem();
    }

    private static Task<Results<NoContent, ProblemHttpResult>> UpdateSize(
        Guid id, SizeRequest request, ICommandHandler<UpdateSizeCommand> handler, CancellationToken cancellationToken) =>
        AdminCategoryEndpoints.NoContentOrProblem(handler.Handle(new UpdateSizeCommand(id, request.Name, request.SortOrder), cancellationToken));

    private static Task<Results<NoContent, ProblemHttpResult>> DeleteSize(
        Guid id, ICommandHandler<DeleteSizeCommand> handler, CancellationToken cancellationToken) =>
        AdminCategoryEndpoints.NoContentOrProblem(handler.Handle(new DeleteSizeCommand(id), cancellationToken));
}
