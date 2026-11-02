using Dapper;
using ECommerce.Application.Abstractions.Data;
using ECommerce.Domain.Shipping;

namespace ECommerce.Infrastructure.Persistence.Repositories;

internal sealed class ShippingRuleRepository(IUnitOfWork unitOfWork) : IShippingRuleRepository
{
    public Task AddAsync(ShippingRule rule, DateTime nowUtc, CancellationToken cancellationToken) =>
        unitOfWork.Connection.ExecuteAsync(Command(
            """
            INSERT INTO shipping.rules
                (id, name, priority, is_active, valid_from, valid_to, min_subtotal, states, postal_code_ranges, service_codes,
                 effect, effect_value, created_at, updated_at)
            VALUES
                (@Id, @Name, @Priority, @IsActive, @ValidFrom::date, @ValidTo::date, @MinSubtotal, @States, @Ranges, @Services,
                 @Effect, @EffectValue, @Now, @Now)
            """,
            Parameters(rule, nowUtc),
            cancellationToken));

    public async Task<bool> UpdateAsync(ShippingRule rule, DateTime nowUtc, CancellationToken cancellationToken) =>
        await unitOfWork.Connection.ExecuteAsync(Command(
            """
            UPDATE shipping.rules
               SET name = @Name, priority = @Priority, is_active = @IsActive, valid_from = @ValidFrom::date, valid_to = @ValidTo::date,
                   min_subtotal = @MinSubtotal, states = @States, postal_code_ranges = @Ranges, service_codes = @Services,
                   effect = @Effect, effect_value = @EffectValue, updated_at = @Now
             WHERE id = @Id
            """,
            Parameters(rule, nowUtc),
            cancellationToken)) > 0;

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
        await unitOfWork.Connection.ExecuteAsync(Command("DELETE FROM shipping.rules WHERE id = @id", new { id }, cancellationToken)) > 0;

    private static object Parameters(ShippingRule rule, DateTime nowUtc)
    {
        var content = rule.Content;
        return new
        {
            rule.Id,
            content.Name,
            content.Priority,
            content.IsActive,
            ValidFrom = content.ValidFrom?.ToDateTime(TimeOnly.MinValue),
            ValidTo = content.ValidTo?.ToDateTime(TimeOnly.MinValue),
            content.MinSubtotal,
            States = content.States.ToArray(),
            Ranges = content.PostalCodeRanges.Select(range => range.ToStored()).ToArray(),
            Services = content.ServiceCodes.ToArray(),
            Effect = content.Effect.ToString(),
            content.EffectValue,
            Now = nowUtc,
        };
    }

    private CommandDefinition Command(string sql, object parameters, CancellationToken cancellationToken) =>
        new(sql, parameters, unitOfWork.Transaction, cancellationToken: cancellationToken);
}
