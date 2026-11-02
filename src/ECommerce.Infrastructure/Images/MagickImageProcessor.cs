using ECommerce.Application.Abstractions.Images;
using ECommerce.Application.Catalog;
using ECommerce.Domain.Abstractions;
using ImageMagick;

namespace ECommerce.Infrastructure.Images;

/// <summary>
/// RF-061: confere o tipo real pelos magic bytes e reprocessa a imagem (arquivo 17): corrige a orientação, leva para
/// sRGB, remove os metadados (EXIF, GPS) e reduz para WebP. Magick.NET porque o ImageSharp não lê HEIC do iPhone.
/// O formato de leitura é fixado pelo que foi detectado: nada de deixar o Magick adivinhar (SVG, MVG e afins).
/// </summary>
public sealed class MagickImageProcessor : IImageProcessor
{
    public const uint MaxDimension = 2400;
    private const uint WebpQuality = 82;

    // Foto de celular tem até uns 8000 px; acima disso é arquivo feito para estourar a memória.
    private const uint MaxSourceDimension = 12000;

    private static readonly string[] HeifBrands = ["heic", "heix", "hevc", "hevx", "heim", "heis", "mif1", "msf1"];

    static MagickImageProcessor()
    {
        ResourceLimits.Width = MaxSourceDimension;
        ResourceLimits.Height = MaxSourceDimension;
    }

    public Result<ProcessedImage> Process(byte[] content)
    {
        var format = DetectFormat(content);
        if (format is null)
        {
            return CatalogErrors.ImageTypeNotAccepted;
        }

        try
        {
            using var image = new MagickImage(content, new MagickReadSettings { Format = format.Value });
            image.AutoOrient();

            // iPhone fotografa em Display P3: sem converter antes de tirar o perfil, as cores ficam lavadas.
            if (image.GetColorProfile() is not null)
            {
                image.TransformColorSpace(ColorProfiles.SRGB);
            }

            image.Strip();
            image.Resize(new MagickGeometry(MaxDimension, MaxDimension) { Greater = true });
            image.Quality = WebpQuality;

            return new ProcessedImage(image.ToByteArray(MagickFormat.WebP), checked((int)image.Width), checked((int)image.Height));
        }
        catch (MagickException)
        {
            return CatalogErrors.ImageUnreadable;
        }
    }

    private static MagickFormat? DetectFormat(byte[] content)
    {
        if (StartsWith(content, 0, [0xFF, 0xD8, 0xFF]))
        {
            return MagickFormat.Jpeg;
        }

        if (StartsWith(content, 0, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return MagickFormat.Png;
        }

        if (StartsWith(content, 0, "RIFF"u8) && StartsWith(content, 8, "WEBP"u8))
        {
            return MagickFormat.WebP;
        }

        // HEIC/HEIF: caixa "ftyp" no byte 4 e a marca do formato no byte 8.
        if (StartsWith(content, 4, "ftyp"u8) && content.Length >= 12)
        {
            var brand = System.Text.Encoding.ASCII.GetString(content, 8, 4);
            return HeifBrands.Contains(brand, StringComparer.Ordinal) ? MagickFormat.Heic : null;
        }

        return null;
    }

    private static bool StartsWith(byte[] content, int offset, ReadOnlySpan<byte> signature) =>
        content.Length >= offset + signature.Length && content.AsSpan(offset, signature.Length).SequenceEqual(signature);
}
