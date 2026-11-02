using ECommerce.Domain.Shipping;

namespace ECommerce.Domain.UnitTests.Shipping;

public sealed class ShippingDomainTests
{
    private static readonly PackageDimensions Box = new(4m, 12m, 16m);

    private static PackageItem Ring(int quantity = 1) => new(20, new PackageDimensions(3m, 5m, 5m), quantity);

    private static PackageItem Bag(int quantity = 1) => new(600, new PackageDimensions(10m, 30m, 40m), quantity);

    [Theory, Trait("Spec", "RF-032-CA1")]
    [InlineData("01310-100", "01310100")]
    [InlineData("01310100", "01310100")]
    [InlineData(" 01310100 ", "01310100")]
    public void PostalCode_AcceptsWithOrWithoutHyphen(string value, string expected)
    {
        PostalCode.Create(value).Value.Value.ShouldBe(expected);
    }

    [Theory, Trait("Spec", "RF-032-CA1")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0131010")]
    [InlineData("013101000")]
    [InlineData("0131010a")]
    public void PostalCode_Invalid_IsRefused(string? value)
    {
        PostalCode.Create(value).Error.ShouldBe(ShippingErrors.InvalidPostalCode);
    }

    [Fact, Trait("Spec", "22-PACKAGE")]
    public void Package_PiecesThatFit_KeepTheBoxAndSumTheWeights()
    {
        var package = PackagingService.Build(100, Box, [Ring(2)]);

        package.WeightGrams.ShouldBe(140);
        package.Dimensions.ShouldBe(Box);
    }

    [Fact, Trait("Spec", "22-PACKAGE")]
    public void Package_PieceThatOnlyFitsTurned_StillKeepsTheBox()
    {
        var turned = new PackageItem(50, new PackageDimensions(15m, 3m, 10m), 1);

        PackagingService.Build(0, Box, [turned]).Dimensions.ShouldBe(Box);
    }

    [Fact, Trait("Spec", "22-PACKAGE")]
    public void Package_PieceBiggerThanTheBox_GrowsThePackage()
    {
        var package = PackagingService.Build(100, Box, [Bag()]);

        package.WeightGrams.ShouldBe(700);
        package.Dimensions.ShouldBe(new PackageDimensions(10m, 30m, 40m));
    }

    [Fact, Trait("Spec", "22-PACKAGE")]
    public void Package_BagAndRing_AddsTheBoxHeightToTheBag()
    {
        var package = PackagingService.Build(100, Box, [Bag(), Ring()]);

        package.WeightGrams.ShouldBe(720);
        package.Dimensions.ShouldBe(new PackageDimensions(14m, 30m, 40m));
    }

    [Fact, Trait("Spec", "22-PACKAGE")]
    public void Package_TwoBags_StackTheHeight()
    {
        var package = PackagingService.Build(0, Box, [Bag(2)]);

        package.WeightGrams.ShouldBe(1200);
        package.Dimensions.ShouldBe(new PackageDimensions(20m, 30m, 40m));
    }
}
