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

// Papel com que a API se conecta, só com leitura e escrita de dados (entrega 6 §3, P6). Sem a configuração, nada muda:
// a API continua com o usuário do migrator, como antes.
var appRole = builder.Configuration["Database:AppRole"];
var appPassword = builder.Configuration["Database:AppPassword"];
if (!string.IsNullOrWhiteSpace(appRole) && !string.IsNullOrWhiteSpace(appPassword))
{
    DatabaseMigrator.ApplyAppRole(connectionString, appRole, appPassword);
    Console.WriteLine($"Permissões do papel '{appRole}' aplicadas.");
}

Console.WriteLine("Banco atualizado.");
return 0;
