using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using ECommerce.Application.Abstractions.Caching;
using ECommerce.Application.Abstractions.Data;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Application.Abstractions.Shipping;
using ECommerce.Domain.Abstractions;
using ECommerce.Domain.Carts;
using ECommerce.Domain.Shipping;
using FluentValidation;

namespace ECommerce.Application.Shipping;

// RF-032: cotação pública por CEP. Preço e peso vêm sempre do banco, nunca do navegador.

public sealed record ShippingOptionResponse(string ServiceCode, string Name, decimal Price, decimal OriginalPrice, int? DeliveryDays, IReadOnlyList<string> AppliedRules);

/// <summary><see cref="IsFallback"/>: o provedor está fora, e os valores são os de contingência da loja, sem prazo (RF-032 CA2).</summary>
public sealed record ShippingQuotesResponse(IReadOnlyList<ShippingOptionResponse> Options, bool IsFallback, decimal? FreeShippingRemaining);

public sealed record ShippingQuoteItem(Guid VariantId, int Quantity);

public sealed record GetShippingQuotesQuery(string PostalCode, IReadOnlyList<ShippingQuoteItem> Items) : IQuery<ShippingQuotesResponse>;

public sealed record GetPostalAddressQuery(string PostalCode) : IQuery<PostalAddress>;

internal sealed class GetShippingQuotesValidator : AbstractValidator<GetShippingQuotesQuery>
{
    public GetShippingQuotesValidator()
    {
        RuleFor(x => x.Items)
            .Must(items => items.Count is > 0 and <= Cart.MaxItems)
            .WithMessage($"Informe de 1 a {Cart.MaxItems} peças.");
        RuleForEach(x => x.Items).ChildRules(item =>
        {
            item.RuleFor(x => x.VariantId).NotEmpty();
            item.RuleFor(x => x.Quantity).InclusiveBetween(1, Cart.MaxQuantityPerItem);
        });
    }
}

internal static class ShippingCache
{
    /// <summary>Só a resposta do provedor: as regras dependem do subtotal e entram depois do cache (entrega 3 §3, P3).</summary>
    public static readonly CachePolicy Quote = new(TimeSpan.FromMinutes(30), ["shipping"]);

    public static readonly CachePolicy Address = new(TimeSpan.FromDays(7), ["shipping-cep"]);

    public static string QuoteKey(ShippingQuoteRequest request)
    {
        var (weight, dimensions) = (request.Package.WeightGrams, request.Package.Dimensions);
        var package = string.Create(
            CultureInfo.InvariantCulture,
            $"{request.Origin.Value}|{weight}|{dimensions.HeightCm:0.0}|{dimensions.WidthCm:0.0}|{dimensions.LengthCm:0.0}|{request.DeclaredValue:0.00}");
        return $"shipping:quote:{request.Destination.Value}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(package)), 0, 16)}";
    }

    public static string AddressKey(PostalCode postalCode) => $"shipping:cep:{postalCode.Value}";
}

/// <summary>Falha do serviço externo: sobe do cache sem virar entrada dele e é tratada logo acima.</summary>
internal sealed class ShippingLookupFailedException(Error error) : Exception(error.Message)
{
    public Error Error { get; } = error;
}

internal sealed class GetShippingQuotesHandler(IDbConnectionFactory connectionFactory, IShippingQuoteProvider provider, ICacheService cache, TimeProvider time)
    : IQueryHandler<GetShippingQuotesQuery, ShippingQuotesResponse>
{
    private const string Sql = $"""
        SELECT origin_postal_code, handling_days, fallback_pac_price, fallback_sedex_price,
               box_weight_grams, box_height_cm, box_width_cm, box_length_cm
          FROM shipping.settings;

        SELECT v.id AS variant_id,
               coalesce(v.price, p.base_price) AS price,
               coalesce(v.weight_grams, p.weight_grams) AS weight_grams,
               coalesce(v.height_cm, p.height_cm) AS height_cm,
               coalesce(v.width_cm, p.width_cm) AS width_cm,
               coalesce(v.length_cm, p.length_cm) AS length_cm
          FROM catalog.product_variants v
          JOIN catalog.products p ON p.id = v.product_id
          JOIN catalog.categories c ON c.id = p.category_id
         WHERE v.id = ANY(@Ids) AND v.is_active AND p.is_active AND c.is_active;

        SELECT {ShippingRuleRow.Columns} FROM shipping.rules WHERE is_active;

        SELECT date_trunc('day', @Now::timestamptz AT TIME ZONE 'America/Sao_Paulo');
        """;

    public async Task<Result<ShippingQuotesResponse>> Handle(GetShippingQuotesQuery query, CancellationToken cancellationToken)
    {
        var destination = PostalCode.Create(query.PostalCode);
        if (destination.IsFailure)
        {
            return destination.Error;
        }

        var lines = query.Items
            .GroupBy(item => item.VariantId)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Quantity));

        SettingsRow? settings;
        List<VariantRow> variants;
        List<ShippingRule> rules;
        DateOnly today;
        await using (var connection = await connectionFactory.OpenConnectionAsync(cancellationToken))
        {
            using var grid = await connection.QueryMultipleAsync(new CommandDefinition(
                Sql, new { Ids = lines.Keys.ToArray(), Now = time.GetUtcNow().UtcDateTime }, cancellationToken: cancellationToken));
            settings = await grid.ReadSingleOrDefaultAsync<SettingsRow>();
            variants = (await grid.ReadAsync<VariantRow>()).AsList();
            rules = (await grid.ReadAsync<ShippingRuleRow>()).Select(row => row.ToRule()).ToList();
            today = DateOnly.FromDateTime(await grid.ReadSingleAsync<DateTime>());
        }

        if (settings is null)
        {
            return ShippingErrors.NotConfigured;
        }

        if (variants.Count != lines.Count)
        {
            return ShippingErrors.ItemUnavailable;
        }

        var package = PackagingService.Build(
            settings.BoxWeightGrams,
            new PackageDimensions(settings.BoxHeightCm, settings.BoxWidthCm, settings.BoxLengthCm),
            variants.Select(v => new PackageItem(v.WeightGrams, new PackageDimensions(v.HeightCm, v.WidthCm, v.LengthCm), lines[v.VariantId])).ToList());

        var subtotal = variants.Sum(v => v.Price * lines[v.VariantId]);
        var request = new ShippingQuoteRequest(PostalCode.Rehydrate(settings.OriginPostalCode.Trim()), destination.Value, package, subtotal);
        var context = new ShippingPricingContext(subtotal, destination.Value, today);

        try
        {
            var quotes = await cache.GetOrCreateAsync(ShippingCache.QuoteKey(request), ct => QuoteAsync(request, ct), ShippingCache.Quote, cancellationToken);
            return Priced(quotes.Select(q => q with { DeliveryDays = q.DeliveryDays + settings.HandlingDays }).ToList(), rules, context, isFallback: false);
        }
        catch (ShippingLookupFailedException failure) when (failure.Error == ShippingErrors.QuoteUnavailable)
        {
            // As regras valem também aqui: a promessa de frete grátis não some porque os Correios caíram (entrega 3 §3, P4).
            ShippingQuote[] fallback =
            [
                new(ShippingServices.Pac, ShippingServices.Pac, settings.FallbackPacPrice, null),
                new(ShippingServices.Sedex, ShippingServices.Sedex, settings.FallbackSedexPrice, null),
            ];
            return Priced(fallback, rules, context, isFallback: true);
        }
        catch (ShippingLookupFailedException failure)
        {
            return failure.Error;
        }
    }

    private static ShippingQuotesResponse Priced(
        IReadOnlyList<ShippingQuote> quotes, IReadOnlyList<ShippingRule> rules, ShippingPricingContext context, bool isFallback)
    {
        var priced = ShippingPricingService.Apply(quotes, rules, context);
        return new ShippingQuotesResponse(
            priced.Options.Select(o => new ShippingOptionResponse(o.Quote.ServiceCode, o.Quote.ServiceName, o.Price, o.Quote.Price, o.Quote.DeliveryDays, o.AppliedRules)).ToList(),
            isFallback,
            priced.FreeShippingRemaining);
    }

    /// <summary>Lança em vez de devolver a falha: contingência em cache fixaria o preço dela por 30 minutos (entrega 3 §3, P4).</summary>
    private async ValueTask<IReadOnlyList<ShippingQuote>> QuoteAsync(ShippingQuoteRequest request, CancellationToken cancellationToken)
    {
        var result = await provider.QuoteAsync(request, cancellationToken);
        return result.IsSuccess ? result.Value : throw new ShippingLookupFailedException(result.Error);
    }

    private sealed class SettingsRow
    {
        public string OriginPostalCode { get; init; } = string.Empty;
        public short HandlingDays { get; init; }
        public decimal FallbackPacPrice { get; init; }
        public decimal FallbackSedexPrice { get; init; }
        public int BoxWeightGrams { get; init; }
        public decimal BoxHeightCm { get; init; }
        public decimal BoxWidthCm { get; init; }
        public decimal BoxLengthCm { get; init; }
    }

    private sealed class VariantRow
    {
        public Guid VariantId { get; init; }
        public decimal Price { get; init; }
        public int WeightGrams { get; init; }
        public decimal HeightCm { get; init; }
        public decimal WidthCm { get; init; }
        public decimal LengthCm { get; init; }
    }
}

internal sealed class GetPostalAddressHandler(IPostalCodeLookup lookup, ICacheService cache) : IQueryHandler<GetPostalAddressQuery, PostalAddress>
{
    public async Task<Result<PostalAddress>> Handle(GetPostalAddressQuery query, CancellationToken cancellationToken)
    {
        var postalCode = PostalCode.Create(query.PostalCode);
        if (postalCode.IsFailure)
        {
            return postalCode.Error;
        }

        try
        {
            return await cache.GetOrCreateAsync(
                ShippingCache.AddressKey(postalCode.Value),
                async ct =>
                {
                    var found = await lookup.FindAsync(postalCode.Value, ct);
                    return found.IsSuccess ? found.Value : throw new ShippingLookupFailedException(found.Error);
                },
                ShippingCache.Address,
                cancellationToken);
        }
        catch (ShippingLookupFailedException failure)
        {
            return failure.Error;
        }
    }
}
