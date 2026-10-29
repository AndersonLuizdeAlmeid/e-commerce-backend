using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Application.Identity.SeedInitialAdmin;

namespace ECommerce.Api.Extensions;

/// <summary>
/// RF-060: na subida da API, cria o admin a partir de <c>AdminSeed:Email</c>/<c>AdminSeed:Password</c> (variáveis
/// de ambiente) se ainda não houver nenhum admin ativo. Fica na API, e não no migrator, porque o banco também
/// pode ser criado pelos scripts do DBeaver.
/// </summary>
internal sealed partial class InitialAdminSeeder(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<InitialAdminSeeder> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var email = configuration["AdminSeed:Email"];
        var password = configuration["AdminSeed:Password"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<SeedInitialAdminCommand, bool>>();
            var result = await handler.Handle(new SeedInitialAdminCommand(email, password), cancellationToken);

            if (result.IsFailure)
            {
                LogFailed(logger, result.Error.Code, result.Error.Message);
            }
            else if (result.Value)
            {
                LogCreated(logger);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Banco fora do ar na subida não pode impedir a API de responder o /health.
            LogError(logger, ex);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Admin inicial criado (RF-060)")]
    private static partial void LogCreated(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Admin inicial não criado: {Code} {Message}")]
    private static partial void LogFailed(ILogger logger, string code, string message);

    [LoggerMessage(Level = LogLevel.Error, Message = "Erro ao criar o admin inicial")]
    private static partial void LogError(ILogger logger, Exception exception);
}
