using System.Text;
using ECommerce.Domain.Abstractions;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ECommerce.Api.Extensions;

/// <summary>Result → HTTP (arquivo 13 §3).</summary>
internal static class ResultExtensions
{
    public static Results<Ok<T>, ProblemHttpResult> ToHttpResult<T>(this Result<T> result) =>
        result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();

    public static ProblemHttpResult ToProblem(
        this Error error, int? statusCode = null, IEnumerable<KeyValuePair<string, object?>>? extensions = null)
    {
        var status = statusCode ?? error.Type switch
        {
            ErrorType.Validation => StatusCodes.Status400BadRequest,
            ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
            ErrorType.Forbidden => StatusCodes.Status403Forbidden,
            ErrorType.NotFound => StatusCodes.Status404NotFound,
            ErrorType.Conflict => StatusCodes.Status409Conflict,
            ErrorType.BusinessRule => StatusCodes.Status422UnprocessableEntity,
            _ => StatusCodes.Status500InternalServerError,
        };

        var problemExtensions = new Dictionary<string, object?> { ["code"] = error.Code };
        if (error is ValidationError validation)
        {
            problemExtensions["errors"] = validation.Errors;
        }

        foreach (var (key, value) in extensions ?? [])
        {
            problemExtensions[key] = value;
        }

        return TypedResults.Problem(
            title: error.Message,
            statusCode: status,
            type: $"/errors/{ToKebabCase(error.Code)}",
            extensions: problemExtensions);
    }

    // "Catalog.CategoryNotFound" → "catalog.category-not-found"
    private static string ToKebabCase(string code)
    {
        var builder = new StringBuilder(code.Length + 8);
        for (var i = 0; i < code.Length; i++)
        {
            var c = code[i];
            if (char.IsUpper(c) && i > 0 && code[i - 1] != '.')
            {
                builder.Append('-');
            }

            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }
}
