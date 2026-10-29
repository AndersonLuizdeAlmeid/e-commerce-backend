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
