using System.Net.Http.Json;
using System.Text.Json;
using ECommerce.Application.Abstractions.Caching;
using ECommerce.Application.Catalog;
using ECommerce.Infrastructure.Messaging;
using Microsoft.Extensions.Options;

namespace ECommerce.Infrastructure.Caching;

/// <summary>Seção <c>Storefront</c>: onde fica o <c>/api/revalidate</c> do Next.js (arquivo 16 §9).</summary>
public sealed class StorefrontSettings
{
    public const string SectionName = "Storefront";
    public const string SecretHeader = "X-Revalidate-Secret";

    /// <summary>Ex.: <c>http://frontend:3000/api/revalidate</c>. Vazio = só invalida o cache da API (testes, API isolada).</summary>
    public string RevalidateUrl { get; init; } = string.Empty;

    public string RevalidateSecret { get; init; } = string.Empty;
}

/// <summary>
/// Mensagem <see cref="CatalogChanged.Type"/> do Outbox: invalida o HybridCache por tag e pede ao Next.js para
/// revalidar as páginas com as mesmas tags. Idempotente; uma falha de rede volta pelo backoff do Outbox.
/// </summary>
internal sealed class CatalogChangedHandler(ICacheService cache, HttpClient http, IOptions<StorefrontSettings> options)
    : IOutboxMessageHandler
{
    public string Type => CatalogChanged.Type;

    public async Task HandleAsync(string payloadJson, CancellationToken cancellationToken)
    {
        var change = JsonSerializer.Deserialize<CatalogChanged>(payloadJson, OutboxWriter.SerializerOptions)
            ?? throw new InvalidOperationException("Payload de catalog.changed vazio.");

        foreach (var tag in change.Tags)
        {
            await cache.RemoveByTagAsync(tag, cancellationToken);
        }

        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.RevalidateUrl))
        {
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, settings.RevalidateUrl)
        {
            Content = JsonContent.Create(new { tags = change.Tags }),
        };
        request.Headers.Add(StorefrontSettings.SecretHeader, settings.RevalidateSecret);

        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
