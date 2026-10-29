using ECommerce.Domain.Abstractions;

namespace ECommerce.Domain.Customers;

public static class CustomerErrors
{
    public static readonly Error InvalidCpf = Error.Validation("Customers.InvalidCpf", "CPF inválido.");

    public static readonly Error InvalidPhone =
        Error.Validation("Customers.InvalidPhone", "Telefone inválido. Informe DDD + número.");

    public static readonly Error InvalidFullName =
        Error.Validation("Customers.InvalidFullName", "Informe o nome completo (3 a 120 caracteres).");

    public static readonly Error CpfAlreadyInUse =
        Error.Conflict("Customers.CpfAlreadyInUse", "Este CPF já está cadastrado em outra conta.");

    public static readonly Error CustomerNotFound =
        Error.NotFound("Customers.CustomerNotFound", "Cliente não encontrado.");
}
