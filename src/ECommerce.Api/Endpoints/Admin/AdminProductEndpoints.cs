using ECommerce.Api.Extensions;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Application.Abstractions.Paging;
using ECommerce.Application.Catalog.Admin;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ECommerce.Api.Endpoints.Admin;

internal sealed record ProductRequest(
    Guid CategoryId,
    string Name,
    string? Slug,
    string? Description,
    decimal BasePrice,
    int WeightGrams,
    decimal HeightCm,
    decimal WidthCm,
    decimal LengthCm,
    string? SeoTitle,
    string? SeoDescription)
{
    public ProductInput ToInput() => new(
        CategoryId, Name, Slug, Description, BasePrice, WeightGrams, HeightCm, WidthCm, LengthCm, SeoTitle, SeoDescription);
}

internal sealed record UpdateProductRequest(int Version, ProductRequest Product);

internal sealed record GenerateVariantsRequest(IReadOnlyList<Guid>? ColorIds, IReadOnlyList<Guid>? SizeIds);

internal sealed record GenerateVariantsResponse(int Created);

internal sealed record UpdateVariantRequest(
    string Sku, decimal? Price, string? Barcode, int? WeightGrams, decimal? HeightCm, decimal? WidthCm, decimal? LengthCm);

/// <summary><c>/api/v1/admin/products</c> e <c>/variants</c> — RF-051, RF-056, RF-059 (arquivo 07 §9).</summary>
internal sealed class AdminProductEndpoints : IEndpointGroup
{
    public void Map(IEndpointRouteBuilder app)
    {
        var products = app.MapGroup("/admin/products").WithTags("Admin · Catálogo").RequireAuthorization(Policies.Admin);

        products.MapGet("/", List).WithName("AdminListProducts")
            .WithSummary("Produtos ativos e inativos, com busca por nome, SKU ou código de barras (RF-051)");
        products.MapGet("/{id:guid}", Get).WithName("AdminGetProduct").WithSummary("Produto com grade de variações e fotos (RF-051)");
        products.MapPost("/", Create).WithName("AdminCreateProduct")
            .WithSummary("Cria o produto inativo (RF-051)")
            .WithDescription("`slug` vazio gera a partir do nome. O produto entra na loja depois de ter variação ativa e ser ativado.")
            .RequireRateLimiting(RateLimitPolicies.AdminWrite);
        products.MapPut("/{id:guid}", Update).WithName("AdminUpdateProduct")
            .WithSummary("Edita o produto (RF-051)")
            .WithDescription("Trocar o slug cria redirect 301 do antigo (RN-025). `version` diferente da atual → 409.")
            .RequireRateLimiting(RateLimitPolicies.AdminWrite);
        products.MapPatch("/{id:guid}/status", SetStatus).WithName("AdminSetProductStatus")
            .WithSummary("Ativa ou desativa; ativar exige variação ativa (RN-023)")
            .RequireRateLimiting(RateLimitPolicies.AdminWrite);
        products.MapPost("/{id:guid}/variants/generate", GenerateVariants).WithName("AdminGenerateVariants")
            .WithSummary("Cria as combinações que faltam na grade (RF-056)")
            .WithDescription("As combinações existentes ficam como estão. Listas vazias criam a variação única (RN-022).")
            .RequireRateLimiting(RateLimitPolicies.AdminWrite);
        products.MapPut("/{id:guid}/variants/{variantId:guid}", UpdateVariant).WithName("AdminUpdateVariant")
            .WithSummary("Edita SKU, preço, código de barras e medidas (RF-056)")
            .WithDescription("`price` nulo herda o preço base; medidas nulas herdam as do produto.")
            .RequireRateLimiting(RateLimitPolicies.AdminWrite);
        products.MapPatch("/{id:guid}/variants/{variantId:guid}/status", SetVariantStatus).WithName("AdminSetVariantStatus")
            .WithSummary("Ativa ou desativa a variação (RN-023)")
            .RequireRateLimiting(RateLimitPolicies.AdminWrite);

        app.MapGet("/admin/variants/by-barcode/{code}", FindByBarcode)
            .WithTags("Admin · Catálogo").RequireAuthorization(Policies.Admin)
            .WithName("AdminFindVariantByBarcode")
            .WithSummary("Localiza a variação pelo código de barras (RF-059)")
            .WithDescription("Dígito verificador errado → 400. Código sem variação → 404 (`Catalog.BarcodeNotFound`), para o painel oferecer o vínculo.");
    }

    private static async Task<Results<Ok<PagedResponse<AdminProductSummaryResponse>>, ProblemHttpResult>> List(
        IQueryHandler<ListAdminProductsQuery, PagedResponse<AdminProductSummaryResponse>> handler,
        CancellationToken cancellationToken,
        int page = 1,
        int pageSize = ListAdminProductsQuery.DefaultPageSize,
        string? search = null,
        Guid? categoryId = null,
        bool? isActive = null,
        Guid? collectionId = null)
    {
        var result = await handler.Handle(new ListAdminProductsQuery(page, pageSize, search, categoryId, isActive, collectionId), cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<Results<Ok<AdminProductResponse>, ProblemHttpResult>> Get(
        Guid id, IQueryHandler<GetAdminProductQuery, AdminProductResponse> handler, CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new GetAdminProductQuery(id), cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<Results<Created<CreatedResponse>, ProblemHttpResult>> Create(
        ProductRequest request, ICommandHandler<CreateProductCommand, Guid> handler, CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new CreateProductCommand(request.ToInput()), cancellationToken);
        return result.IsSuccess
            ? TypedResults.Created($"/api/v1/admin/products/{result.Value}", new CreatedResponse(result.Value))
            : result.Error.ToProblem();
    }

    private static Task<Results<NoContent, ProblemHttpResult>> Update(
        Guid id, UpdateProductRequest request, ICommandHandler<UpdateProductCommand> handler, CancellationToken cancellationToken) =>
        AdminCategoryEndpoints.NoContentOrProblem(handler.Handle(new UpdateProductCommand(id, request.Version, request.Product.ToInput()), cancellationToken));

    private static Task<Results<NoContent, ProblemHttpResult>> SetStatus(
        Guid id, StatusRequest request, ICommandHandler<SetProductStatusCommand> handler, CancellationToken cancellationToken) =>
        AdminCategoryEndpoints.NoContentOrProblem(handler.Handle(new SetProductStatusCommand(id, request.IsActive), cancellationToken));

    private static async Task<Results<Ok<GenerateVariantsResponse>, ProblemHttpResult>> GenerateVariants(
        Guid id, GenerateVariantsRequest request, ICommandHandler<GenerateVariantsCommand, int> handler, CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new GenerateVariantsCommand(id, request.ColorIds ?? [], request.SizeIds ?? []), cancellationToken);
        return result.IsSuccess ? TypedResults.Ok(new GenerateVariantsResponse(result.Value)) : result.Error.ToProblem();
    }

    private static Task<Results<NoContent, ProblemHttpResult>> UpdateVariant(
        Guid id, Guid variantId, UpdateVariantRequest request, ICommandHandler<UpdateVariantCommand> handler, CancellationToken cancellationToken) =>
        AdminCategoryEndpoints.NoContentOrProblem(handler.Handle(
            new UpdateVariantCommand(id, variantId, request.Sku, request.Price, request.Barcode, request.WeightGrams, request.HeightCm, request.WidthCm, request.LengthCm),
            cancellationToken));

    private static Task<Results<NoContent, ProblemHttpResult>> SetVariantStatus(
        Guid id, Guid variantId, StatusRequest request, ICommandHandler<SetVariantStatusCommand> handler, CancellationToken cancellationToken) =>
        AdminCategoryEndpoints.NoContentOrProblem(handler.Handle(new SetVariantStatusCommand(id, variantId, request.IsActive), cancellationToken));

    private static async Task<Results<Ok<VariantByBarcodeResponse>, ProblemHttpResult>> FindByBarcode(
        string code, IQueryHandler<FindVariantByBarcodeQuery, VariantByBarcodeResponse> handler, CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new FindVariantByBarcodeQuery(code), cancellationToken);
        return result.ToHttpResult();
    }
}
