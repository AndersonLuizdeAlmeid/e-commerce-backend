using Dapper;
using ECommerce.Application.Abstractions.Data;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Domain.Abstractions;
using ECommerce.Domain.Shipping;
using FluentValidation;

namespace ECommerce.Application.Shipping;

// RF-058: regras comerciais sobre a cotação. A cotação lê as regras a cada chamada, então valem na hora.

public sealed record PostalCodeRangeDto(string From, string To);

public sealed record ShippingRuleResponse(
    Guid Id,
    string Name,
    int Priority,
    bool IsActive,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    decimal? MinSubtotal,
    IReadOnlyList<string> States,
    IReadOnlyList<PostalCodeRangeDto> PostalCodeRanges,
    IReadOnlyList<string> ServiceCodes,
    string Effect,
    decimal? EffectValue);

public sealed record ShippingRuleInput(
    string Name,
    int Priority,
    bool IsActive,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    decimal? MinSubtotal,
    IReadOnlyList<string> States,
    IReadOnlyList<PostalCodeRangeDto> PostalCodeRanges,
    IReadOnlyList<string> ServiceCodes,
    string Effect,
    decimal? EffectValue);

public sealed record ListShippingRulesQuery : IQuery<IReadOnlyList<ShippingRuleResponse>>;

public sealed record CreateShippingRuleCommand(ShippingRuleInput Input) : ICommand<Guid>;

public sealed record UpdateShippingRuleCommand(Guid Id, ShippingRuleInput Input) : ICommand;

public sealed record DeleteShippingRuleCommand(Guid Id) : ICommand;

internal static class ShippingRuleInputs
{
    public static Result<ShippingRule> ToRule(Guid id, ShippingRuleInput input)
    {
        if (!Enum.TryParse<ShippingRuleEffect>(input.Effect, ignoreCase: false, out var effect) || input.Effect != effect.ToString())
        {
            return ShippingRuleErrors.InvalidEffectValue;
        }

        var ranges = new List<PostalCodeRange>();
        foreach (var range in input.PostalCodeRanges)
        {
            var created = PostalCodeRange.Create(range.From, range.To);
            if (created.IsFailure)
            {
                return created.Error;
            }

            ranges.Add(created.Value);
        }

        return ShippingRule.Create(id, new ShippingRuleContent(
            input.Name,
            input.Priority,
            input.IsActive,
            input.ValidFrom,
            input.ValidTo,
            input.MinSubtotal,
            input.States.Select(state => state.Trim().ToUpperInvariant()).Distinct().ToList(),
            ranges,
            input.ServiceCodes.Distinct().ToList(),
            effect,
            input.EffectValue));
    }
}

internal sealed class CreateShippingRuleValidator : AbstractValidator<CreateShippingRuleCommand>
{
    public CreateShippingRuleValidator()
    {
        RuleFor(x => x.Input.Priority).InclusiveBetween(0, 100_000).WithMessage("A prioridade vai de 0 a 100000.");
        RuleFor(x => x.Input.PostalCodeRanges).Must(ranges => ranges.Count <= 50).WithMessage("Informe até 50 faixas de CEP.");
    }
}

internal sealed class UpdateShippingRuleValidator : AbstractValidator<UpdateShippingRuleCommand>
{
    public UpdateShippingRuleValidator()
    {
        RuleFor(x => x.Input.Priority).InclusiveBetween(0, 100_000).WithMessage("A prioridade vai de 0 a 100000.");
        RuleFor(x => x.Input.PostalCodeRanges).Must(ranges => ranges.Count <= 50).WithMessage("Informe até 50 faixas de CEP.");
    }
}

internal sealed class ShippingRuleRow
{
    public const string Columns = "id, name, priority, is_active, valid_from, valid_to, min_subtotal, states, postal_code_ranges, service_codes, effect, effect_value";

    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public int Priority { get; init; }
    public bool IsActive { get; init; }
    public DateTime? ValidFrom { get; init; }
    public DateTime? ValidTo { get; init; }
    public decimal? MinSubtotal { get; init; }
    public string[] States { get; init; } = [];
    public string[] PostalCodeRanges { get; init; } = [];
    public string[] ServiceCodes { get; init; } = [];
    public string Effect { get; init; } = string.Empty;
    public decimal? EffectValue { get; init; }

    public DateOnly? ValidFromDate => ValidFrom is { } date ? DateOnly.FromDateTime(date) : null;

    public DateOnly? ValidToDate => ValidTo is { } date ? DateOnly.FromDateTime(date) : null;

    public ShippingRule ToRule() => ShippingRule.Rehydrate(Id, new ShippingRuleContent(
        Name,
        Priority,
        IsActive,
        ValidFromDate,
        ValidToDate,
        MinSubtotal,
        States,
        PostalCodeRanges.Select(PostalCodeRange.Rehydrate).ToList(),
        ServiceCodes,
        Enum.Parse<ShippingRuleEffect>(Effect),
        EffectValue));
}

internal sealed class ListShippingRulesHandler(IDbConnectionFactory connectionFactory) : IQueryHandler<ListShippingRulesQuery, IReadOnlyList<ShippingRuleResponse>>
{
    private const string Sql = $"SELECT {ShippingRuleRow.Columns} FROM shipping.rules ORDER BY priority, name, id";

    public async Task<Result<IReadOnlyList<ShippingRuleResponse>>> Handle(ListShippingRulesQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<ShippingRuleRow>(new CommandDefinition(Sql, cancellationToken: cancellationToken));
        IReadOnlyList<ShippingRuleResponse> rules = rows
            .Select(row => new ShippingRuleResponse(
                row.Id, row.Name, row.Priority, row.IsActive, row.ValidFromDate, row.ValidToDate, row.MinSubtotal, row.States,
                row.PostalCodeRanges.Select(PostalCodeRange.Rehydrate).Select(range => new PostalCodeRangeDto(range.From, range.To)).ToList(),
                row.ServiceCodes, row.Effect, row.EffectValue))
            .ToList();
        return Result.Success(rules);
    }
}

internal sealed class CreateShippingRuleHandler(IShippingRuleRepository rules, TimeProvider time) : ICommandHandler<CreateShippingRuleCommand, Guid>
{
    public async Task<Result<Guid>> Handle(CreateShippingRuleCommand command, CancellationToken cancellationToken)
    {
        var rule = ShippingRuleInputs.ToRule(Guid.CreateVersion7(), command.Input);
        if (rule.IsFailure)
        {
            return rule.Error;
        }

        await rules.AddAsync(rule.Value, time.GetUtcNow().UtcDateTime, cancellationToken);
        return rule.Value.Id;
    }
}

internal sealed class UpdateShippingRuleHandler(IShippingRuleRepository rules, TimeProvider time) : ICommandHandler<UpdateShippingRuleCommand>
{
    public async Task<Result> Handle(UpdateShippingRuleCommand command, CancellationToken cancellationToken)
    {
        var rule = ShippingRuleInputs.ToRule(command.Id, command.Input);
        if (rule.IsFailure)
        {
            return Result.Failure(rule.Error);
        }

        return await rules.UpdateAsync(rule.Value, time.GetUtcNow().UtcDateTime, cancellationToken)
            ? Result.Success()
            : Result.Failure(ShippingRuleErrors.NotFound);
    }
}

internal sealed class DeleteShippingRuleHandler(IShippingRuleRepository rules) : ICommandHandler<DeleteShippingRuleCommand>
{
    public async Task<Result> Handle(DeleteShippingRuleCommand command, CancellationToken cancellationToken) =>
        await rules.DeleteAsync(command.Id, cancellationToken) ? Result.Success() : Result.Failure(ShippingRuleErrors.NotFound);
}
