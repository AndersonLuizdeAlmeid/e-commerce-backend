using ECommerce.Domain.Identity;

namespace ECommerce.Domain.UnitTests.Identity;

public sealed class UserTests
{
    private static readonly DateTime Now = new(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);

    private static User NewCustomer() => User.RegisterCustomer(Email.Create("maria@loja.teste").Value, "hash", Now);

    [Fact, Trait("Spec", "RF-001-CA5")]
    public void RegisterCustomer_CreatesActiveCustomerWithUnconfirmedEmail()
    {
        var user = NewCustomer();

        user.Role.ShouldBe(Role.Customer);
        user.IsActive.ShouldBeTrue();
        user.IsEmailConfirmed.ShouldBeFalse();
        user.SecurityStamp.ShouldNotBe(Guid.Empty);
    }

    [Fact, Trait("Spec", "RN-002")]
    public void RegisterFailedSignIn_OnFifthFailure_LocksFor15MinutesAndResetsCounter()
    {
        var user = NewCustomer();

        for (var i = 0; i < 4; i++)
        {
            user.RegisterFailedSignIn(Now);
        }

        user.CanSignIn(Now).ShouldBeTrue();
        user.FailedLoginAttempts.ShouldBe(4);

        user.RegisterFailedSignIn(Now);

        user.CanSignIn(Now).ShouldBeFalse();
        user.FailedLoginAttempts.ShouldBe(0);
        user.CanSignIn(Now.AddMinutes(14)).ShouldBeFalse();
        user.CanSignIn(Now.AddMinutes(15).AddSeconds(1)).ShouldBeTrue();
    }

    [Fact]
    public void RegisterSuccessfulSignIn_ClearsFailures()
    {
        var user = NewCustomer();
        user.RegisterFailedSignIn(Now);

        user.RegisterSuccessfulSignIn(Now);

        user.FailedLoginAttempts.ShouldBe(0);
        user.LockoutEndUtc.ShouldBeNull();
    }

    [Fact, Trait("Spec", "RN-003")]
    public void ChangePassword_RotatesSecurityStampAndUnlocks()
    {
        var user = NewCustomer();
        for (var i = 0; i < 5; i++)
        {
            user.RegisterFailedSignIn(Now);
        }

        var oldStamp = user.SecurityStamp;

        user.ChangePassword("novo-hash", Now);

        user.PasswordHash.ShouldBe("novo-hash");
        user.SecurityStamp.ShouldNotBe(oldStamp);
        user.CanSignIn(Now).ShouldBeTrue();
    }

    [Fact]
    public void ConfirmEmail_IsIdempotent()
    {
        var user = NewCustomer();

        user.ConfirmEmail(Now);
        user.ConfirmEmail(Now.AddDays(1));

        user.EmailConfirmedAtUtc.ShouldBe(Now);
    }

    [Fact]
    public void CanSignIn_WhenInactive_IsFalse()
    {
        var active = NewCustomer();
        var inactive = User.Rehydrate(
            active.Id, active.Email, active.PasswordHash, active.Role, isActive: false, null, 0, null, active.SecurityStamp, 0, Now, Now);

        inactive.CanSignIn(Now).ShouldBeFalse();
    }
}
