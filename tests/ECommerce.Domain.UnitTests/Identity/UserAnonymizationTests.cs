using ECommerce.Domain.Identity;

namespace ECommerce.Domain.UnitTests.Identity;

/// <summary>Fase 6.3: exclusão da conta pela cliente (entrega 6 §3, P7).</summary>
public sealed class UserAnonymizationTests
{
    private static readonly DateTime Now = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

    private static User NewCustomer()
    {
        var user = User.RegisterCustomer(Email.Create("maria@loja.teste").Value, "hash-de-verdade", Now);
        user.ConfirmEmail(Now);
        return user;
    }

    [Fact, Trait("Spec", "ENT6-P7")]
    public void Anonymize_RemovesEmailAndPassword_AndClosesTheAccountForGood()
    {
        var user = NewCustomer();
        var stamp = user.SecurityStamp;

        user.Anonymize(Now.AddDays(1)).ShouldBeTrue();

        user.Email.Value.ShouldBe($"removida-{user.Id.Value:N}@invalid");
        user.PasswordHash.ShouldBe(User.RemovedPasswordHash);
        user.IsActive.ShouldBeFalse();
        user.IsEmailConfirmed.ShouldBeFalse();
        user.CanSignIn(Now.AddDays(2)).ShouldBeFalse();
        user.SecurityStamp.ShouldNotBe(stamp);
        user.AnonymizedAtUtc.ShouldBe(Now.AddDays(1));
        user.IsAnonymized.ShouldBeTrue();
    }

    [Fact, Trait("Spec", "ENT6-P7")]
    public void Anonymized_CannotBeReactivatedNorPromoted()
    {
        var user = NewCustomer();
        user.Anonymize(Now);
        var stamp = user.SecurityStamp;

        user.SetActive(true, Now).ShouldBeFalse();
        user.ChangeRole(Role.Admin, Now).ShouldBeFalse();

        user.IsActive.ShouldBeFalse();
        user.Role.ShouldBe(Role.Customer);
        user.SecurityStamp.ShouldBe(stamp);
    }

    [Fact, Trait("Spec", "ENT6-P7")]
    public void Anonymize_Twice_ChangesNothing()
    {
        var user = NewCustomer();
        user.Anonymize(Now);

        user.Anonymize(Now.AddDays(1)).ShouldBeFalse();
        user.AnonymizedAtUtc.ShouldBe(Now);
    }

    [Fact, Trait("Spec", "ENT6-P7")]
    public void Admin_IsNotAnonymizedThisWay() =>
        Should.Throw<InvalidOperationException>(() => User.CreateAdmin(Email.Create("admin@loja.teste").Value, "hash", Now).Anonymize(Now));
}
