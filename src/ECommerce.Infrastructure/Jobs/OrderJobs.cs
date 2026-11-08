using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Application.Ordering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ECommerce.Infrastructure.Jobs;

/// <summary>
/// Jobs do pedido (arquivo 25 §2): cancelamento em 48 h e lembrete de 24 h, a cada 5 minutos. Cada rodada pega até
/// 50 pedidos com <c>FOR UPDATE SKIP LOCKED</c>, então duas instâncias não cancelam o mesmo pedido; se sobrou,
/// a próxima rodada continua.
/// </summary>
internal sealed partial class OrderJobs(IServiceScopeFactory scopes, TimeProvider time, ILogger<OrderJobs> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
            var cancel = scope.ServiceProvider.GetRequiredService<ICommandHandler<CancelOverdueOrdersCommand, int>>();
            var cancelled = await cancel.Handle(new CancelOverdueOrdersCommand(), cancellationToken);
            if (cancelled.IsFailure)
            {
                LogFailed(logger, "cancelamento em 48 h", cancelled.Error.Code);
            }
            else if (cancelled.Value > 0)
            {
                LogCancelled(logger, cancelled.Value);
            }

            var remind = scope.ServiceProvider.GetRequiredService<ICommandHandler<SendPaymentRemindersCommand, int>>();
            var reminded = await remind.Handle(new SendPaymentRemindersCommand(), cancellationToken);
            if (reminded.IsFailure)
            {
                LogFailed(logger, "lembrete de pagamento", reminded.Error.Code);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogCrashed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Count} pedido(s) cancelado(s) por falta de pagamento")]
    private static partial void LogCancelled(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Job de pedidos ({Job}) falhou: {Code}")]
    private static partial void LogFailed(ILogger logger, string job, string code);

    [LoggerMessage(Level = LogLevel.Error, Message = "Falha no job de pedidos")]
    private static partial void LogCrashed(ILogger logger, Exception exception);
}
