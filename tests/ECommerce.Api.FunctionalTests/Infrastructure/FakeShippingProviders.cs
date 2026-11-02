using System.Collections.Concurrent;
using ECommerce.Application.Abstractions.Shipping;
using ECommerce.Domain.Abstractions;
using ECommerce.Domain.Shipping;

namespace ECommerce.Api.FunctionalTests.Infrastructure;

/// <summary>Cotação e CEP sob controle do teste: guarda o que foi pedido e pode ficar fora do ar.</summary>
public sealed class FakeShippingProviders : IShippingQuoteProvider, IPostalCodeLookup
{
    private readonly ConcurrentQueue<ShippingQuoteRequest> _quoteRequests = new();

    public IReadOnlyCollection<ShippingQuoteRequest> QuoteRequests => _quoteRequests;

    public Error? QuoteFailure { get; set; }

    public Error? AddressFailure { get; set; }

    public int AddressCalls { get; private set; }

    public Task<Result<IReadOnlyList<ShippingQuote>>> QuoteAsync(ShippingQuoteRequest request, CancellationToken cancellationToken)
    {
        _quoteRequests.Enqueue(request);
        if (QuoteFailure is { } failure)
        {
            return Task.FromResult(Result.Failure<IReadOnlyList<ShippingQuote>>(failure));
        }

        IReadOnlyList<ShippingQuote> quotes =
        [
            new(ShippingServices.Pac, ShippingServices.Pac, 21.40m, 7),
            new(ShippingServices.Sedex, ShippingServices.Sedex, 38.90m, 3),
        ];
        return Task.FromResult(Result.Success(quotes));
    }

    public Task<Result<PostalAddress>> FindAsync(PostalCode postalCode, CancellationToken cancellationToken)
    {
        AddressCalls++;
        return Task.FromResult(AddressFailure is { } failure
            ? Result.Failure<PostalAddress>(failure)
            : Result.Success(new PostalAddress(postalCode.Value, "Avenida Paulista", "Bela Vista", "São Paulo", "SP")));
    }

    public void Reset()
    {
        _quoteRequests.Clear();
        QuoteFailure = null;
        AddressFailure = null;
        AddressCalls = 0;
    }
}
