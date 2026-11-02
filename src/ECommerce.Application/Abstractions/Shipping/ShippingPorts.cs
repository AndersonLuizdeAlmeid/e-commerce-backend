using ECommerce.Domain.Abstractions;
using ECommerce.Domain.Shipping;

namespace ECommerce.Application.Abstractions.Shipping;

public sealed record ShippingQuoteRequest(PostalCode Origin, PostalCode Destination, ShippingPackage Package, decimal DeclaredValue);

/// <summary>Cotação de frete (arquivo 22 §3.2, ADR-010). Provedor fora do ar → <see cref="ShippingErrors.QuoteUnavailable"/>; quem chama usa a contingência.</summary>
public interface IShippingQuoteProvider
{
    Task<Result<IReadOnlyList<ShippingQuote>>> QuoteAsync(ShippingQuoteRequest request, CancellationToken cancellationToken);
}

public sealed record PostalAddress(string PostalCode, string Street, string District, string City, string State);

/// <summary>Endereço pelo CEP (arquivo 22 §4.4).</summary>
public interface IPostalCodeLookup
{
    Task<Result<PostalAddress>> FindAsync(PostalCode postalCode, CancellationToken cancellationToken);
}
