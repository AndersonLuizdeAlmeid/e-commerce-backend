using ECommerce.Api.Extensions;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Application.Catalog.Admin;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace ECommerce.Api.Endpoints.Admin;

internal sealed record UpdateProductImageRequest(string Alt, Guid? ColorId);

internal sealed record ReorderProductImagesRequest(Guid? ColorId, IReadOnlyList<Guid> ImageIds);

/// <summary><c>/api/v1/admin/products/{id}/images</c> — RF-061 (arquivo 07 §9).</summary>
internal sealed class AdminProductImageEndpoints : IEndpointGroup
{
    public void Map(IEndpointRouteBuilder app)
    {
        var images = app.MapGroup("/admin/products/{id:guid}/images")
            .WithTags("Admin · Catálogo")
            .RequireAuthorization(Policies.Admin)
            .RequireRateLimiting(RateLimitPolicies.AdminWrite);

        images.MapPost("/", Upload)
            .WithName("AdminUploadProductImage")
            .WithSummary("Envia uma foto: JPEG, PNG, WebP ou HEIC, até 15 MB (RF-061)")
            .WithDescription("Multipart com `file`, `alt` e `colorId` opcional. Um arquivo por requisição. Sai sem EXIF/GPS, em WebP.")
            .WithMetadata(new RequestSizeLimitAttribute(AdminCollectionEndpoints.MaxImageRequestBytes))
            .DisableAntiforgery(); // Autenticação por Bearer, não por cookie: não há o que forjar.

        images.MapPut("/order", Reorder)
            .WithName("AdminReorderProductImages")
            .WithSummary("Reordena a galeria de um acabamento; a primeira é a capa (RF-061)");

        images.MapPut("/{imageId:guid}", Update)
            .WithName("AdminUpdateProductImage")
            .WithSummary("Edita texto alternativo e acabamento da foto (RF-061)");

        images.MapDelete("/{imageId:guid}", Delete)
            .WithName("AdminDeleteProductImage")
            .WithSummary("Remove a foto; produto na loja não pode ficar sem nenhuma (RF-061)");
    }

    private static async Task<Results<Created<UploadedImageResponse>, ProblemHttpResult>> Upload(
        Guid id,
        IFormFile file,
        [FromForm] string alt,
        [FromForm] Guid? colorId,
        ICommandHandler<UploadProductImageCommand, UploadedImageResponse> handler,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, cancellationToken);

        var result = await handler.Handle(new UploadProductImageCommand(id, buffer.ToArray(), alt, colorId), cancellationToken);
        return result.IsSuccess
            ? TypedResults.Created($"/api/v1/admin/products/{id}/images/{result.Value.Id}", result.Value)
            : result.Error.ToProblem();
    }

    private static Task<Results<NoContent, ProblemHttpResult>> Reorder(
        Guid id, ReorderProductImagesRequest request, ICommandHandler<ReorderProductImagesCommand> handler, CancellationToken cancellationToken) =>
        AdminCategoryEndpoints.NoContentOrProblem(handler.Handle(new ReorderProductImagesCommand(id, request.ColorId, request.ImageIds), cancellationToken));

    private static Task<Results<NoContent, ProblemHttpResult>> Update(
        Guid id, Guid imageId, UpdateProductImageRequest request, ICommandHandler<UpdateProductImageCommand> handler, CancellationToken cancellationToken) =>
        AdminCategoryEndpoints.NoContentOrProblem(handler.Handle(new UpdateProductImageCommand(id, imageId, request.Alt, request.ColorId), cancellationToken));

    private static Task<Results<NoContent, ProblemHttpResult>> Delete(
        Guid id, Guid imageId, ICommandHandler<DeleteProductImageCommand> handler, CancellationToken cancellationToken) =>
        AdminCategoryEndpoints.NoContentOrProblem(handler.Handle(new DeleteProductImageCommand(id, imageId), cancellationToken));
}
