using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Application.Inventory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ECommerce.Infrastructure.Jobs;

/// <summary>
/// A cada 5 minutos: EM-11, que só sai uma vez por dia depois das 8h de São Paulo (entrega 6 §3, P4), e o vigia de
/// operação com o EM-14 (P3). A primeira rodada é na subida.
/// </summary>
internal sealed partial class OperationsJobs(
    IServiceScopeFactory scopes,
    OpsMonitor monitor,
    IOptions<OpsSettings> options,
    TimeProvider time,
    ILogger<OperationsJobs> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.ScheduledJobsEnabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            await RunOnceAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var digest = scope.ServiceProvider.GetRequiredService<ICommandHandler<SendLowStockDigestCommand, int>>();
            var sent = await digest.Handle(new SendLowStockDigestCommand(), cancellationToken);
            if (sent.IsFailure)
            {
                LogFailed(logger, "estoque baixo", sent.Error.Code);
            }
            else if (sent.Value > 0)
            {
                LogDigest(logger, sent.Value);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogCrashed(logger, "estoque baixo", ex);
        }

        try
        {
            await monitor.CheckAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogCrashed(logger, "vigia de operação", ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Resumo de estoque baixo enviado com {Count} variação(ões)")]
    private static partial void LogDigest(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Job de operação ({Job}) falhou: {Code}")]
    private static partial void LogFailed(ILogger logger, string job, string code);

    [LoggerMessage(Level = LogLevel.Error, Message = "Falha no job de operação ({Job})")]
    private static partial void LogCrashed(ILogger logger, string job, Exception exception);
}
