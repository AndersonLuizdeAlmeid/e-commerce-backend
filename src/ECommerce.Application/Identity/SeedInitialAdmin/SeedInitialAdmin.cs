using ECommerce.Application.Abstractions.Authentication;
using ECommerce.Application.Abstractions.Messaging;
using ECommerce.Domain.Abstractions;
using ECommerce.Domain.Identity;
using FluentValidation;

namespace ECommerce.Application.Identity.SeedInitialAdmin;

/// <summary>
/// RF-060 — cria o admin inicial a partir da configuração quando não existe nenhum admin ativo.
/// Devolve <c>true</c> se criou e <c>false</c> se já havia admin (não faz nada).
/// </summary>
public sealed record SeedInitialAdminCommand(string Email, string Password) : ICommand<bool>;

internal sealed class SeedInitialAdminValidator : AbstractValidator<SeedInitialAdminCommand>
{
    public SeedInitialAdminValidator()
    {
        RuleFor(x => x.Email).Must(email => Domain.Identity.Email.Create(email).IsSuccess).WithMessage(IdentityErrors.InvalidEmail.Message);
        RuleFor(x => x.Password).StrongPassword();
    }
}

internal sealed class SeedInitialAdminHandler(
    IUserRepository users,
    IPasswordHasher passwordHasher,
    TimeProvider time) : ICommandHandler<SeedInitialAdminCommand, bool>
{
    public async Task<Result<bool>> Handle(SeedInitialAdminCommand command, CancellationToken cancellationToken)
    {
        if (await users.AnyActiveAdminAsync(cancellationToken))
        {
            return false;
        }

        var email = Email.Create(command.Email);
        if (email.IsFailure)
        {
            return email.Error;
        }

        // Q06: admin não compra com a mesma conta, então o e-mail não pode ser de um cliente existente.
        if (await users.ExistsByEmailAsync(email.Value, cancellationToken))
        {
            return IdentityErrors.EmailAlreadyInUse;
        }

        var admin = User.CreateAdmin(email.Value, passwordHasher.Hash(command.Password), time.GetUtcNow().UtcDateTime);
        return await users.TryAddAsync(admin, cancellationToken) ? true : IdentityErrors.EmailAlreadyInUse;
    }
}
