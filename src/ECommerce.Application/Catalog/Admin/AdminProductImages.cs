using ECommerce.Application.Abstractions.Images;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Application.Abstractions.Outbox;
using ECommerce.Domain.Abstractions;
using ECommerce.Domain.Catalog;
using FluentValidation;

namespace ECommerce.Application.Catalog.Admin;

// RF-061 — fotos do produto no painel. Cada arquivo chega numa requisição própria (limite do Cloudflare).

/// <summary>Mensagem do Outbox <see cref="Type"/>: apaga o arquivo do bucket depois do commit (RF-061 CA4).</summary>
public sealed record DeleteStoredImage(string StorageKey)
{
    public const string Type = "catalog.delete-stored-image";
}

public sealed record UploadedImageResponse(Guid Id, string Url, string Alt, Guid? ColorId, short Position, int Width, int Height);

public sealed record UploadProductImageCommand(Guid ProductId, byte[] Content, string Alt, Guid? ColorId) : ICommand<UploadedImageResponse>
{
    public const int MaxBytes = 15 * 1024 * 1024;
}

public sealed record UpdateProductImageCommand(Guid ProductId, Guid ImageId, string Alt, Guid? ColorId) : ICommand;

public sealed record DeleteProductImageCommand(Guid ProductId, Guid ImageId) : ICommand;

public sealed record ReorderProductImagesCommand(Guid ProductId, Guid? ColorId, IReadOnlyList<Guid> ImageIds) : ICommand;

internal sealed class UploadProductImageValidator : AbstractValidator<UploadProductImageCommand>
{
    public UploadProductImageValidator()
    {
        RuleFor(x => x.Content.Length)
            .GreaterThan(0).WithMessage("Escolha um arquivo.")
            .LessThanOrEqualTo(UploadProductImageCommand.MaxBytes).WithMessage("A foto passa de 15 MB.")
            .OverridePropertyName("file");
        RuleFor(x => x.Alt).NotEmpty().MaximumLength(ProductImage.AltMaxLength);
    }
}

internal sealed class UpdateProductImageValidator : AbstractValidator<UpdateProductImageCommand>
{
    public UpdateProductImageValidator() => RuleFor(x => x.Alt).NotEmpty().MaximumLength(ProductImage.AltMaxLength);
}

internal sealed class UploadProductImageHandler(
    IProductRepository products,
    IProductImageRepository images,
    IColorRepository colors,
    IImageProcessor processor,
    IImageStorage storage,
    IOutbox outbox,
    TimeProvider time) : ICommandHandler<UploadProductImageCommand, UploadedImageResponse>
{
    public async Task<Result<UploadedImageResponse>> Handle(UploadProductImageCommand command, CancellationToken cancellationToken)
    {
        var productId = new ProductId(command.ProductId);
        if (await products.GetByIdAsync(productId, cancellationToken) is null)
        {
            return CatalogErrors.ProductNotFound;
        }

        var colorId = command.ColorId is { } id ? new ColorId(id) : (ColorId?)null;
        if (colorId is { } color && await colors.GetByIdAsync(color, cancellationToken) is null)
        {
            return CatalogErrors.ColorNotFound;
        }

        var processed = processor.Process(command.Content);
        if (processed.IsFailure)
        {
            return processed.Error;
        }

        var imageId = ProductImageId.New();
        var key = $"products/{productId.Value}/{imageId.Value}.webp";
        var position = await images.NextPositionAsync(productId, colorId, cancellationToken);
        var image = ProductImage.Create(
            imageId, productId, colorId, storage.PublicUrl(key), key, command.Alt, position,
            processed.Value.Width, processed.Value.Height, time.GetUtcNow().UtcDateTime);
        if (image.IsFailure)
        {
            return image.Error;
        }

        // A linha entra antes do envio: se o envio falhar, a transação desfaz e não sobra nada.
        await images.AddAsync(image.Value, cancellationToken);
        await CatalogChanged.ProductsAsync(outbox, cancellationToken);
        await storage.PutAsync(key, processed.Value.Content, ProcessedImage.ContentType, cancellationToken);

        return new UploadedImageResponse(
            imageId.Value, image.Value.Url, image.Value.Alt, command.ColorId, position, processed.Value.Width, processed.Value.Height);
    }
}

internal sealed class UpdateProductImageHandler(IProductImageRepository images, IColorRepository colors, IOutbox outbox)
    : ICommandHandler<UpdateProductImageCommand>
{
    public async Task<Result> Handle(UpdateProductImageCommand command, CancellationToken cancellationToken)
    {
        var image = await images.GetByIdAsync(new ProductImageId(command.ImageId), cancellationToken);
        if (image is null || image.ProductId.Value != command.ProductId)
        {
            return Result.Failure(CatalogErrors.ImageNotFound);
        }

        var alt = image.SetAlt(command.Alt);
        if (alt.IsFailure)
        {
            return alt;
        }

        var colorId = command.ColorId is { } id ? new ColorId(id) : (ColorId?)null;
        if (colorId != image.ColorId)
        {
            if (colorId is { } color && await colors.GetByIdAsync(color, cancellationToken) is null)
            {
                return Result.Failure(CatalogErrors.ColorNotFound);
            }

            image.MoveToColor(colorId, await images.NextPositionAsync(image.ProductId, colorId, cancellationToken));
        }

        await images.UpdateAsync(image, cancellationToken);
        await CatalogChanged.ProductsAsync(outbox, cancellationToken);
        return Result.Success();
    }
}

internal sealed class DeleteProductImageHandler(IProductRepository products, IProductImageRepository images, IOutbox outbox)
    : ICommandHandler<DeleteProductImageCommand>
{
    public async Task<Result> Handle(DeleteProductImageCommand command, CancellationToken cancellationToken)
    {
        var image = await images.GetByIdAsync(new ProductImageId(command.ImageId), cancellationToken);
        if (image is null || image.ProductId.Value != command.ProductId)
        {
            return Result.Failure(CatalogErrors.ImageNotFound);
        }

        // Mesma regra de ativar (RF-051 CA3): produto na loja não fica sem nenhuma foto.
        var product = await products.GetByIdAsync(image.ProductId, cancellationToken);
        if (product is { IsActive: true } && await images.CountForProductAsync(image.ProductId, cancellationToken) == 1)
        {
            return Result.Failure(CatalogErrors.ActiveProductNeedsImage);
        }

        await images.DeleteAsync(image.Id, cancellationToken);
        if (image.StorageKey is not null)
        {
            await outbox.EnqueueAsync(DeleteStoredImage.Type, new DeleteStoredImage(image.StorageKey), cancellationToken);
        }

        await CatalogChanged.ProductsAsync(outbox, cancellationToken);
        return Result.Success();
    }
}

internal sealed class ReorderProductImagesHandler(IProductImageRepository images, IOutbox outbox) : ICommandHandler<ReorderProductImagesCommand>
{
    public async Task<Result> Handle(ReorderProductImagesCommand command, CancellationToken cancellationToken)
    {
        var productId = new ProductId(command.ProductId);
        var colorId = command.ColorId is { } id ? new ColorId(id) : (ColorId?)null;
        var gallery = await images.ListForGalleryAsync(productId, colorId, cancellationToken);

        // A lista precisa ser exatamente a galeria atual: outra aba pode ter enviado ou removido uma foto.
        var current = gallery.Select(image => image.Id.Value).ToHashSet();
        if (command.ImageIds.Count != current.Count || !command.ImageIds.All(current.Contains))
        {
            return Result.Failure(CatalogErrors.ImageOrderOutdated);
        }

        await images.ReorderAsync(productId, command.ImageIds.Select(imageId => new ProductImageId(imageId)).ToList(), cancellationToken);
        await CatalogChanged.ProductsAsync(outbox, cancellationToken);
        return Result.Success();
    }
}
