using ECommerce.Domain.Identity;

namespace ECommerce.Domain.UnitTests.Identity;

public sealed class TokenTests
{
    private static readonly DateTime Now = new(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Sliding = TimeSpan.FromDays(7);
    private static readonly TimeSpan Absolute = TimeSpan.FromDays(30);

    [Fact, Trait("Spec", "RF-003-CA1")]
    public void Rotate_RevokesCurrentAndKeepsFamily()
    {
        var first = RefreshToken.StartFamily(UserId.New(), "h1", Now, Sliding, Absolute, "127.0.0.1");

        var second = first.Rotate("h2", Now.AddDays(1), Sliding, null);

        first.IsRevoked.ShouldBeTrue();
        first.ReplacedById.ShouldBe(second.Id);
        second.FamilyId.ShouldBe(first.FamilyId);
        second.IsActive(Now.AddDays(1)).ShouldBeTrue();
        second.ExpiresAtUtc.ShouldBe(Now.AddDays(8));
    }

    [Fact, Trait("Spec", "08-ABSOLUTE")]
    public void Rotate_NeverExtendsBeyondTheFamilyAbsoluteLifetime()
    {
        var token = RefreshToken.StartFamily(UserId.New(), "h1", Now, Sliding, Absolute, null);

        var late = token.Rotate("h2", Now.AddDays(28), Sliding, null);

        late.ExpiresAtUtc.ShouldBe(Now.AddDays(30));
        late.IsActive(Now.AddDays(30).AddSeconds(1)).ShouldBeFalse();
    }

    [Fact]
    public void RefreshToken_AfterSlidingExpiration_IsNotActive()
    {
        var token = RefreshToken.StartFamily(UserId.New(), "h1", Now, Sliding, Absolute, null);

        token.IsActive(Now.AddDays(7).AddSeconds(1)).ShouldBeFalse();
    }

    [Theory, Trait("Spec", "23-TOKENS")]
    [InlineData(UserTokenPurpose.PasswordReset, 1)]
    [InlineData(UserTokenPurpose.EmailConfirmation, 24)]
    public void UserToken_ExpiresAccordingToPurpose(UserTokenPurpose purpose, int hours)
    {
        var token = UserToken.Issue(UserId.New(), purpose, "hash", Now);

        token.IsUsable(purpose, Now.AddHours(hours).AddSeconds(-1)).ShouldBeTrue();
        token.IsUsable(purpose, Now.AddHours(hours)).ShouldBeFalse();
    }

    [Fact]
    public void UserToken_IsSingleUseAndBoundToPurpose()
    {
        var token = UserToken.Issue(UserId.New(), UserTokenPurpose.PasswordReset, "hash", Now);

        token.IsUsable(UserTokenPurpose.EmailConfirmation, Now).ShouldBeFalse();

        token.MarkUsed(Now);
        token.IsUsable(UserTokenPurpose.PasswordReset, Now).ShouldBeFalse();
    }
}
