using ECommerce.Domain.Abstractions;

namespace ECommerce.Application.Abstractions.Messaging;

#pragma warning disable CA1040 // interfaces marcadoras
/// <summary>Comando sem valor de retorno (arquivo 03, ADR-004). Roda dentro de transação.</summary>
public interface ICommand;

/// <summary>Comando que devolve <typeparamref name="TResponse"/> em caso de sucesso. Roda dentro de transação.</summary>
public interface ICommand<TResponse>;

/// <summary>
/// O comando grava o que fez mesmo quando devolve falha: login errado conta a tentativa (RN-002) e o reuso de
/// refresh token revoga a família (RF-003 CA2). Sem isso o decorator transacional desfaria a gravação.
/// </summary>
public interface IPersistOnFailure;
#pragma warning restore CA1040

public interface ICommandHandler<in TCommand>
    where TCommand : ICommand
{
    Task<Result> Handle(TCommand command, CancellationToken cancellationToken);
}

public interface ICommandHandler<in TCommand, TResponse>
    where TCommand : ICommand<TResponse>
{
    Task<Result<TResponse>> Handle(TCommand command, CancellationToken cancellationToken);
}
