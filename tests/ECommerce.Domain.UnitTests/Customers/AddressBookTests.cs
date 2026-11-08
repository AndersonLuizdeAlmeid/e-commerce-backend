using ECommerce.Domain.Customers;
using ECommerce.Domain.Identity;

namespace ECommerce.Domain.UnitTests.Customers;

public sealed class AddressBookTests
{
    private static readonly DateTime Now = new(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);

    private static AddressContent Content(string street = "Rua Augusta", string postalCode = "01305-000", string state = "SP") =>
        AddressContent.Create("Maria da Silva", postalCode, street, "100", "Apto 12", "Consolação", "São Paulo", state).Value;

    private static AddressBook EmptyBook() => AddressBook.Rehydrate(new UserId(Guid.CreateVersion7()), []);

    [Fact, Trait("Spec", "RF-007")]
    public void FirstAddress_IsTheDefault_EvenWhenNotAsked()
    {
        var book = EmptyBook();

        var first = book.Add(Content(), makeDefault: false, Now).Value;
        var second = book.Add(Content("Rua Oscar Freire"), makeDefault: false, Now.AddMinutes(1)).Value;

        first.IsDefault.ShouldBeTrue();
        second.IsDefault.ShouldBeFalse();
    }

    [Fact, Trait("Spec", "RF-007")]
    public void NewDefault_TakesThePlaceOfTheOldOne()
    {
        var book = EmptyBook();
        var first = book.Add(Content(), makeDefault: false, Now).Value;

        var second = book.Add(Content("Rua Oscar Freire"), makeDefault: true, Now.AddMinutes(1)).Value;

        second.IsDefault.ShouldBeTrue();
        first.IsDefault.ShouldBeFalse();
        book.Addresses.Count(address => address.IsDefault).ShouldBe(1);
    }

    [Fact, Trait("Spec", "RF-007")]
    public void SetDefault_LeavesExactlyOneDefault()
    {
        var book = EmptyBook();
        book.Add(Content(), makeDefault: false, Now);
        var second = book.Add(Content("Rua Oscar Freire"), makeDefault: false, Now.AddMinutes(1)).Value;

        book.SetDefault(second.Id).IsSuccess.ShouldBeTrue();

        book.Addresses.Single(address => address.IsDefault).Id.ShouldBe(second.Id);
    }

    [Fact, Trait("Spec", "RF-007")]
    public void RemovingTheDefault_PassesItToTheNewestAddress()
    {
        var book = EmptyBook();
        var first = book.Add(Content(), makeDefault: false, Now).Value;
        book.Add(Content("Rua Oscar Freire"), makeDefault: false, Now.AddMinutes(1));
        var newest = book.Add(Content("Rua da Consolação"), makeDefault: false, Now.AddMinutes(2)).Value;

        book.Remove(first.Id).IsSuccess.ShouldBeTrue();

        book.Addresses.Count.ShouldBe(2);
        book.Addresses.Single(address => address.IsDefault).Id.ShouldBe(newest.Id);
    }

    [Fact, Trait("Spec", "RF-007")]
    public void RemovingTheLastAddress_LeavesTheBookEmpty()
    {
        var book = EmptyBook();
        var only = book.Add(Content(), makeDefault: false, Now).Value;

        book.Remove(only.Id).IsSuccess.ShouldBeTrue();

        book.Addresses.ShouldBeEmpty();
    }

    [Fact, Trait("Spec", "RF-007")]
    public void Book_IsLimitedToTenAddresses()
    {
        var book = EmptyBook();
        for (var i = 0; i < AddressBook.MaxAddresses; i++)
        {
            book.Add(Content(), makeDefault: false, Now.AddMinutes(i)).IsSuccess.ShouldBeTrue();
        }

        book.Add(Content(), makeDefault: false, Now).Error.ShouldBe(AddressErrors.TooManyAddresses);
    }

    [Fact, Trait("Spec", "RF-007-CA1")]
    public void UnknownAddress_IsNotFound()
    {
        var book = EmptyBook();
        book.Add(Content(), makeDefault: false, Now);
        var unknown = Guid.CreateVersion7();

        book.Update(unknown, Content(), Now).Error.ShouldBe(AddressErrors.AddressNotFound);
        book.SetDefault(unknown).Error.ShouldBe(AddressErrors.AddressNotFound);
        book.Remove(unknown).Error.ShouldBe(AddressErrors.AddressNotFound);
    }

    [Fact]
    public void Content_IsCleaned()
    {
        var content = AddressContent.Create("  Maria   da Silva ", "01305000", " Rua  Augusta ", " 100 ", "   ", "Consolação", "São Paulo", " sp ").Value;

        content.Recipient.ShouldBe("Maria da Silva");
        content.PostalCode.Value.ShouldBe("01305000");
        content.Street.ShouldBe("Rua Augusta");
        content.Number.ShouldBe("100");
        content.Complement.ShouldBeNull();
        content.State.ShouldBe("SP");
    }

    [Fact, Trait("Spec", "RF-007-CA2")]
    public void Content_WithAStateThatDoesNotMatchThePostalCode_Fails()
    {
        AddressContent.Create("Maria da Silva", "01305-000", "Rua Augusta", "100", null, "Consolação", "São Paulo", "MG")
            .Error.ShouldBe(AddressErrors.StateDoesNotMatchPostalCode);
    }

    [Theory]
    [InlineData("Ma", "01305000", "Rua", "1", "Bairro", "Cidade", "SP")]
    [InlineData("Maria", "0130500", "Rua", "1", "Bairro", "Cidade", "SP")]
    [InlineData("Maria", "01305000", "", "1", "Bairro", "Cidade", "SP")]
    [InlineData("Maria", "01305000", "Rua", "", "Bairro", "Cidade", "SP")]
    [InlineData("Maria", "01305000", "Rua", "1", "", "Cidade", "SP")]
    [InlineData("Maria", "01305000", "Rua", "1", "Bairro", "", "SP")]
    [InlineData("Maria", "01305000", "Rua", "1", "Bairro", "Cidade", "XX")]
    public void Content_WithAMissingOrInvalidField_Fails(
        string recipient, string postalCode, string street, string number, string district, string city, string state)
    {
        AddressContent.Create(recipient, postalCode, street, number, null, district, city, state).IsFailure.ShouldBeTrue();
    }
}
