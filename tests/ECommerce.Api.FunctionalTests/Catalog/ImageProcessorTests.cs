using ECommerce.Application.Catalog;
using ECommerce.Infrastructure.Images;
using ImageMagick;

namespace ECommerce.Api.FunctionalTests.Catalog;

/// <summary>O processador de verdade, com arquivos gerados na hora. Não usa banco nem Docker.</summary>
public sealed class ImageProcessorTests
{
    private readonly MagickImageProcessor _processor = new();

    [Theory, Trait("Spec", "RF-061")]
    [InlineData(MagickFormat.Jpeg)]
    [InlineData(MagickFormat.Png)]
    [InlineData(MagickFormat.WebP)]
    public void AcceptedFormats_BecomeWebp(MagickFormat format)
    {
        var result = _processor.Process(TestImages.Create(format, 800, 600));

        result.IsSuccess.ShouldBeTrue();
        using var saved = new MagickImage(result.Value.Content);
        saved.Format.ShouldBe(MagickFormat.WebP);
        result.Value.Width.ShouldBe(800);
        result.Value.Height.ShouldBe(600);
    }

    [Fact, Trait("Spec", "RF-061")]
    public void Heic_FromIphone_BecomesWebp()
    {
        var heic = Convert.FromBase64String(TestImages.HeicWithGpsBase64);
        heic.AsSpan().IndexOf("Exif"u8).ShouldBeGreaterThan(-1);

        var result = _processor.Process(heic);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Message : null);
        result.Value.Width.ShouldBe(64);
        result.Value.Height.ShouldBe(48);
        using var saved = new MagickImage(result.Value.Content);
        saved.Format.ShouldBe(MagickFormat.WebP);
        saved.GetExifProfile().ShouldBeNull();
    }

    [Fact, Trait("Spec", "RF-061-CA2")]
    public void Exif_AndGps_AreRemoved()
    {
        var withGps = TestImages.JpegWithGps();
        using (var original = new MagickImage(withGps))
        {
            original.GetExifProfile().ShouldNotBeNull();
        }

        var result = _processor.Process(withGps);

        using var saved = new MagickImage(result.Value.Content);
        saved.GetExifProfile().ShouldBeNull();
        result.Value.Content.AsSpan().IndexOf("Exif"u8).ShouldBe(-1);
    }

    [Fact, Trait("Spec", "RF-061")]
    public void Orientation_IsApplied()
    {
        // Foto "em pé" gravada deitada, como o celular faz: 800 × 400 com orientação de 90°.
        var rotated = TestImages.JpegRotated(800, 400);
        using (var original = new MagickImage(rotated))
        {
            original.Orientation.ShouldBe(OrientationType.RightTop);
        }

        var result = _processor.Process(rotated);

        result.Value.Width.ShouldBe(400);
        result.Value.Height.ShouldBe(800);
    }

    [Fact, Trait("Spec", "RF-061")]
    public void LargePhoto_IsReducedKeepingProportion_AndSmallOneIsNotEnlarged()
    {
        var large = _processor.Process(TestImages.Create(MagickFormat.Jpeg, 4000, 2000));
        large.Value.Width.ShouldBe(2400);
        large.Value.Height.ShouldBe(1200);

        var small = _processor.Process(TestImages.Create(MagickFormat.Jpeg, 300, 200));
        small.Value.Width.ShouldBe(300);
    }

    [Theory, Trait("Spec", "RF-061-CA1")]
    [InlineData("isto é um texto com extensão .jpg")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>")]
    [InlineData("")]
    public void FileThatIsNotAnImage_IsRefused(string content)
    {
        _processor.Process(System.Text.Encoding.UTF8.GetBytes(content)).Error.ShouldBe(CatalogErrors.ImageTypeNotAccepted);
    }

    [Fact, Trait("Spec", "RF-061-CA1")]
    public void CorruptedImage_IsRefused()
    {
        // Assinatura de JPEG seguida de lixo: passa pelos magic bytes, mas não é uma foto.
        byte[] corrupted = [0xFF, 0xD8, 0xFF, 0xE0, .. Enumerable.Repeat((byte)0x42, 200)];

        _processor.Process(corrupted).Error.ShouldBe(CatalogErrors.ImageUnreadable);
    }
}

/// <summary>Imagens de teste geradas na hora: nenhum arquivo binário no repositório.</summary>
public static class TestImages
{
    /// <summary>HEIC de verdade, 64 × 48, com EXIF e GPS, gerado com pillow-heif: o Magick.NET lê HEIC, mas não grava.</summary>
    public const string HeicWithGpsBase64 = "AAAAHGZ0eXBoZWljAAAAAG1pZjFoZWljbWlhZgAAAb5tZXRhAAAAAAAAACFoZGxyAAAAAAAAAABwaWN0AAAAAAAAAAAAAAAAAAAAADRpbG9jAAAAAERAAAIAAQAAAAAB4gABAAAAAAAAAB0AAgAAAAAB/wABAAAAAAAAAGwAAAA4aWluZgAAAAAAAgAAABVpbmZlAgAAAAABAABodmMxAAAAABVpbmZlAgAAAQACAABFeGlmAAAAAA5waXRtAAAAAAABAAAA/WlwcnAAAADdaXBjbwAAAHZodmNDAQNwAAAAkAAAAAAAHvAA/P34+AAADwNgAAEAGEABDAH//wNwAAADAJAAAAMAAAMAHroCQGEAAQAqQgEBA3AAAAMAkAAAAwAAAwAeoCCBBZbq5Ka5uAhoMCAAAAMDIAAAAwAhYgABAAZEAcFzwIkAAAATY29scm5jbHgAAQANAAaAAAAAFGlzcGUAAAAAAAAAQAAAAEAAAAAoY2xhcAAAAEAAAAABAAAAMAAAAAEAAAAAAAAAAv////AAAAACAAAAEHBpeGkAAAAAAwgICAAAABhpcG1hAAAAAAAAAAEAAQWBAgMFhAAAABppcmVmAAAAAAAAAA5jZHNjAAIAAQABAAAAkW1kYXQAAAAZKAGvE4D4EOGnf/rko//p1zw8nm786KQz4AAAAAZFeGlmAABNTQAqAAAACAACAQ8AAgAAAAYAAAAmiCUABAAAAAEAAAAsAAAAAEFwcGxlAAACAAEAAgAAAAJTAAAAAAIABQAAAAMAAABKAAAAAAAAAB4AAAABAAAAAgAAAAEAAAAAAAAAAQ==";

    public static byte[] Create(MagickFormat format, uint width, uint height)
    {
        using var image = new MagickImage(MagickColors.Goldenrod, width, height);
        image.Format = format;
        return image.ToByteArray();
    }

    public static byte[] JpegWithGps()
    {
        using var image = new MagickImage(MagickColors.Goldenrod, 200, 200);
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.GPSLatitudeRef, "S");
        exif.SetValue(ExifTag.GPSLatitude, [new Rational(30, 1), new Rational(2, 1), new Rational(0, 1)]);
        exif.SetValue(ExifTag.Make, "Apple");
        image.SetProfile(exif);
        image.Format = MagickFormat.Jpeg;
        return image.ToByteArray();
    }

    public static byte[] JpegRotated(uint width, uint height)
    {
        using var image = new MagickImage(MagickColors.Goldenrod, width, height);
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Orientation, (ushort)OrientationType.RightTop);
        image.SetProfile(exif);

        // Ao gravar, o ImageMagick reescreve o EXIF a partir desta propriedade.
        image.Orientation = OrientationType.RightTop;
        image.Format = MagickFormat.Jpeg;
        return image.ToByteArray();
    }
}
