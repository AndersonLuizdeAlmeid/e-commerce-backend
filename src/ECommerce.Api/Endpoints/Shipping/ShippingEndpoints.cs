using ECommerce.Api.Extensions;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Application.Abstractions.Shipping;
using ECommerce.Application.Shipping;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ECommerce.Api.Endpoints.Shipping;

internal sealed record ShippingQuoteRequestItem(Guid VariantId, int Quantity);

internal sealed record ShippingQuotesRequest(string? PostalCode, IReadOnlyList<ShippingQuoteRequestItem>? Items);

internal sealed record ShippingSettingsRequest(
    string OriginPostalCode,
    int HandlingDays,
    decimal FallbackPacPrice,
    decimal FallbackSedexPrice,
    int BoxWeightGrams,
    decimal BoxHeightCm,
    decimal BoxWidthCm,
    decimal BoxLengthCm);

/// <summary><c>/api/v1/shipping</c> e <c>/admin/shipping</c> — RF-032 e RF-058 (arquivo 07 §5 e §9).</summary>
internal sealed class ShippingEndpoints : IEndpointGroup
{
    public void Map(IEndpointRouteBuilder app)
    {
        // Público (o visitante simula no produto e na sacola), com limite próprio: cada chamada pode custar uma consulta externa (arquivo 11).
        var shipping = app.MapGroup("/shipping").WithTags("Frete").AllowAnonymous().RequireRateLimiting(RateLimitPolicies.ShippingQuote);

        shipping.MapPost("/quotes", Quote).WithName("GetShippingQuotes")
            .WithSummary("PAC e SEDEX com preço e prazo para o CEP (RF-032)")
            .WithDescription("CEP inválido → 400 (CA1). Provedor fora do ar → valores de contingência, com `isFallback` e sem prazo (CA2). Loja sem configuração → 503 `Shipping.NotConfigured`. Peça fora da loja → 422.");
        shipping.MapGet("/postal-codes/{cep}", FindAddress).WithName("GetPostalAddress")
            .WithSummary("Endereço pelo CEP (arquivo 22 §4.4)")
            .WithDescription("CEP inválido → 400. CEP que não existe → 404. Serviço fora do ar → 503: o endereço é preenchido à mão.");

        var settings = app.MapGroup("/admin/shipping/settings").WithTags("Admin · Frete").RequireAuthorization(Policies.Admin);

        settings.MapGet("/", GetSettings).WithName("AdminGetShippingSettings")
            .WithSummary("CEP de origem, manuseio, contingência e caixa padrão (RF-058); 204 se ainda não configurado");
        settings.MapPut("/", SaveSettings).WithName("AdminSaveShippingSettings")
            .WithSummary("Salva a configuração de frete (RF-058)")
            .RequireRateLimiting(RateLimitPolicies.AdminWrite);
    }

    private static async Task<Results<Ok<ShippingQuotesResponse>, ProblemHttpResult>> Quote(
        ShippingQuotesRequest request, IQueryHandler<GetShippingQuotesQuery, ShippingQuotesResponse> handler, CancellationToken cancellationToken)
    {
        var items = request.Items?.Select(item => new ShippingQuoteItem(item.VariantId, item.Quantity)).ToList() ?? [];
        var result = await handler.Handle(new GetShippingQuotesQuery(request.PostalCode ?? string.Empty, items), cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<Results<Ok<PostalAddress>, ProblemHttpResult>> FindAddress(
        string cep, IQueryHandler<GetPostalAddressQuery, PostalAddress> handler, CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new GetPostalAddressQuery(cep), cancellationToken);
        return result.ToHttpResult();
    }

    private static async Task<Results<Ok<ShippingSettingsResponse>, NoContent, ProblemHttpResult>> GetSettings(
        IQueryHandler<GetShippingSettingsQuery, ShippingSettingsResponse?> handler, CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new GetShippingSettingsQuery(), cancellationToken);
        if (result.IsFailure)
        {
            return result.Error.ToProblem();
        }

        return result.Value is null ? TypedResults.NoContent() : TypedResults.Ok(result.Value);
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> SaveSettings(
        ShippingSettingsRequest request, ICommandHandler<SaveShippingSettingsCommand> handler, CancellationToken cancellationToken)
    {
        var result = await handler.Handle(
            new SaveShippingSettingsCommand(
                request.OriginPostalCode, request.HandlingDays, request.FallbackPacPrice, request.FallbackSedexPrice,
                request.BoxWeightGrams, request.BoxHeightCm, request.BoxWidthCm, request.BoxLengthCm),
            cancellationToken);
        return result.IsSuccess ? TypedResults.NoContent() : result.Error.ToProblem();
    }
}
