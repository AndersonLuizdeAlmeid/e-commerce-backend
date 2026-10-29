using ECommerce.Domain.Abstractions;

namespace ECommerce.Application.Abstractions.Messaging;

#pragma warning disable CA1040 // interface marcadora: amarra a query ao tipo de resposta
public interface IQuery<TResponse>;
#pragma warning restore CA1040

public interface IQueryHandler<in TQuery, TResponse>
    where TQuery : IQuery<TResponse>
{
    Task<Result<TResponse>> Handle(TQuery query, CancellationToken cancellationToken);
}
