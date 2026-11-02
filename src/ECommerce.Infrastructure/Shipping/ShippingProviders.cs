using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ECommerce.Application.Abstractions.Shipping;
using ECommerce.Domain.Abstractions;
using ECommerce.Domain.Shipping;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ECommerce.Infrastructure.Shipping;

/// <summary>Seção <c>Shipping</c> (arquivo 22). O token é segredo: só por variável de ambiente (arquivo 17).</summary>
public sealed class ShippingProviderSettings
{
    public const string SectionName = "Shipping";
    public const string FakeProvider = "Fake";
    public const string MelhorEnvioProvider = "MelhorEnvio";

    /// <summary><c>MelhorEnvio</c>, ou <c>Fake</c> em desenvolvimento e nos testes.</summary>
    public string Provider { get; init; } = MelhorEnvioProvider;

    public MelhorEnvioSettings MelhorEnvio { get; init; } = new();

    public string ViaCepBaseUrl { get; init; } = "https://viacep.com.br/";

    public sealed class MelhorEnvioSettings
    {
        /// <summary>Sandbox: <c>https://sandbox.melhorenvio.com.br/</c>. Produção: <c>https://melhorenvio.com.br/</c>.</summary>
        public string BaseUrl { get; init; } = "https://sandbox.melhorenvio.com.br/";

        public string Token { get; init; } = string.Empty;

        /// <summary>Exigido pelo Melhor Envio: nome da aplicação e e-mail de contato técnico, como <c>Loja (contato@loja.com)</c>.</summary>
        public string UserAgent { get; init; } = string.Empty;

        /// <summary>Identificadores dos serviços no Melhor Envio.</summary>
        public int PacServiceId { get; init; } = 1;

        public int SedexServiceId { get; init; } = 2;
    }
}

/// <summary>Cotação pela API do Melhor Envio (arquivo 20, D03), como um pacote só (entrega 3 §3, P9).</summary>
internal sealed partial class MelhorEnvioQuoteProvider(
    HttpClient http, IOptions<ShippingProviderSettings> options, ILogger<MelhorEnvioQuoteProvider> logger) : IShippingQuoteProvider
{
    private const decimal GramsPerKilogram = 1000m;

    public async Task<Result<IReadOnlyList<ShippingQuote>>> QuoteAsync(ShippingQuoteRequest request, CancellationToken cancellationToken)
    {
        var settings = options.Value.MelhorEnvio;
        using var message = new HttpRequestMessage(HttpMethod.Post, "api/v2/me/shipment/calculate")
        {
            Content = JsonContent.Create(new
            {
                from = new { postal_code = request.Origin.Value },
                to = new { postal_code = request.Destination.Value },
                services = string.Create(CultureInfo.InvariantCulture, $"{settings.PacServiceId},{settings.SedexServiceId}"),
                products = new[]
                {
                    new
                    {
                        id = "pacote",
                        // A API pede medidas inteiras: arredonda para cima, para não cotar um pacote menor que o real.
                        height = (int)Math.Ceiling(request.Package.Dimensions.HeightCm),
                        width = (int)Math.Ceiling(request.Package.Dimensions.WidthCm),
                        length = (int)Math.Ceiling(request.Package.Dimensions.LengthCm),
                        weight = request.Package.WeightGrams / GramsPerKilogram,
                        insurance_value = request.DeclaredValue,
                        quantity = 1,
                    },
                },
            }),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.Token);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Headers.TryAddWithoutValidation("User-Agent", settings.UserAgent);

        try
        {
            using var response = await http.SendAsync(message, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            // O provedor recusa o CEP de destino: o erro é de quem digitou, e a contingência daria um preço para um CEP inexistente.
            if (response.StatusCode == HttpStatusCode.UnprocessableEntity && body.Contains("to.postal_code", StringComparison.Ordinal))
            {
                return ShippingErrors.InvalidPostalCode;
            }

            if (!response.IsSuccessStatusCode)
            {
                LogProviderError(logger, (int)response.StatusCode, body.Length > 500 ? body[..500] : body);
                return ShippingErrors.QuoteUnavailable;
            }

            var quotes = Parse(body, settings);
            if (quotes.Count == 0)
            {
                LogProviderError(logger, (int)response.StatusCode, "Nenhum serviço disponível na resposta.");
                return ShippingErrors.QuoteUnavailable;
            }

            return quotes;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Timeout, circuito aberto, rede ou resposta malformada: tudo vira contingência (arquivo 22 §5).
            LogProviderFailure(logger, exception);
            return ShippingErrors.QuoteUnavailable;
        }
    }

    /// <summary>Serviço sem preço vem com <c>error</c> (trecho não atendido, por exemplo) e fica de fora.</summary>
    private static List<ShippingQuote> Parse(string body, ShippingProviderSettings.MelhorEnvioSettings settings)
    {
        using var document = JsonDocument.Parse(body);
        var quotes = new List<ShippingQuote>();
        foreach (var service in document.RootElement.EnumerateArray())
        {
            var id = service.GetProperty("id").GetInt32();
            var code = id == settings.PacServiceId ? ShippingServices.Pac : id == settings.SedexServiceId ? ShippingServices.Sedex : null;
            var price = ReadDecimal(service, "custom_price") ?? ReadDecimal(service, "price");
            if (code is null || price is null || service.TryGetProperty("error", out _))
            {
                continue;
            }

            quotes.Add(new ShippingQuote(code, code, price.Value, ReadInt(service, "custom_delivery_time") ?? ReadInt(service, "delivery_time")));
        }

        return quotes;
    }

    /// <summary>O preço vem como texto ("21.40"); aceita número também, para não depender desse detalhe.</summary>
    private static decimal? ReadDecimal(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDecimal(),
            JsonValueKind.String when decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    private static int? ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;

    [LoggerMessage(Level = LogLevel.Error, Message = "Melhor Envio respondeu {Status}: {Body}")]
    private static partial void LogProviderError(ILogger logger, int status, string body);

    [LoggerMessage(Level = LogLevel.Error, Message = "Falha ao cotar no Melhor Envio.")]
    private static partial void LogProviderFailure(ILogger logger, Exception exception);
}

/// <summary>
/// Preços previsíveis para desenvolver sem token. Só existe em Development e nos testes; em produção a API nem sobe
/// com ele, para a loja nunca vender com frete inventado.
/// </summary>
internal sealed class FakeQuoteProvider : IShippingQuoteProvider
{
    public Task<Result<IReadOnlyList<ShippingQuote>>> QuoteAsync(ShippingQuoteRequest request, CancellationToken cancellationToken)
    {
        var region = request.Destination.Value[0] - '0';
        var distance = 3m + (region * 1.5m);
        var weight = request.Package.WeightGrams / 1000m;
        IReadOnlyList<ShippingQuote> quotes =
        [
            new(ShippingServices.Pac, ShippingServices.Pac, decimal.Round(15m + distance + (weight * 8m), 2), 4 + region),
            new(ShippingServices.Sedex, ShippingServices.Sedex, decimal.Round(28m + (distance * 2m) + (weight * 14m), 2), 1 + (region / 3)),
        ];
        return Task.FromResult(Result.Success(quotes));
    }
}

/// <summary>Endereço pelo CEP no ViaCEP (arquivo 22 §4.4).</summary>
internal sealed partial class ViaCepLookup(HttpClient http, ILogger<ViaCepLookup> logger) : IPostalCodeLookup
{
    public async Task<Result<PostalAddress>> FindAsync(PostalCode postalCode, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync(new Uri($"ws/{postalCode.Value}/json/", UriKind.Relative), cancellationToken);
            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                return ShippingErrors.InvalidPostalCode;
            }

            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = document.RootElement;

            // CEP bem formado que não existe: o ViaCEP responde 200 com `erro`.
            if (root.TryGetProperty("erro", out _))
            {
                return ShippingErrors.PostalCodeNotFound;
            }

            return new PostalAddress(postalCode.Value, Text(root, "logradouro"), Text(root, "bairro"), Text(root, "localidade"), Text(root, "uf"));
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            LogLookupFailure(logger, exception);
            return ShippingErrors.PostalCodeLookupUnavailable;
        }
    }

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) ? value.GetString() ?? string.Empty : string.Empty;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Falha ao consultar o CEP no ViaCEP.")]
    private static partial void LogLookupFailure(ILogger logger, Exception exception);
}
