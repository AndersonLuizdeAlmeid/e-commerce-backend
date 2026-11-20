using ECommerce.Api.Extensions;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Application.Catalog.GetMerchantFeed;
using Microsoft.Net.Http.Headers;

namespace ECommerce.Api.Endpoints.Catalog;

/// <summary><c>/api/v1/feeds</c> — feeds públicos para buscadores (SEO-14, entrega 6 fatia 6.6).</summary>
internal sealed class FeedEndpoints : IEndpointGroup
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(1);

    public void Map(IEndpointRouteBuilder app)
    {
        var feeds = app.MapGroup("/feeds").WithTags("Feeds")
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.PublicRead);

        feeds.MapGet("/google-merchant.xml", GoogleMerchant)
            .WithName("GetGoogleMerchantFeed")
            .WithSummary("Feed de produtos do Google Merchant Center (RSS 2.0)")
            .WithDescription(
                "Uma entrada por variação ativa de peça visível e com foto, agrupada pela peça (`item_group_id`). GTIN quando o código " +
                "de barras é válido; senão `identifier_exists=no`. Esgotada sai como `out_of_stock`. Cache de 1 h, apagado a cada mudança " +
                "de catálogo. A loja serve o mesmo feed em `/feeds/google-merchant.xml`.")
            .Produces<string>(StatusCodes.Status200OK, "application/xml");
    }

    private static async Task<IResult> GoogleMerchant(
        HttpContext context, IQueryHandler<GetMerchantFeedQuery, MerchantFeed> handler, CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new GetMerchantFeedQuery(), cancellationToken);
        if (result.IsFailure)
        {
            return result.Error.ToProblem();
        }

        context.Response.Headers[HeaderNames.CacheControl] = $"public, max-age={(int)MaxAge.TotalSeconds}";
        return TypedResults.Text(result.Value.ToXml(), "application/xml; charset=utf-8");
    }
}
