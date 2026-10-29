using System.Data.Common;

namespace ECommerce.Application.Abstractions.Data;

/// <summary>
/// Uma conexão e uma transação por request de comando (arquivo 06 §2). Os repositórios e o Outbox usam
/// <see cref="Connection"/>/<see cref="Transaction"/>; quem abre e fecha é o decorator transacional.
/// </summary>
public interface IUnitOfWork
{
    DbConnection Connection { get; }
    DbTransaction Transaction { get; }
    bool HasActiveTransaction { get; }

    Task BeginAsync(CancellationToken cancellationToken);
    Task CommitAsync(CancellationToken cancellationToken);
    Task RollbackAsync(CancellationToken cancellationToken);
}
