using ECommerce.Api.Extensions;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Application.Abstractions.Paging;
using ECommerce.Application.Catalog;
using ECommerce.Application.Catalog.GetCatalogFilters;
using ECommerce.Application.Catalog.GetCategoryBySlug;
using ECommerce.Application.Catalog.GetCollections;
using ECommerce.Application.Catalog.GetProductBySlug;
using ECommerce.Application.Catalog.GetShowcase;
using ECommerce.Application.Catalog.GetSitemap;
using ECommerce.Application.Catalog.ListCategories;
using ECommerce.Application.Catalog.ListProducts;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ECommerce.Api.Endpoints.Catalog;

internal sealed class CatalogEndpoints : IEndpointGroup
{
    public void Map(IEndpointRouteBuilder app)
    {
        // Catálogo é público (arquivo 09 §2) e tem limite de leitura por IP (arquivo 11 §3).
        var group = app.MapGroup("/catalog").WithTags("Catalog")
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.PublicRead);

        group.MapGet("/categories", ListCategories)
            .WithName("ListCategories")
            .WithSummary("Lista as categorias ativas (RF-022)")
            .WithPublicHttpCache();

        group.MapGet("/categories/{slug}", GetCategoryBySlug)
            .WithName("GetCategoryBySlug")
            .WithSummary("Dados da categoria ativa, com texto e SEO")
            .WithDescription("Slug antigo → 301 com Location para o slug atual (RN-025).")
            .Produces(StatusCodes.Status301MovedPermanently)
            .WithPublicHttpCache();

        group.MapGet("/collections", ListCollections)
            .WithName("ListCollections")
            .WithSummary("Coleções ativas, na ordem da navegação (RF-024)")
            .WithPublicHttpCache();

        group.MapGet("/collections/{slug}", GetCollectionBySlug)
            .WithName("GetCollectionBySlug")
            .WithSummary("Dados da coleção ativa: texto, SEO, capa e tema das cartelas (RF-024)")
            .WithDescription("Slug antigo → 301 com Location para o slug atual (CA4).")
            .Produces(StatusCodes.Status301MovedPermanently)
            .WithPublicHttpCache();

        group.MapGet("/showcase", GetShowcase)
            .WithName("GetShowcase")
            .WithSummary("Peça em destaque e ocasião vigente, para a home (RF-025)")
            .WithPublicHttpCache();

        group.MapGet("/products", ListProducts)
            .WithName("ListProducts")
            .WithSummary("Vitrine paginada com filtros e ordenação (RF-020)")
            .WithDescription("`colors` são slugs de cor e `sizes` nomes de tamanho; repita o parâmetro para vários valores. "
                + "`sort`: createdAt:desc (padrão), createdAt:asc, price:asc, price:desc, name:asc, name:desc.")
            .WithPublicHttpCache();

        group.MapGet("/products/{slug}", GetProductBySlug)
            .WithName("GetProductBySlug")
            .WithSummary("Página do produto com variações, grade e imagens (RF-021)")
            .WithDescription("Slug antigo → 301 com Location para o slug atual. Produto fora da loja → 410 com `categorySlug`.")
            .Produces(StatusCodes.Status301MovedPermanently)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status410Gone)
            .WithPublicHttpCache();

        group.MapGet("/filters", GetFilters)
            .WithName("GetCatalogFilters")
            .WithSummary("Cores, tamanhos e faixa de preço disponíveis para os filtros (RF-023)")
            .WithPublicHttpCache();

        group.MapGet("/sitemap", GetSitemap)
            .WithName("GetSitemap")
            .WithSummary("Slugs e data de alteração de categorias e produtos visíveis (SEO-06)")
            .WithPublicHttpCache();
    }

    private static async Task<Results<Ok<IReadOnlyList<CategorySummaryResponse>>, ProblemHttpResult>> ListCategories(
        IQueryHandler<ListCategoriesQuery, IReadOnlyList<CategorySummaryResponse>> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new ListCategoriesQuery(), cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<Results<Ok<CategoryDetailsResponse>, RedirectHttpResult, ProblemHttpResult>> GetCategoryBySlug(
        string slug,
        IQueryHandler<GetCategoryBySlugQuery, CategoryDetailsResponse> handler,
        IQueryHandler<ResolveCategorySlugQuery, string> resolver,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new GetCategoryBySlugQuery(slug), cancellationToken);
        if (result.IsSuccess)
        {
            return TypedResults.Ok(result.Value);
        }

        var moved = await resolver.Handle(new ResolveCategorySlugQuery(slug), cancellationToken);
        return moved.IsSuccess
            ? TypedResults.Redirect($"/api/v1/catalog/categories/{Uri.EscapeDataString(moved.Value)}", permanent: true)
            : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<IReadOnlyList<CollectionSummaryResponse>>, ProblemHttpResult>> ListCollections(
        IQueryHandler<ListCollectionsQuery, IReadOnlyList<CollectionSummaryResponse>> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new ListCollectionsQuery(), cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<Results<Ok<CollectionDetailsResponse>, RedirectHttpResult, ProblemHttpResult>> GetCollectionBySlug(
        string slug,
        IQueryHandler<GetCollectionBySlugQuery, CollectionDetailsResponse> handler,
        IQueryHandler<ResolveCollectionSlugQuery, string> resolver,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new GetCollectionBySlugQuery(slug), cancellationToken);
        if (result.IsSuccess)
        {
            return TypedResults.Ok(result.Value);
        }

        var moved = await resolver.Handle(new ResolveCollectionSlugQuery(slug), cancellationToken);
        return moved.IsSuccess
            ? TypedResults.Redirect($"/api/v1/catalog/collections/{Uri.EscapeDataString(moved.Value)}", permanent: true)
            : result.Error.ToProblem();
    }

    private static async Task<Results<Ok<ShowcaseResponse>, ProblemHttpResult>> GetShowcase(
        IQueryHandler<GetShowcaseQuery, ShowcaseResponse> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new GetShowcaseQuery(), cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<Results<Ok<PagedResponse<ProductSummaryResponse>>, ProblemHttpResult>> ListProducts(
        IQueryHandler<ListProductsQuery, PagedResponse<ProductSummaryResponse>> handler,
        CancellationToken cancellationToken,
        int page = 1,
        int pageSize = ListProductsQuery.DefaultPageSize,
        string? category = null,
        decimal? minPrice = null,
        decimal? maxPrice = null,
        string? search = null,
        string[]? colors = null,
        string[]? sizes = null,
        string sort = ListProductsQuery.DefaultSort,
        string? collection = null)
    {
        var query = new ListProductsQuery(
            page, pageSize, category, minPrice, maxPrice, search, colors ?? [], sizes ?? [], sort, collection);

        var result = await handler.Handle(query, cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<Results<Ok<ProductDetailsResponse>, RedirectHttpResult, ProblemHttpResult>> GetProductBySlug(
        string slug,
        IQueryHandler<GetProductBySlugQuery, ProductBySlugResult> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new GetProductBySlugQuery(slug), cancellationToken);
        if (result.IsFailure)
        {
            return result.Error.ToProblem();
        }

        return result.Value switch
        {
            ProductFound found => TypedResults.Ok(found.Product),
            ProductMoved moved => TypedResults.Redirect(
                $"/api/v1/catalog/products/{Uri.EscapeDataString(moved.Slug)}", permanent: true),
            ProductGone gone => CatalogErrors.ProductUnavailable.ToProblem(
                StatusCodes.Status410Gone,
                [new KeyValuePair<string, object?>("categorySlug", gone.CategorySlug)]),
            _ => throw new InvalidOperationException($"Resultado não mapeado: {result.Value.GetType().Name}"),
        };
    }

    private static async Task<Results<Ok<CatalogFiltersResponse>, ProblemHttpResult>> GetFilters(
        IQueryHandler<GetCatalogFiltersQuery, CatalogFiltersResponse> handler,
        CancellationToken cancellationToken,
        string? category = null,
        string? collection = null)
    {
        var result = await handler.Handle(new GetCatalogFiltersQuery(category, collection), cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<Results<Ok<SitemapResponse>, ProblemHttpResult>> GetSitemap(
        IQueryHandler<GetSitemapQuery, SitemapResponse> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new GetSitemapQuery(), cancellationToken);
        return result.ToHttpResult();
    }
}
