using ECommerce.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

// Executado antes do deploy (arquivo 05 §4). Em Development também aplica o catálogo de exemplo.
var builder = Host.CreateApplicationBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Database");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("ConnectionStrings:Database não configurada.");
    return 1;
}

var result = DatabaseMigrator.Migrate(connectionString, includeDevelopmentSeed: builder.Environment.IsDevelopment());

if (!result.Successful)
{
    Console.Error.WriteLine(result.Error);
    return 1;
}

Console.WriteLine("Banco atualizado.");
return 0;
