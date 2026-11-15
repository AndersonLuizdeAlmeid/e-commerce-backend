using ECommerce.Domain.Identity;

namespace ECommerce.Domain.UnitTests.Identity;

/// <summary>RF-054: ativar, desativar, promover e rebaixar sempre trocam o security stamp (entrega 5 §3, P7).</summary>
public sealed class UserAdministrationTests
{
    private static readonly DateTime Now = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);

    private static User NewCustomer() => User.RegisterCustomer(Email.Create("maria@loja.teste").Value, "hash", Now);

    [Fact, Trait("Spec", "RF-054")]
    public void Deactivate_BlocksSignIn_AndRotatesTheStamp()
    {
        var user = NewCustomer();
        var stamp = user.SecurityStamp;

        user.SetActive(false, Now.AddMinutes(1)).ShouldBeTrue();

        user.IsActive.ShouldBeFalse();
        user.CanSignIn(Now.AddMinutes(2)).ShouldBeFalse();
        user.SecurityStamp.ShouldNotBe(stamp);
        user.UpdatedAtUtc.ShouldBe(Now.AddMinutes(1));
    }

    [Fact, Trait("Spec", "RF-054")]
    public void SameState_ChangesNothing()
    {
        var user = NewCustomer();
        var stamp = user.SecurityStamp;

        user.SetActive(true, Now).ShouldBeFalse();
        user.ChangeRole(Role.Customer, Now).ShouldBeFalse();
        user.SecurityStamp.ShouldBe(stamp);
    }

    [Fact, Trait("Spec", "RF-054")]
    public void ChangeRole_RotatesTheStamp_SoTheOldTokenStopsWorking()
    {
        var user = NewCustomer();
        var stamp = user.SecurityStamp;

        user.ChangeRole(Role.Admin, Now).ShouldBeTrue();
        user.Role.ShouldBe(Role.Admin);
        user.SecurityStamp.ShouldNotBe(stamp);

        var promoted = user.SecurityStamp;
        user.ChangeRole(Role.Customer, Now).ShouldBeTrue();
        user.SecurityStamp.ShouldNotBe(promoted);
    }
}
