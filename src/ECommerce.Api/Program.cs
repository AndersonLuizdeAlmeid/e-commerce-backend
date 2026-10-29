using System.Text.Json.Serialization;
using ECommerce.Api.Endpoints;
using ECommerce.Api.Endpoints.Identity;
using ECommerce.Api.Extensions;
using ECommerce.Api.Middlewares;
using ECommerce.Application;
using ECommerce.Infrastructure;
using Microsoft.AspNetCore.HttpOverrides;
using Scalar.AspNetCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.AddServerHeader = false;
    kestrel.Limits.MaxRequestBodySize = 1024 * 1024; // arquivo 17 §1
});

builder.Services.AddStructuredLogging(builder.Configuration, builder.Environment);
builder.Services.AddApiProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    // Números só como número: o OpenAPI deixa de publicar "number | string" e os tipos do frontend ficam corretos.
    options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
});
builder.Services.AddOpenApi();
builder.Services.AddFrontendCors(builder.Configuration, builder.Environment);

// Atrás do Caddy (arquivo 18 §7): o IP real vem do X-Forwarded-For, aceito só de proxies conhecidos (arquivo 11 §2).
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    foreach (var network in builder.Configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? [])
    {
        options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
    }
});

builder.Services.AddApiAuthentication(builder.Configuration);
builder.Services.AddApiRateLimiting(builder.Configuration);
builder.Services.Configure<RefreshCookieSettings>(builder.Configuration.GetSection(RefreshCookieSettings.SectionName));
builder.Services.AddHostedService<InitialAdminSeeder>();

builder.Services
    .AddApplication()
    .AddInfrastructure(builder.Configuration, builder.Environment);

var app = builder.Build();

// Ordem do pipeline: arquivo 03 §4 e arquivo 11 §5 (rate limiter depois da autenticação, para enxergar o usuário).
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSerilogRequestLogging();
app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing"))
{
    app.UseHsts();
}

app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseCors(CorsExtensions.FrontendPolicy);
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

if (!app.Environment.IsProduction())
{
    app.MapOpenApi().AllowAnonymous();
    app.MapScalarApiReference("/docs").AllowAnonymous();
}

app.MapHealthEndpoints();
app.MapEndpointGroups(app.MapGroup("/api/v1"));

await app.RunAsync();

#pragma warning disable CA1515 // precisa ser público para o WebApplicationFactory dos testes
public partial class Program;
#pragma warning restore CA1515
