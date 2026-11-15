using ECommerce.Domain.Abstractions;

namespace ECommerce.Domain.Identity;

public static class IdentityErrors
{
    public static readonly Error InvalidEmail =
        Error.Validation("Identity.InvalidEmail", "Informe um e-mail válido.");

    /// <summary>Mensagem genérica de propósito (RF-001 CA2): não confirma que o e-mail já tem conta.</summary>
    public static readonly Error EmailAlreadyInUse =
        Error.Conflict("Identity.EmailAlreadyInUse", "Não foi possível concluir o cadastro com este e-mail.");

    /// <summary>Credencial errada, conta inexistente, inativa ou bloqueada: sempre a mesma resposta (RF-002 CA2).</summary>
    public static readonly Error InvalidCredentials =
        Error.Unauthorized("Identity.InvalidCredentials", "E-mail ou senha inválidos.");

    public static readonly Error InvalidSession =
        Error.Unauthorized("Identity.InvalidSession", "Sessão expirada. Entre novamente.");

    public static readonly Error InvalidToken =
        Error.Validation("Identity.InvalidToken", "Link inválido ou expirado. Solicite um novo.");

    public static readonly Error WrongCurrentPassword =
        Error.Validation("Identity.WrongCurrentPassword", "A senha atual não confere.");

    public static readonly Error EmailAlreadyConfirmed =
        Error.BusinessRule("Identity.EmailAlreadyConfirmed", "Seu e-mail já está confirmado.");

    public static readonly Error UserNotFound =
        Error.NotFound("Identity.UserNotFound", "Usuário não encontrado.");

    public static readonly Error ConcurrencyConflict =
        Error.Conflict("Identity.ConcurrencyConflict", "Os dados foram alterados por outra operação. Tente novamente.");

    /// <summary>RF-054 CA1: ninguém se tranca fora do painel.</summary>
    public static readonly Error CannotChangeSelf =
        Error.BusinessRule("Identity.CannotChangeSelf", "Você não pode desativar nem rebaixar a sua própria conta. Peça a outra administradora.");

    /// <summary>RF-054 CA2: a loja sempre tem uma administradora ativa.</summary>
    public static readonly Error LastAdmin =
        Error.BusinessRule("Identity.LastAdmin", "Esta é a única administradora ativa. Promova outra conta antes.");
}
