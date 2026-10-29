namespace ECommerce.Application.Catalog;

public sealed record SeoResponse(string Title, string Description);

public sealed record ColorResponse(Guid Id, string Name, string Slug, string Hex);

public sealed record SizeResponse(Guid Id, string Name);

public sealed record ProductImageResponse(string Url, string Alt, Guid? ColorId);
