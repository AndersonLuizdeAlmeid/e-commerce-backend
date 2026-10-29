using System.Text.Json;
using ECommerce.Domain.Abstractions;
using FluentValidation;

namespace ECommerce.Application.Behaviors;

internal static class ValidationFailures
{
    /// <summary>Roda todos os validators e junta os erros por campo (camelCase), ou devolve null se a entrada é válida.</summary>
    public static async Task<ValidationError?> ValidateAsync<T>(
        IEnumerable<IValidator<T>> validators, T instance, CancellationToken cancellationToken)
    {
        var failures = new List<FluentValidation.Results.ValidationFailure>();
        foreach (var validator in validators)
        {
            var result = await validator.ValidateAsync(instance, cancellationToken);
            failures.AddRange(result.Errors);
        }

        if (failures.Count == 0)
        {
            return null;
        }

        var errors = failures
            .GroupBy(failure => JsonNamingPolicy.CamelCase.ConvertName(failure.PropertyName))
            .ToDictionary(group => group.Key, group => group.Select(failure => failure.ErrorMessage).Distinct().ToArray());

        return new ValidationError(errors);
    }
}
