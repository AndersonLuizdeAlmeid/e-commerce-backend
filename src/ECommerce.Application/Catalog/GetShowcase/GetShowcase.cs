using Dapper;
using ECommerce.Application.Abstractions.Caching;
using ECommerce.Application.Abstractions.Data;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Application.Catalog.Admin;
using ECommerce.Application.Catalog.ListProducts;
using ECommerce.Domain.Abstractions;

namespace ECommerce.Application.Catalog.GetShowcase;

/// <summary>RF-025 — peça em destaque e ocasião vigente, para a home.</summary>
public sealed record GetShowcaseQuery : IQuery<ShowcaseResponse>;

public sealed record ShowcaseResponse(FeaturedResponse? Featured, OccasionResponse? Occasion);

/// <summary><c>IsFallback</c>: o destaque não está disponível e veio a novidade mais recente com estoque (CA1).</summary>
public sealed record FeaturedResponse(ProductSummaryResponse Product, Guid? ColorId, bool IsFallback);

public sealed record OccasionResponse(string Name, string Text, string? ImageUrl, IReadOnlyList<ProductSummaryResponse> Products);

internal sealed class GetShowcaseHandler(IDbConnectionFactory connectionFactory, ICacheService cache, TimeProvider time)
    : IQueryHandler<GetShowcaseQuery, ShowcaseResponse>
{
    // Mesma visibilidade da listagem (RF-020 CA1), mais o estoque que o CA1 pede.
    private const string InStock = $"""
        p.is_active
        AND EXISTS (SELECT 1 FROM catalog.categories c WHERE c.id = p.category_id AND c.is_active)
        AND EXISTS ({ShowcaseSql.VariantWithStock})
        """;

    private const string FeaturedSql = $"""
        SELECT f.product_id, f.color_id
          FROM catalog.showcase_featured f
          JOIN catalog.products p ON p.id = f.product_id
         WHERE {InStock}
        """;

    private const string NewestSql = $"""
        SELECT p.id
          FROM catalog.products p
         WHERE {InStock}
         ORDER BY p.created_at DESC, p.id
         LIMIT 1
        """;

    // CA3: o Postgres converte para Brasília, sem depender do tzdata da imagem da API. Nenhum job: fora do período, a linha não volta.
    private const string OccasionSql = """
        SELECT id, name, short_text, image_url
          FROM catalog.occasions
         WHERE is_active AND (@Now::timestamptz AT TIME ZONE 'America/Sao_Paulo')::date BETWEEN starts_on AND ends_on
         LIMIT 1;
        """;

    private const string OccasionProductsSql = "SELECT product_id FROM catalog.occasion_products WHERE occasion_id = @Id ORDER BY position";

    public async Task<Result<ShowcaseResponse>> Handle(GetShowcaseQuery query, CancellationToken cancellationToken)
    {
        var showcase = await cache.GetOrCreateAsync(CatalogCache.ShowcaseKey, LoadAsync, CatalogCache.Showcase, cancellationToken);
        return showcase;
    }

    private async ValueTask<ShowcaseResponse> LoadAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var featured = await LoadFeaturedAsync(connection, cancellationToken);
        var occasion = await LoadOccasionAsync(connection, cancellationToken);
        return new ShowcaseResponse(featured, occasion);
    }

    private static async Task<FeaturedResponse?> LoadFeaturedAsync(System.Data.Common.DbConnection connection, CancellationToken cancellationToken)
    {
        var chosen = await connection.QuerySingleOrDefaultAsync<FeaturedRow>(new CommandDefinition(FeaturedSql, cancellationToken: cancellationToken));
        var productId = chosen?.ProductId
            ?? await connection.QuerySingleOrDefaultAsync<Guid?>(new CommandDefinition(NewestSql, cancellationToken: cancellationToken));
        if (productId is not { } id)
        {
            return null;
        }

        var product = (await ProductSummaries.LoadVisibleAsync(connection, [id], cancellationToken)).SingleOrDefault();
        if (product is null)
        {
            return null;
        }

        // O acabamento padrão escolhido no painel (RF-025) leva a foto dele, quando houver.
        if (chosen?.ColorId is { } colorId)
        {
            var image = await connection.QueryFirstOrDefaultAsync<ImageRow>(new CommandDefinition(
                "SELECT url, alt FROM catalog.product_images WHERE product_id = @Id AND color_id = @ColorId ORDER BY position LIMIT 1",
                new { Id = id, ColorId = colorId },
                cancellationToken: cancellationToken));
            if (image is not null)
            {
                product = product with { Image = new ProductImageResponse(image.Url, image.Alt, colorId) };
            }
        }

        return new FeaturedResponse(product, chosen?.ColorId, IsFallback: chosen is null);
    }

    private async Task<OccasionResponse?> LoadOccasionAsync(System.Data.Common.DbConnection connection, CancellationToken cancellationToken)
    {
        var occasion = await connection.QuerySingleOrDefaultAsync<OccasionRow>(new CommandDefinition(
            OccasionSql, new { Now = time.GetUtcNow().UtcDateTime }, cancellationToken: cancellationToken));
        if (occasion is null)
        {
            return null;
        }

        var ids = (await connection.QueryAsync<Guid>(
            new CommandDefinition(OccasionProductsSql, new { occasion.Id }, cancellationToken: cancellationToken))).AsList();
        var products = await ProductSummaries.LoadVisibleAsync(connection, ids, cancellationToken);

        // CA4: sem nenhuma peça na loja, a ocasião não aparece.
        return products.Count == 0 ? null : new OccasionResponse(occasion.Name, occasion.ShortText, occasion.ImageUrl, products);
    }

    private sealed class FeaturedRow
    {
        public Guid ProductId { get; init; }
        public Guid? ColorId { get; init; }
    }

    private sealed class ImageRow
    {
        public string Url { get; init; } = string.Empty;
        public string Alt { get; init; } = string.Empty;
    }

    private sealed class OccasionRow
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string ShortText { get; init; } = string.Empty;
        public string? ImageUrl { get; init; }
    }
}
