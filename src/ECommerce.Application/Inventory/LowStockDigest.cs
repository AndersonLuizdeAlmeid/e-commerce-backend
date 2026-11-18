using System.Globalization;
using System.Text.Json;
using Dapper;
using ECommerce.Application.Abstractions.Data;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Application.Abstractions.Outbox;
using ECommerce.Application.Notifications;
using ECommerce.Domain.Abstractions;

namespace ECommerce.Application.Inventory;

/// <summary>
/// EM-11 (arquivo 23 §3, entrega 6 §3 P4): uma vez por dia, a partir das <see cref="Hour"/> h de São Paulo, avisa a loja
/// das variações à venda no limite de estoque baixo ou abaixo dele. Sem nenhuma, não manda nada. Devolve quantas
/// variações entraram no e-mail (0 quando ainda não é hora, quando já saiu hoje ou quando não há nenhuma).
/// </summary>
public sealed record SendLowStockDigestCommand : ICommand<int>
{
    public const int Hour = 8;
    public const int MaxItems = 50;
    public const string MarkKey = "em-11";
}

/// <summary>Linha do EM-11: peça, cor e tamanho, SKU e saldo.</summary>
public sealed record LowStockEmailLine(string Name, string? Detail, string Sku, int QuantityOnHand, int Threshold);

internal sealed class SendLowStockDigestHandler(IUnitOfWork unitOfWork, IOutbox outbox, TimeProvider time)
    : ICommandHandler<SendLowStockDigestCommand, int>
{
    private static readonly TimeZoneInfo SaoPaulo = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    // A marca é o que garante um e-mail por dia: com duas instâncias, só uma consegue avançá-la para o horário de hoje.
    private const string ClaimSql = """
        INSERT INTO ops.job_marks (key, last_at) VALUES (@Key, @Slot)
        ON CONFLICT (key) DO UPDATE SET last_at = EXCLUDED.last_at WHERE ops.job_marks.last_at < EXCLUDED.last_at
        RETURNING key
        """;

    private const string ItemsSql = """
        SELECT count(*)
          FROM inventory.stock_items st
          JOIN catalog.product_variants v ON v.id = st.variant_id
          JOIN catalog.products p ON p.id = v.product_id
         WHERE st.quantity_on_hand <= st.low_stock_threshold AND v.is_active AND p.is_active;

        SELECT p.name AS product_name, co.name AS color_name, s.name AS size_name, v.sku,
               st.quantity_on_hand, st.low_stock_threshold
          FROM inventory.stock_items st
          JOIN catalog.product_variants v ON v.id = st.variant_id
          JOIN catalog.products p ON p.id = v.product_id
          LEFT JOIN catalog.colors co ON co.id = v.color_id
          LEFT JOIN catalog.sizes s ON s.id = v.size_id
         WHERE st.quantity_on_hand <= st.low_stock_threshold AND v.is_active AND p.is_active
         ORDER BY st.quantity_on_hand, p.name, co.name NULLS FIRST, s.sort_order NULLS FIRST, v.id
         LIMIT @MaxItems;
        """;

    public async Task<Result<int>> Handle(SendLowStockDigestCommand command, CancellationToken cancellationToken)
    {
        var slot = CurrentSlot(time.GetUtcNow());
        var claimed = await unitOfWork.Connection.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            ClaimSql, new { Key = SendLowStockDigestCommand.MarkKey, Slot = slot }, unitOfWork.Transaction, cancellationToken: cancellationToken));
        if (claimed is null)
        {
            return 0;
        }

        using var grid = await unitOfWork.Connection.QueryMultipleAsync(new CommandDefinition(
            ItemsSql, new { SendLowStockDigestCommand.MaxItems }, unitOfWork.Transaction, cancellationToken: cancellationToken));
        var total = await grid.ReadSingleAsync<int>();
        var lines = (await grid.ReadAsync<Row>())
            .Select(row => new LowStockEmailLine(
                row.ProductName,
                string.Join(" · ", new[] { row.ColorName, row.SizeName }.Where(part => !string.IsNullOrEmpty(part))) is { Length: > 0 } detail ? detail : null,
                row.Sku,
                row.QuantityOnHand,
                row.LowStockThreshold))
            .ToList();

        if (lines.Count == 0)
        {
            return 0;
        }

        await outbox.EnqueueAsync(
            SendEmail.Type,
            new SendEmail(
                EmailTemplates.LowStockDigest,
                SendEmail.StoreRecipient,
                null,
                new Dictionary<string, string>
                {
                    ["count"] = total.ToString(CultureInfo.InvariantCulture),
                    ["items"] = JsonSerializer.Serialize(lines, JsonSerializerOptions.Web),
                },
                new Dictionary<string, string>()),
            cancellationToken);

        return lines.Count;
    }

    /// <summary>O horário de hoje se já passou das <see cref="SendLowStockDigestCommand.Hour"/> h em São Paulo; senão, o de ontem.</summary>
    internal static DateTime CurrentSlot(DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, SaoPaulo);
        var slot = local.Date.AddHours(SendLowStockDigestCommand.Hour);
        if (local.DateTime < slot)
        {
            slot = slot.AddDays(-1);
        }

        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(slot, DateTimeKind.Unspecified), SaoPaulo);
    }

    private sealed class Row
    {
        public string ProductName { get; init; } = string.Empty;
        public string? ColorName { get; init; }
        public string? SizeName { get; init; }
        public string Sku { get; init; } = string.Empty;
        public int QuantityOnHand { get; init; }
        public int LowStockThreshold { get; init; }
    }
}
