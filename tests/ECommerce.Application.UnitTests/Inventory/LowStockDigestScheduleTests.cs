using ECommerce.Application.Inventory;

namespace ECommerce.Application.UnitTests.Inventory;

/// <summary>EM-11 uma vez por dia a partir das 8h de São Paulo (entrega 6 §3, P4). São Paulo está em UTC−3, sem horário de verão.</summary>
public sealed class LowStockDigestScheduleTests
{
    [Theory, Trait("Spec", "EM-11")]
    [InlineData("2026-11-16T11:00:00Z", "2026-11-16T11:00:00Z")] // 8h00 em SP: horário de hoje
    [InlineData("2026-11-16T20:30:00Z", "2026-11-16T11:00:00Z")] // 17h30 em SP: ainda o de hoje
    [InlineData("2026-11-16T10:59:59Z", "2026-11-15T11:00:00Z")] // 7h59 em SP: o de ontem
    [InlineData("2026-11-17T02:00:00Z", "2026-11-16T11:00:00Z")] // 23h em SP do dia 16, já dia 17 em UTC
    public void CurrentSlot_IsTheLatestEightAmInSaoPaulo(string now, string expected)
    {
        var slot = SendLowStockDigestHandler.CurrentSlot(DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture));

        slot.ShouldBe(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture).UtcDateTime);
        slot.Kind.ShouldBe(DateTimeKind.Utc);
    }
}
