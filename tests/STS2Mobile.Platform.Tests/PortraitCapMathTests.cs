namespace STS2Mobile.Platform.Tests;

/// <summary>
/// The portrait safe cap follows what the screen reports: the notch/island inset on the phone, and nothing at all on a
/// notchless 9:16 screen. The fixed 162-unit band is only for a host without the native query.
/// </summary>
public class PortraitCapMathTests
{
    [Theory]
    // The two reference canvases: 9:16 at 1080x1920 has no inset, 9:19.5 at 1080x2340 reports 0.069 (162 units rounded up).
    [InlineData(1920f, 0.0, 0f)]
    [InlineData(2340f, 0.069, 162f)]
    [InlineData(2340f, 0.0689, 162f)]
    [InlineData(1920f, 0.069, 133f)]
    public void WithTheNativeQueryTheCapFollowsTheInset(float canvasHeight, double inset, float expected)
    {
        Assert.Equal(expected, PortraitHeaderMath.CapHeight(hasTopInset: true, inset, canvasHeight));
    }

    [Fact]
    public void NoInsetMeansNoReservedBand()
    {
        // A notchless phone (or a window not yet reporting) reserves nothing: there is no minimum under the rule.
        Assert.Equal(0f, PortraitHeaderMath.CapHeight(hasTopInset: true, 0, 1920f));
        Assert.Equal(0f, PortraitHeaderMath.CapHeight(hasTopInset: true, 0, 2340f));
        Assert.Equal(2f, PortraitHeaderMath.CapHeight(hasTopInset: true, 0.001, 1920f));
        Assert.Equal(0f, PortraitHeaderMath.CapHeight(hasTopInset: true, double.NaN, 1920f));
    }

    [Theory]
    [InlineData(1920f)]
    [InlineData(2340f)]
    public void WithoutTheNativeQueryTheDesktopBandIsKept(float canvasHeight)
    {
        Assert.Equal(PortraitHeaderMath.DefaultCapHeight,
            PortraitHeaderMath.CapHeight(hasTopInset: false, 0, canvasHeight));
        Assert.Equal(PortraitHeaderMath.DefaultCapHeight,
            PortraitHeaderMath.CapHeight(hasTopInset: false, 0.069, canvasHeight));
        Assert.Equal(162f, PortraitHeaderMath.DefaultCapHeight);
    }
}
