using ECommerce.Application.Abstractions.Data;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Domain.Abstractions;
using FluentValidation;

namespace ECommerce.Application.Behaviors;

// Pipeline de comandos (arquivo 03 §4): Validação → Transação → Handler.
// A validação fica por fora para não abrir transação com entrada inválida.

internal sealed class ValidatingCommandHandler<TCommand>(
    ICommandHandler<TCommand> inner,
    IEnumerable<IValidator<TCommand>> validators) : ICommandHandler<TCommand>
    where TCommand : ICommand
{
    public async Task<Result> Handle(TCommand command, CancellationToken cancellationToken)
    {
        var error = await ValidationFailures.ValidateAsync(validators, command, cancellationToken);
        return error is null ? await inner.Handle(command, cancellationToken) : Result.Failure(error);
    }
}

internal sealed class ValidatingCommandHandler<TCommand, TResponse>(
    ICommandHandler<TCommand, TResponse> inner,
    IEnumerable<IValidator<TCommand>> validators) : ICommandHandler<TCommand, TResponse>
    where TCommand : ICommand<TResponse>
{
    public async Task<Result<TResponse>> Handle(TCommand command, CancellationToken cancellationToken)
    {
        var error = await ValidationFailures.ValidateAsync(validators, command, cancellationToken);
        return error is null ? await inner.Handle(command, cancellationToken) : error;
    }
}

/// <summary>Abre a transação, faz commit no sucesso e rollback na falha ou exceção (arquivo 06 §2).</summary>
internal sealed class TransactionalCommandHandler<TCommand>(ICommandHandler<TCommand> inner, IUnitOfWork unitOfWork)
    : ICommandHandler<TCommand>
    where TCommand : ICommand
{
    public Task<Result> Handle(TCommand command, CancellationToken cancellationToken) =>
        Transaction.RunAsync(unitOfWork, command is IPersistOnFailure, r => r.IsSuccess, ct => inner.Handle(command, ct), cancellationToken);
}

internal sealed class TransactionalCommandHandler<TCommand, TResponse>(
    ICommandHandler<TCommand, TResponse> inner, IUnitOfWork unitOfWork) : ICommandHandler<TCommand, TResponse>
    where TCommand : ICommand<TResponse>
{
    public Task<Result<TResponse>> Handle(TCommand command, CancellationToken cancellationToken) =>
        Transaction.RunAsync(unitOfWork, command is IPersistOnFailure, r => r.IsSuccess, ct => inner.Handle(command, ct), cancellationToken);
}

internal static class Transaction
{
    public static async Task<TResult> RunAsync<TResult>(
        IUnitOfWork unitOfWork,
        bool persistOnFailure,
        Func<TResult, bool> isSuccess,
        Func<CancellationToken, Task<TResult>> handle,
        CancellationToken cancellationToken)
    {
        await unitOfWork.BeginAsync(cancellationToken);
        try
        {
            var result = await handle(cancellationToken);

            if (isSuccess(result) || persistOnFailure)
            {
                await unitOfWork.CommitAsync(cancellationToken);
            }
            else
            {
                await unitOfWork.RollbackAsync(cancellationToken);
            }

            return result;
        }
        catch
        {
            await unitOfWork.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
}
