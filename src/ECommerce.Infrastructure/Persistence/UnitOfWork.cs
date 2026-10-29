using System.Data.Common;
using ECommerce.Application.Abstractions.Data;
using Npgsql;

namespace ECommerce.Infrastructure.Persistence;

/// <summary>Scoped: uma conexão e uma transação por comando (arquivo 06 §2).</summary>
internal sealed class UnitOfWork(NpgsqlDataSource dataSource) : IUnitOfWork, IAsyncDisposable
{
    private NpgsqlConnection? _connection;
    private NpgsqlTransaction? _transaction;

    public DbConnection Connection =>
        _connection ?? throw new InvalidOperationException("Nenhuma transação aberta: comandos rodam dentro do decorator transacional.");

    public DbTransaction Transaction =>
        _transaction ?? throw new InvalidOperationException("Nenhuma transação aberta.");

    public bool HasActiveTransaction => _transaction is not null;

    public async Task BeginAsync(CancellationToken cancellationToken)
    {
        if (_transaction is not null)
        {
            throw new InvalidOperationException("Já existe uma transação aberta neste escopo.");
        }

        _connection = await dataSource.OpenConnectionAsync(cancellationToken);
        _transaction = await _connection.BeginTransactionAsync(cancellationToken);
    }

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        await Transaction.CommitAsync(cancellationToken);
        await CloseAsync();
    }

    public async Task RollbackAsync(CancellationToken cancellationToken)
    {
        if (_transaction is not null)
        {
            await _transaction.RollbackAsync(cancellationToken);
        }

        await CloseAsync();
    }

    public async ValueTask DisposeAsync() => await CloseAsync();

    private async Task CloseAsync()
    {
        if (_transaction is not null)
        {
            await _transaction.DisposeAsync();
            _transaction = null;
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }
    }
}
