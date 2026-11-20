using ECommerce.Application.Identity.ChangePassword;
using ECommerce.Application.Identity.Register;
using ECommerce.Domain.Identity;

namespace ECommerce.Application.UnitTests.Identity;

public sealed class IdentityValidatorTests
{
    private static RegisterCommand ValidRegister() =>
        new("Maria da Silva", "maria@loja.teste", "Joia-Preferida-123", "(11) 98765-4321", null, null);

    [Fact]
    public void Register_WithValidData_IsValid()
    {
        new RegisterValidator().Validate(ValidRegister()).IsValid.ShouldBeTrue();
    }

    // Entrega 6, S4 (ASVS V2.1): curta demais ou comum demais, mesmo com maiúscula, número e símbolo.
    [Theory, Trait("Spec", "ENT6-S4")]
    [InlineData("Ab1")]
    [InlineData("Senha@12345")]
    [InlineData("senha12345678")]
    [InlineData("Password@2026")]
    [InlineData("123456789012")]
    [InlineData("qwertyuiopas")]
    [InlineData("aaaaaaaaaaaa")]
    [InlineData("Minha-Senha-2026!")]
    [InlineData("Admin@123456789")]
    public void Register_WithWeakPassword_FailsOnPassword(string password)
    {
        var result = new RegisterValidator().Validate(ValidRegister() with { Password = password });

        result.Errors.Select(e => e.PropertyName).ShouldContain(nameof(RegisterCommand.Password));
    }

    // Sem regra de composição: frase só com minúsculas, com acento ou com espaço vale.
    [Theory, Trait("Spec", "ENT6-S4")]
    [InlineData("minha joia preferida da loja")]
    [InlineData("anéis de prata no domingo")]
    [InlineData("cafezinhoderua")]
    public void Register_WithLongPassphrase_IsValid(string password)
    {
        new RegisterValidator().Validate(ValidRegister() with { Password = password }).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Register_WithPasswordOver128Chars_Fails()
    {
        var result = new RegisterValidator().Validate(ValidRegister() with { Password = "Aa1" + new string('x', 126) });

        result.Errors.Select(e => e.PropertyName).ShouldContain(nameof(RegisterCommand.Password));
    }

    [Theory]
    [InlineData(nameof(RegisterCommand.Email), "nao-e-email")]
    [InlineData(nameof(RegisterCommand.Phone), "123")]
    [InlineData(nameof(RegisterCommand.Cpf), "123.456.789-00")]
    [InlineData(nameof(RegisterCommand.FullName), "Al")]
    public void Register_WithInvalidField_FailsOnThatField(string field, string value)
    {
        var command = field switch
        {
            nameof(RegisterCommand.Email) => ValidRegister() with { Email = value },
            nameof(RegisterCommand.Phone) => ValidRegister() with { Phone = value },
            nameof(RegisterCommand.Cpf) => ValidRegister() with { Cpf = value },
            _ => ValidRegister() with { FullName = value },
        };

        new RegisterValidator().Validate(command).Errors.Select(e => e.PropertyName).ShouldContain(field);
    }

    [Fact]
    public void ChangePassword_WhenNewEqualsCurrent_Fails()
    {
        var result = new ChangePasswordValidator().Validate(new ChangePasswordCommand(UserId.New(), "Joia-Preferida-123", "Joia-Preferida-123", null));

        result.Errors.Select(e => e.PropertyName).ShouldContain(nameof(ChangePasswordCommand.NewPassword));
    }
}
