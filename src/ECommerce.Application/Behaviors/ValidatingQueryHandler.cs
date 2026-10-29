using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Domain.Abstractions;
using FluentValidation;

namespace ECommerce.Application.Behaviors;

/// <summary>
/// Decorator: roda os validators da query antes do handler e devolve <see cref="ValidationError"/> (400) sem
/// chamar o handler quando a entrada é inválida (arquivo 13 §1).
/// </summary>
internal sealed class ValidatingQueryHandler<TQuery, TResponse>(
    IQueryHandler<TQuery, TResponse> inner,
    IEnumerable<IValidator<TQuery>> validators) : IQueryHandler<TQuery, TResponse>
    where TQuery : IQuery<TResponse>
{
    public async Task<Result<TResponse>> Handle(TQuery query, CancellationToken cancellationToken)
    {
        var error = await ValidationFailures.ValidateAsync(validators, query, cancellationToken);
        return error is null ? await inner.Handle(query, cancellationToken) : error;
    }
}
