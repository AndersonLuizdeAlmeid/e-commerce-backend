using ECommerce.Api.Extensions;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Application.Catalog.Admin;
using ECommerce.Domain.Abstractions;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ECommerce.Api.Endpoints.Admin;

internal sealed record CategoryRequest(
    Guid? ParentId,
    string Name,
    string? Slug,
    string? Description,
    string? SeoTitle,
    string? SeoDescription,
    string? ColorLabel,
    string? SizeLabel)
{
    public CategoryInput ToInput() => new(ParentId, Name, Slug, Description, SeoTitle, SeoDescription, ColorLabel, SizeLabel);
}

/// <summary><see cref="Version"/> é o que o painel leu; se outra pessoa salvou antes, a API responde 409.</summary>
internal sealed record UpdateCategoryRequest(int Version, CategoryRequest Category);

internal sealed record StatusRequest(bool IsActive);

internal sealed record CreatedResponse(Guid Id);

/// <summary><c>/api/v1/admin/categories</c> — RF-050 (arquivo 07 §9).</summary>
internal sealed class AdminCategoryEndpoints : IEndpointGroup
{
    public void Map(IEndpointRouteBuilder app)
    {
        // Policy no grupo: impossível esquecer em um endpoint novo (arquivo 09 §2).
        var group = app.MapGroup("/admin/categories").WithTags("Admin · Catálogo").RequireAuthorization(Policies.Admin);

        group.MapGet("/", List)
            .WithName("AdminListCategories").WithSummary("Todas as categorias, ativas e inativas, com subcategorias (RF-050)");

        group.MapPost("/", Create)
            .WithName("AdminCreateCategory").WithSummary("Cria categoria ou subcategoria (RF-050)")
            .WithDescription("`slug` vazio gera a partir do nome. `colorLabel`/`sizeLabel` vazios herdam da categoria mãe.")
            .RequireRateLimiting(RateLimitPolicies.AdminWrite);

        group.MapPut("/{id:guid}", Update)
            .WithName("AdminUpdateCategory").WithSummary("Edita texto, SEO, rótulos da grade e categoria mãe (RF-050)")
            .WithDescription("Trocar o slug cria redirect 301 do antigo (RN-025). `version` diferente da atual → 409.")
            .RequireRateLimiting(RateLimitPolicies.AdminWrite);

        group.MapPatch("/{id:guid}/status", SetStatus)
            .WithName("AdminSetCategoryStatus").WithSummary("Ativa ou desativa (RF-050)")
            .RequireRateLimiting(RateLimitPolicies.AdminWrite);

        group.MapDelete("/{id:guid}", Delete)
            .WithName("AdminDeleteCategory").WithSummary("Exclui categoria sem produtos nem subcategorias (RF-050)")
            .RequireRateLimiting(RateLimitPolicies.AdminWrite);
    }

    private static async Task<Results<Ok<IReadOnlyList<AdminCategoryResponse>>, ProblemHttpResult>> List(
        IQueryHandler<ListAdminCategoriesQuery, IReadOnlyList<AdminCategoryResponse>> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new ListAdminCategoriesQuery(), cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<Results<Created<CreatedResponse>, ProblemHttpResult>> Create(
        CategoryRequest request,
        ICommandHandler<CreateCategoryCommand, Guid> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new CreateCategoryCommand(request.ToInput()), cancellationToken);
        return result.IsSuccess
            ? TypedResults.Created($"/api/v1/admin/categories/{result.Value}", new CreatedResponse(result.Value))
            : result.Error.ToProblem();
    }

    private static Task<Results<NoContent, ProblemHttpResult>> Update(
        Guid id, UpdateCategoryRequest request, ICommandHandler<UpdateCategoryCommand> handler, CancellationToken cancellationToken) =>
        NoContentOrProblem(handler.Handle(new UpdateCategoryCommand(id, request.Version, request.Category.ToInput()), cancellationToken));

    private static Task<Results<NoContent, ProblemHttpResult>> SetStatus(
        Guid id, StatusRequest request, ICommandHandler<SetCategoryStatusCommand> handler, CancellationToken cancellationToken) =>
        NoContentOrProblem(handler.Handle(new SetCategoryStatusCommand(id, request.IsActive), cancellationToken));

    private static Task<Results<NoContent, ProblemHttpResult>> Delete(
        Guid id, ICommandHandler<DeleteCategoryCommand> handler, CancellationToken cancellationToken) =>
        NoContentOrProblem(handler.Handle(new DeleteCategoryCommand(id), cancellationToken));

    internal static async Task<Results<NoContent, ProblemHttpResult>> NoContentOrProblem(Task<Result> pending)
    {
        var result = await pending;
        return result.IsSuccess ? TypedResults.NoContent() : result.Error.ToProblem();
    }
}
