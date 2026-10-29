using ECommerce.Domain.Customers;
using ECommerce.Domain.Identity;

namespace ECommerce.Domain.UnitTests.Customers;

public sealed class ValueObjectTests
{
    [Theory, Trait("Spec", "RF-001-CA4")]
    [InlineData("  Maria@Loja.COM ", "maria@loja.com")]
    [InlineData("joao.silva+loja@exemplo.com.br", "joao.silva+loja@exemplo.com.br")]
    public void Email_IsNormalized(string input, string expected)
    {
        Email.Create(input).Value.Value.ShouldBe(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("sem-arroba")]
    [InlineData("maria@localhost")]
    [InlineData("Maria <maria@loja.com>")]
    public void Email_Invalid_Fails(string input)
    {
        Email.Create(input).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Email_Masked_HidesLocalPart()
    {
        Email.Create("maria@loja.com").Value.Masked().ShouldBe("m***@loja.com");
    }

    [Theory]
    [InlineData("529.982.247-25", "52998224725")]
    [InlineData("11144477735", "11144477735")]
    public void Cpf_Valid_IsStoredAsDigits(string input, string expected)
    {
        Cpf.Create(input).Value.Value.ShouldBe(expected);
    }

    [Theory]
    [InlineData("529.982.247-26")]
    [InlineData("111.111.111-11")]
    [InlineData("1234567890")]
    [InlineData("")]
    public void Cpf_Invalid_Fails(string input)
    {
        Cpf.Create(input).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Cpf_Masked_ShowsOnlyMiddleDigits()
    {
        Cpf.Create("52998224725").Value.Masked().ShouldBe("***.982.247-**");
    }

    [Theory]
    [InlineData("(11) 98765-4321", "11987654321")]
    [InlineData("+55 21 3333-4444", "2133334444")]
    public void Phone_Valid_IsStoredAsDigits(string input, string expected)
    {
        Phone.Create(input).Value.Value.ShouldBe(expected);
    }

    [Theory]
    [InlineData("98765-4321")]
    [InlineData("(01) 98765-4321")]
    [InlineData("(11) 88765-43210")]
    public void Phone_Invalid_Fails(string input)
    {
        Phone.Create(input).IsFailure.ShouldBeTrue();
    }

    [Theory]
    [InlineData("  Maria   da  Silva ", "Maria da Silva")]
    public void Customer_NormalizesName(string input, string expected)
    {
        Customer.Create(UserId.New(), input, null, null, DateTime.UtcNow).Value.FullName.ShouldBe(expected);
    }

    [Fact]
    public void Customer_WithTooShortName_Fails()
    {
        Customer.Create(UserId.New(), "Al", null, null, DateTime.UtcNow).IsFailure.ShouldBeTrue();
    }
}
