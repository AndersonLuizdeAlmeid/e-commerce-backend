using Microsoft.Net.Http.Headers;

namespace ECommerce.Api.Middlewares;

/// <summary>Headers de segurança da API, que só devolve JSON (arquivo 17 §2). HSTS fica com o UseHsts fora de Development.</summary>
internal sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";

            // Só o catálogo público declara cache (HttpCacheExtensions). Todo o resto (perfil, sacola, pedidos, painel)
            // tem dado pessoal e não pode ficar em cache de navegador nem de CDN (ASVS V8.2.1, entrega 6.2).
            if (!headers.ContainsKey(HeaderNames.CacheControl))
            {
                headers.CacheControl = "no-store";
            }

            // A UI do Scalar (/docs) precisa carregar scripts; o resto da API não.
            if (!context.Request.Path.StartsWithSegments("/docs"))
            {
                headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
            }

            return Task.CompletedTask;
        });

        return next(context);
    }
}
