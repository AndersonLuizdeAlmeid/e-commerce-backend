using System.Globalization;
using System.Text;
using System.Xml;
using Dapper;
using ECommerce.Application.Abstractions.Caching;
using ECommerce.Application.Abstractions.Data;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Domain.Abstractions;

namespace ECommerce.Application.Catalog.GetMerchantFeed;

// Feed do Google Merchant Center (SEO-14, entrega 6 fatia 6.6): uma entrada por variação ativa, agrupada pela peça.

/// <summary>Configuração do feed. <see cref="StoreUrl"/> é a loja (links), sem barra no fim.</summary>
public sealed class MerchantFeedOptions
{
    public string StoreUrl { get; init; } = "http://localhost:3000";

    public string StoreName { get; init; } = "MO Acessórios";

    public string Brand { get; init; } = "MO Acessórios";

    /// <summary>Gênero das peças, exigido pelo Google para acessórios no Brasil.</summary>
    public string DefaultGender { get; init; } = "female";

    /// <summary>Coleções cujas peças são masculinas (a coleção "Eles").</summary>
    public IReadOnlyList<string> MaleCollections { get; init; } = ["eles"];
}

/// <summary>Uma variação no feed, já com tudo o que o Google pede.</summary>
public sealed record MerchantFeedItem(
    string Id,
    string GroupId,
    string Title,
    string Description,
    string Link,
    string ImageLink,
    IReadOnlyList<string> AdditionalImageLinks,
    bool InStock,
    decimal Price,
    string? Gtin,
    string? Color,
    string? Size,
    string ProductType,
    string Gender);

public sealed record MerchantFeed(string StoreName, string StoreUrl, string Brand, IReadOnlyList<MerchantFeedItem> Items)
{
    /// <summary>RSS 2.0 com o namespace <c>g:</c> (especificação de dados de produto do Merchant Center).</summary>
    public string ToXml()
    {
        var output = new StringBuilder();
        using (var xml = XmlWriter.Create(output, new XmlWriterSettings { Indent = true, Encoding = Encoding.UTF8, OmitXmlDeclaration = true }))
        {
            const string g = "http://base.google.com/ns/1.0";
            xml.WriteStartElement("rss");
            xml.WriteAttributeString("version", "2.0");
            xml.WriteAttributeString("xmlns", "g", null, g);
            xml.WriteStartElement("channel");
            xml.WriteElementString("title", StoreName);
            xml.WriteElementString("link", StoreUrl);
            xml.WriteElementString("description", $"Peças da {StoreName}");

            foreach (var item in Items)
            {
                xml.WriteStartElement("item");
                xml.WriteElementString("g", "id", g, item.Id);
                xml.WriteElementString("g", "item_group_id", g, item.GroupId);
                xml.WriteElementString("title", item.Title);
                xml.WriteElementString("description", item.Description);
                xml.WriteElementString("link", item.Link);
                xml.WriteElementString("g", "image_link", g, item.ImageLink);
                foreach (var image in item.AdditionalImageLinks)
                {
                    xml.WriteElementString("g", "additional_image_link", g, image);
                }

                xml.WriteElementString("g", "availability", g, item.InStock ? "in_stock" : "out_of_stock");
                xml.WriteElementString("g", "price", g, item.Price.ToString("0.00", CultureInfo.InvariantCulture) + " BRL");
                xml.WriteElementString("g", "condition", g, "new");
                xml.WriteElementString("g", "brand", g, Brand);
                if (item.Gtin is null)
                {
                    xml.WriteElementString("g", "identifier_exists", g, "no");
                }
                else
                {
                    xml.WriteElementString("g", "gtin", g, item.Gtin);
                }

                if (item.Color is not null)
                {
                    xml.WriteElementString("g", "color", g, item.Color);
                }

                if (item.Size is not null)
                {
                    xml.WriteElementString("g", "size", g, item.Size);
                }

                xml.WriteElementString("g", "product_type", g, item.ProductType);
                xml.WriteElementString("g", "age_group", g, "adult");
                xml.WriteElementString("g", "gender", g, item.Gender);
                xml.WriteEndElement();
            }

            xml.WriteEndElement();
            xml.WriteEndElement();
        }

        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + output;
    }
}

public sealed record GetMerchantFeedQuery : IQuery<MerchantFeed>;

/// <summary>GTIN que o Google aceita: 8, 12, 13 ou 14 dígitos com o dígito verificador certo. Outro código fica de fora.</summary>
public static class Gtin
{
    public static string? Normalize(string? value)
    {
        var digits = value?.Trim();
        if (digits is null || digits.Length is not (8 or 12 or 13 or 14) || !digits.All(char.IsAsciiDigit))
        {
            return null;
        }

        // Da direita para a esquerda, sem o verificador: pesos 3, 1, 3, 1...
        var sum = 0;
        for (var position = 0; position < digits.Length - 1; position++)
        {
            var digit = digits[digits.Length - 2 - position] - '0';
            sum += digit * (position % 2 == 0 ? 3 : 1);
        }

        return (10 - sum % 10) % 10 == digits[^1] - '0' ? digits : null;
    }
}

/// <summary>
/// Peças visíveis na loja (ativas, com categoria ativa e foto), uma entrada por variação ativa. Esgotada continua no feed
/// como <c>out_of_stock</c>: o Google mantém o histórico do item em vez de recomeçar quando a peça volta. Cache de 1 h,
/// apagado junto com o catálogo (tag de produtos).
/// </summary>
internal sealed class GetMerchantFeedHandler(IDbConnectionFactory connectionFactory, ICacheService cache, MerchantFeedOptions options)
    : IQueryHandler<GetMerchantFeedQuery, MerchantFeed>
{
    private const string Sql = """
        SELECT v.id, v.sku, v.barcode, p.id AS product_id, p.slug, p.name, p.description, p.seo_description,
               coalesce(v.price, p.base_price) AS price, v.color_id, co.name AS color_name, co.slug AS color_slug,
               sz.name AS size_name, c.name AS category_name, parent.name AS parent_category_name,
               coalesce(s.quantity_on_hand, 0) > 0 AS in_stock,
               EXISTS (SELECT 1 FROM catalog.product_collections pc
                         JOIN catalog.collections cl ON cl.id = pc.collection_id
                        WHERE pc.product_id = p.id AND cl.slug = ANY(@MaleCollections)) AS is_male
          FROM catalog.product_variants v
          JOIN catalog.products p ON p.id = v.product_id AND p.is_active
          JOIN catalog.categories c ON c.id = p.category_id AND c.is_active
          LEFT JOIN catalog.categories parent ON parent.id = c.parent_id
          LEFT JOIN catalog.colors co ON co.id = v.color_id
          LEFT JOIN catalog.sizes sz ON sz.id = v.size_id
          LEFT JOIN inventory.stock_items s ON s.variant_id = v.id
         WHERE v.is_active AND (parent.id IS NULL OR parent.is_active)
         ORDER BY p.slug, co.name NULLS FIRST, sz.sort_order NULLS FIRST, v.sku;

        SELECT i.product_id, i.color_id, i.url
          FROM catalog.product_images i
          JOIN catalog.products p ON p.id = i.product_id AND p.is_active
         ORDER BY i.product_id, i.position;
        """;

    private const int MaxAdditionalImages = 10;

    public async Task<Result<MerchantFeed>> Handle(GetMerchantFeedQuery query, CancellationToken cancellationToken)
    {
        var feed = await cache.GetOrCreateAsync(CatalogCache.MerchantFeedKey, LoadAsync, CatalogCache.MerchantFeed, cancellationToken);
        return feed;
    }

    private async ValueTask<MerchantFeed> LoadAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        using var grid = await connection.QueryMultipleAsync(new CommandDefinition(
            Sql, new { MaleCollections = options.MaleCollections.ToArray() }, cancellationToken: cancellationToken));

        var variants = (await grid.ReadAsync<VariantRow>()).ToList();
        var images = (await grid.ReadAsync<ImageRow>()).ToLookup(row => row.ProductId);
        var storeUrl = options.StoreUrl.TrimEnd('/');

        var items = new List<MerchantFeedItem>(variants.Count);
        foreach (var row in variants)
        {
            // Fotos da cor primeiro, depois as que valem para todas as cores (arquivo 05, product_images.color_id).
            var productImages = images[row.ProductId];
            var gallery = productImages.Where(image => row.ColorId is not null && image.ColorId == row.ColorId)
                .Concat(productImages.Where(image => image.ColorId is null))
                .Select(image => Absolute(image.Url, storeUrl))
                .Distinct()
                .ToList();
            if (gallery.Count == 0)
            {
                // O Google recusa item sem foto; a peça entra quando ganhar uma.
                continue;
            }

            var query = new List<string>();
            if (row.ColorSlug is not null)
            {
                query.Add("cor=" + Uri.EscapeDataString(row.ColorSlug));
            }

            if (row.SizeName is not null)
            {
                query.Add("tamanho=" + Uri.EscapeDataString(row.SizeName));
            }

            var description = FirstNonBlank(row.Description, row.SeoDescription, row.Name);
            items.Add(new MerchantFeedItem(
                row.Sku,
                row.ProductId.ToString(),
                string.Join(" - ", new[] { row.Name, row.ColorName, row.SizeName }.Where(part => !string.IsNullOrWhiteSpace(part))),
                description.Length > 5000 ? description[..5000] : description,
                $"{storeUrl}/produto/{Uri.EscapeDataString(row.Slug)}" + (query.Count > 0 ? "?" + string.Join("&", query) : string.Empty),
                gallery[0],
                gallery.Skip(1).Take(MaxAdditionalImages).ToList(),
                row.InStock,
                row.Price,
                Gtin.Normalize(row.Barcode),
                row.ColorName,
                row.SizeName,
                row.ParentCategoryName is null ? row.CategoryName : $"{row.ParentCategoryName} > {row.CategoryName}",
                row.IsMale ? "male" : options.DefaultGender));
        }

        return new MerchantFeed(options.StoreName, storeUrl, options.Brand, items);
    }

    private static string FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    /// <summary>Foto enviada pelo painel já é absoluta (R2); caminho relativo é servido pela loja.</summary>
    private static string Absolute(string url, string storeUrl) =>
        Uri.TryCreate(url, UriKind.Absolute, out _) ? url : $"{storeUrl}/{url.TrimStart('/')}";

    private sealed class VariantRow
    {
        public Guid Id { get; init; }
        public string Sku { get; init; } = string.Empty;
        public string? Barcode { get; init; }
        public Guid ProductId { get; init; }
        public string Slug { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string? Description { get; init; }
        public string? SeoDescription { get; init; }
        public decimal Price { get; init; }
        public Guid? ColorId { get; init; }
        public string? ColorName { get; init; }
        public string? ColorSlug { get; init; }
        public string? SizeName { get; init; }
        public string CategoryName { get; init; } = string.Empty;
        public string? ParentCategoryName { get; init; }
        public bool InStock { get; init; }
        public bool IsMale { get; init; }
    }

    private sealed class ImageRow
    {
        public Guid ProductId { get; init; }
        public Guid? ColorId { get; init; }
        public string Url { get; init; } = string.Empty;
    }
}
