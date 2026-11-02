using ECommerce.Domain.Abstractions;

namespace ECommerce.Application.Abstractions.Images;

/// <summary>Imagem pronta para publicar: WebP, sem metadados e no tamanho máximo da loja (RF-061).</summary>
public sealed record ProcessedImage(byte[] Content, int Width, int Height)
{
    public const string ContentType = "image/webp";
}

/// <summary>Valida o tipo real pelos magic bytes e reprocessa a imagem (arquivo 17, RF-061).</summary>
public interface IImageProcessor
{
    Result<ProcessedImage> Process(byte[] content);
}

/// <summary>Bucket S3 compatível: Cloudflare R2 em produção, RustFS em desenvolvimento (arquivo 20, D06).</summary>
public interface IImageStorage
{
    /// <summary>URL pública do objeto, sem precisar enviá-lo: a linha do banco é gravada antes do envio.</summary>
    string PublicUrl(string key);

    Task PutAsync(string key, byte[] content, string contentType, CancellationToken cancellationToken);

    /// <summary>Apagar o que não existe não é erro: a mensagem do Outbox pode rodar mais de uma vez.</summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken);
}
