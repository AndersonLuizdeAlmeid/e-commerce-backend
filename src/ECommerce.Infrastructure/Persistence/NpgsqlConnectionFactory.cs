using System.Data.Common;
using ECommerce.Application.Abstractions.Data;
using Npgsql;

namespace ECommerce.Infrastructure.Persistence;

internal sealed class NpgsqlConnectionFactory(NpgsqlDataSource dataSource) : IDbConnectionFactory
{
    public async ValueTask<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken) =>
        await dataSource.OpenConnectionAsync(cancellationToken);
}
