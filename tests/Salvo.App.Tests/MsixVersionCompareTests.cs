using Salvo.App.Services;

namespace Salvo.App.Tests;

/// <summary>
/// Covers MsixUpdateService's update-availability comparison. The headline
/// case is a 4-part GitHub release tag equalling the 3-part installed
/// version: before the fix that compared as "newer" and produced a
/// perpetual, unclearable "update available".
/// </summary>
public sealed class MsixVersionCompareTests
{
    [Fact]
    public void IsNewer_FourPartTagEqualToInstalled_IsNotNewer()
    {
        // The regression: release tag "0.2.14.0" vs installed "0.2.14".
        MsixUpdateService.IsNewer("0.2.14.0", "0.2.14").Should().BeFalse();
    }

    [Theory]
    [InlineData("0.2.15.0", "0.2.14")]  // higher build, 4-part tag
    [InlineData("0.3.0", "0.2.14")]     // higher minor
    [InlineData("1.0.0", "0.9.9")]      // higher major
    [InlineData("0.3", "0.2.14")]       // 2-part tag normalises to 0.3.0
    [InlineData("0.3.0-canary.5", "0.2.14")] // prerelease suffix stripped
    public void IsNewer_ReturnsTrue_WhenLatestExceedsInstalled(string latest, string current)
    {
        MsixUpdateService.IsNewer(latest, current).Should().BeTrue();
    }

    [Theory]
    [InlineData("0.2.14", "0.2.14")]    // exact equal
    [InlineData("0.2.13.0", "0.2.14")]  // older build, 4-part tag
    [InlineData("0.1.99", "0.2.0")]     // older minor
    public void IsNewer_ReturnsFalse_WhenLatestNotAheadOfInstalled(string latest, string current)
    {
        MsixUpdateService.IsNewer(latest, current).Should().BeFalse();
    }

    [Theory]
    [InlineData("not-a-version", "0.2.14")]
    [InlineData("0.2.14", "garbage")]
    public void IsNewer_ReturnsFalse_WhenEitherOperandIsUnparseable(string latest, string current)
    {
        MsixUpdateService.IsNewer(latest, current).Should().BeFalse();
    }
}
