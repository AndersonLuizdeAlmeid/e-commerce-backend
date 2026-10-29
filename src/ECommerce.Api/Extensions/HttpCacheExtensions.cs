using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace ECommerce.Api.Extensions;

/// <summary>
/// Cache HTTP dos endpoints públicos (arquivo 12 §5): <c>Cache-Control: public, max-age</c> e <c>ETag</c> pelo hash
/// do corpo, com <c>304 Not Modified</c> quando o <c>If-None-Match</c> bate. Só atua em respostas 200.
/// </summary>
internal static class HttpCacheExtensions
{
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromSeconds(60);

    public static RouteHandlerBuilder WithPublicHttpCache(this RouteHandlerBuilder builder, TimeSpan? maxAge = null) =>
        builder.AddEndpointFilter(new PublicHttpCacheFilter(maxAge ?? DefaultMaxAge));

    private sealed class PublicHttpCacheFilter(TimeSpan maxAge) : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            var result = await next(context);

            var inner = result is INestedHttpResult nested ? nested.Result : result as IResult;
            if (inner is not (IStatusCodeHttpResult { StatusCode: StatusCodes.Status200OK } and IValueHttpResult { Value: { } value }))
            {
                return result;
            }

            var http = context.HttpContext;
            var serializerOptions = http.RequestServices.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
            var body = JsonSerializer.SerializeToUtf8Bytes(value, value.GetType(), serializerOptions);
            var etag = new EntityTagHeaderValue($"\"{Convert.ToBase64String(SHA256.HashData(body), 0, 16).TrimEnd('=')}\"");

            var headers = http.Response.GetTypedHeaders();
            headers.CacheControl = new CacheControlHeaderValue { Public = true, MaxAge = maxAge };
            headers.ETag = etag;

            var ifNoneMatch = http.Request.GetTypedHeaders().IfNoneMatch;
            if (ifNoneMatch.Any(tag => tag.Equals(EntityTagHeaderValue.Any) || tag.Compare(etag, useStrongComparison: false)))
            {
                return TypedResults.StatusCode(StatusCodes.Status304NotModified);
            }

            return TypedResults.Bytes(body, "application/json; charset=utf-8");
        }
    }
}
