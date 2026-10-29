namespace ECommerce.Domain.Abstractions;

/// <summary>Igualdade por identidade (arquivo 04 §2).</summary>
public abstract class Entity<TId>
    where TId : notnull
{
    public TId Id { get; protected init; } = default!;

    public override bool Equals(object? obj) =>
        obj is Entity<TId> other && other.GetType() == GetType() && EqualityComparer<TId>.Default.Equals(Id, other.Id);

    public override int GetHashCode() => EqualityComparer<TId>.Default.GetHashCode(Id);
}

/// <summary>
/// Raiz de agregado com versão para concorrência otimista (arquivo 06 §6.1). Os domain events entram quando houver
/// o primeiro efeito que precise deles fora do próprio caso de uso (revalidação de páginas, Fase 2B).
/// </summary>
public abstract class AggregateRoot<TId> : Entity<TId>
    where TId : notnull
{
    public int Version { get; protected set; }
}
