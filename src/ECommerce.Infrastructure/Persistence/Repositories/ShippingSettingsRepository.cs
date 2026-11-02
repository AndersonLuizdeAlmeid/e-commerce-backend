using Dapper;
using ECommerce.Application.Abstractions.Data;
using ECommerce.Domain.Shipping;

namespace ECommerce.Infrastructure.Persistence.Repositories;

internal sealed class ShippingSettingsRepository(IUnitOfWork unitOfWork) : IShippingSettingsRepository
{
    public Task SaveAsync(ShippingSettings settings, DateTime nowUtc, CancellationToken cancellationToken) =>
        unitOfWork.Connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO shipping.settings
                (id, origin_postal_code, handling_days, fallback_pac_price, fallback_sedex_price,
                 box_weight_grams, box_height_cm, box_width_cm, box_length_cm, updated_at)
            VALUES
                (1, @Origin, @HandlingDays, @FallbackPacPrice, @FallbackSedexPrice,
                 @BoxWeightGrams, @BoxHeightCm, @BoxWidthCm, @BoxLengthCm, @UpdatedAt)
            ON CONFLICT (id) DO UPDATE
               SET origin_postal_code = EXCLUDED.origin_postal_code, handling_days = EXCLUDED.handling_days,
                   fallback_pac_price = EXCLUDED.fallback_pac_price, fallback_sedex_price = EXCLUDED.fallback_sedex_price,
                   box_weight_grams = EXCLUDED.box_weight_grams, box_height_cm = EXCLUDED.box_height_cm,
                   box_width_cm = EXCLUDED.box_width_cm, box_length_cm = EXCLUDED.box_length_cm,
                   updated_at = EXCLUDED.updated_at
            """,
            new
            {
                Origin = settings.Origin.Value,
                settings.HandlingDays,
                settings.FallbackPacPrice,
                settings.FallbackSedexPrice,
                settings.BoxWeightGrams,
                BoxHeightCm = settings.Box.HeightCm,
                BoxWidthCm = settings.Box.WidthCm,
                BoxLengthCm = settings.Box.LengthCm,
                UpdatedAt = nowUtc,
            },
            unitOfWork.Transaction,
            cancellationToken: cancellationToken));
}
