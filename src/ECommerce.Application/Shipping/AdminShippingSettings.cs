using Dapper;
using ECommerce.Application.Abstractions.Data;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Domain.Abstractions;
using ECommerce.Domain.Shipping;
using FluentValidation;

namespace ECommerce.Application.Shipping;

// RF-058: CEP de origem, manuseio, contingência e caixa padrão. Trocar a configuração não precisa limpar cache:
// origem e pacote fazem parte da chave da cotação, e o resto é aplicado depois dela.

public sealed record ShippingSettingsResponse(
    string OriginPostalCode,
    int HandlingDays,
    decimal FallbackPacPrice,
    decimal FallbackSedexPrice,
    int BoxWeightGrams,
    decimal BoxHeightCm,
    decimal BoxWidthCm,
    decimal BoxLengthCm);

public sealed record GetShippingSettingsQuery : IQuery<ShippingSettingsResponse?>;

public sealed record SaveShippingSettingsCommand(
    string OriginPostalCode,
    int HandlingDays,
    decimal FallbackPacPrice,
    decimal FallbackSedexPrice,
    int BoxWeightGrams,
    decimal BoxHeightCm,
    decimal BoxWidthCm,
    decimal BoxLengthCm) : ICommand;

internal sealed class SaveShippingSettingsValidator : AbstractValidator<SaveShippingSettingsCommand>
{
    public SaveShippingSettingsValidator()
    {
        RuleFor(x => x.OriginPostalCode).Must(cep => PostalCode.Create(cep).IsSuccess).WithMessage("CEP inválido: informe os 8 números.");
        RuleFor(x => x.HandlingDays).InclusiveBetween(0, 30).WithMessage("O manuseio vai de 0 a 30 dias.");
        RuleFor(x => x.FallbackPacPrice).InclusiveBetween(0m, 9999m).WithMessage("Informe um valor de R$ 0 a R$ 9.999.");
        RuleFor(x => x.FallbackSedexPrice).InclusiveBetween(0m, 9999m).WithMessage("Informe um valor de R$ 0 a R$ 9.999.");
        RuleFor(x => x.BoxWeightGrams).InclusiveBetween(0, 30000).WithMessage("O peso da caixa vai até 30 kg.");
        RuleFor(x => x.BoxHeightCm).InclusiveBetween(0.1m, 200m).WithMessage("Informe de 0,1 a 200 cm.");
        RuleFor(x => x.BoxWidthCm).InclusiveBetween(0.1m, 200m).WithMessage("Informe de 0,1 a 200 cm.");
        RuleFor(x => x.BoxLengthCm).InclusiveBetween(0.1m, 200m).WithMessage("Informe de 0,1 a 200 cm.");
    }
}

internal sealed class GetShippingSettingsHandler(IDbConnectionFactory connectionFactory) : IQueryHandler<GetShippingSettingsQuery, ShippingSettingsResponse?>
{
    private const string Sql = """
        SELECT origin_postal_code, handling_days, fallback_pac_price, fallback_sedex_price,
               box_weight_grams, box_height_cm, box_width_cm, box_length_cm
          FROM shipping.settings
        """;

    public async Task<Result<ShippingSettingsResponse?>> Handle(GetShippingSettingsQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(Sql, cancellationToken: cancellationToken));
        return Result.Success(row is null
            ? null
            : new ShippingSettingsResponse(
                row.OriginPostalCode.Trim(), row.HandlingDays, row.FallbackPacPrice, row.FallbackSedexPrice, row.BoxWeightGrams, row.BoxHeightCm, row.BoxWidthCm, row.BoxLengthCm));
    }

    private sealed class Row
    {
        public string OriginPostalCode { get; init; } = string.Empty;
        public short HandlingDays { get; init; }
        public decimal FallbackPacPrice { get; init; }
        public decimal FallbackSedexPrice { get; init; }
        public int BoxWeightGrams { get; init; }
        public decimal BoxHeightCm { get; init; }
        public decimal BoxWidthCm { get; init; }
        public decimal BoxLengthCm { get; init; }
    }
}

internal sealed class SaveShippingSettingsHandler(IShippingSettingsRepository settings, TimeProvider time) : ICommandHandler<SaveShippingSettingsCommand>
{
    public async Task<Result> Handle(SaveShippingSettingsCommand command, CancellationToken cancellationToken)
    {
        var origin = PostalCode.Create(command.OriginPostalCode);
        if (origin.IsFailure)
        {
            return Result.Failure(origin.Error);
        }

        await settings.SaveAsync(
            new ShippingSettings(
                origin.Value,
                command.HandlingDays,
                command.FallbackPacPrice,
                command.FallbackSedexPrice,
                command.BoxWeightGrams,
                new PackageDimensions(command.BoxHeightCm, command.BoxWidthCm, command.BoxLengthCm)),
            time.GetUtcNow().UtcDateTime,
            cancellationToken);
        return Result.Success();
    }
}
