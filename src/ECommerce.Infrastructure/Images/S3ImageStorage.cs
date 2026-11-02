using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using ECommerce.Application.Abstractions.Images;
using ECommerce.Application.Catalog.Admin;
using ECommerce.Infrastructure.Messaging;
using Microsoft.Extensions.Options;

namespace ECommerce.Infrastructure.Images;

/// <summary>Seção <c>ImageStorage</c>: bucket S3 compatível (arquivo 20, D06).</summary>
public sealed class ImageStorageSettings
{
    public const string SectionName = "ImageStorage";

    /// <summary>R2: <c>https://{conta}.r2.cloudflarestorage.com</c>. RustFS em dev: <c>http://localhost:9000</c>.</summary>
    public string ServiceUrl { get; init; } = string.Empty;

    public string AccessKey { get; init; } = string.Empty;
    public string SecretKey { get; init; } = string.Empty;
    public string Bucket { get; init; } = string.Empty;

    /// <summary>Base das URLs públicas, sem barra no final. Ex.: <c>https://imagens.loja.com.br</c>.</summary>
    public string PublicBaseUrl { get; init; } = string.Empty;

    /// <summary>"auto" no R2.</summary>
    public string Region { get; init; } = "auto";
}

/// <summary>O mesmo cliente serve para R2 e RustFS. Criado no primeiro uso: quem não mexe em fotos não precisa da configuração.</summary>
internal sealed class S3ImageStorage(IOptions<ImageStorageSettings> options) : IImageStorage, IDisposable
{
    // As chaves são imutáveis (o id da foto está nelas), então o CDN pode guardar para sempre.
    private const string ImmutableCache = "public, max-age=31536000, immutable";

    private readonly Lazy<AmazonS3Client> _client = new(() => CreateClient(options.Value));

    public string PublicUrl(string key) => $"{options.Value.PublicBaseUrl.TrimEnd('/')}/{key}";

    public async Task PutAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(content, writable: false);
        var request = new PutObjectRequest
        {
            BucketName = options.Value.Bucket,
            Key = key,
            InputStream = stream,
            ContentType = contentType,

            // O R2 não aceita o payload assinado em streaming do SDK; sem HTTPS (RustFS local) a assinatura é obrigatória.
            DisablePayloadSigning = options.Value.ServiceUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase),
        };
        request.Headers.CacheControl = ImmutableCache;

        await _client.Value.PutObjectAsync(request, cancellationToken);
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken) =>
        _client.Value.DeleteObjectAsync(options.Value.Bucket, key, cancellationToken);

    public void Dispose()
    {
        if (_client.IsValueCreated)
        {
            _client.Value.Dispose();
        }
    }

    private static AmazonS3Client CreateClient(ImageStorageSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ServiceUrl) || string.IsNullOrWhiteSpace(settings.Bucket))
        {
            throw new InvalidOperationException("ImageStorage:ServiceUrl e ImageStorage:Bucket não configurados.");
        }

        return new AmazonS3Client(
            new BasicAWSCredentials(settings.AccessKey, settings.SecretKey),
            new AmazonS3Config
            {
                ServiceURL = settings.ServiceUrl,
                AuthenticationRegion = settings.Region,
                ForcePathStyle = true,

                // R2 e RustFS não usam os checksums que o SDK 4 passou a mandar por padrão.
                RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
                ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            });
    }
}

/// <summary>Mensagem <see cref="DeleteStoredImage.Type"/> do Outbox: tira o arquivo do bucket depois do commit (RF-061 CA4).</summary>
internal sealed class DeleteStoredImageHandler(IImageStorage storage) : IOutboxMessageHandler
{
    public string Type => DeleteStoredImage.Type;

    public Task HandleAsync(string payloadJson, CancellationToken cancellationToken)
    {
        var message = JsonSerializer.Deserialize<DeleteStoredImage>(payloadJson, OutboxWriter.SerializerOptions)
            ?? throw new InvalidOperationException("Payload de catalog.delete-stored-image vazio.");
        return storage.DeleteAsync(message.StorageKey, cancellationToken);
    }
}
