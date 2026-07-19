using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Salvo.Core.Launch;
using Salvo.Core.Launch.Probes;
using Salvo.Core.Models;
using Salvo.Core.Services;

namespace Salvo.Core.Tests;

/// <summary>
/// Pins the launch-window-style contract: the model value maps onto the
/// ProcessStartInfo show hint, and a Hidden launch opts out of the
/// main-window probe (no visible window can ever satisfy it — readiness
/// must fall to the non-window probes instead of riding out the timeout).
/// </summary>
public sealed class LaunchWindowStyleTests
{
    [Theory]
    [InlineData(LaunchWindowStyle.Normal, ProcessWindowStyle.Normal)]
    [InlineData(LaunchWindowStyle.Minimized, ProcessWindowStyle.Minimized)]
    [InlineData(LaunchWindowStyle.Maximized, ProcessWindowStyle.Maximized)]
    [InlineData(LaunchWindowStyle.Hidden, ProcessWindowStyle.Hidden)]
    public void MapWindowStyle_CoversEveryStyle(LaunchWindowStyle style, ProcessWindowStyle expected)
    {
        ProcessLauncher.MapWindowStyle(style).Should().Be(expected);
    }

    [Fact]
    public void MainWindowProbe_DoesNotApply_ToHiddenLaunches()
    {
        var probe = new MainWindowProbe();

        probe.AppliesTo(MakeCtx(LaunchWindowStyle.Normal)).Should().BeTrue();
        probe.AppliesTo(MakeCtx(LaunchWindowStyle.Minimized)).Should().BeTrue();
        probe.AppliesTo(MakeCtx(LaunchWindowStyle.Hidden)).Should().BeFalse();
    }

    private static ProbeContext MakeCtx(LaunchWindowStyle style)
    {
        var session = LaunchSession.Begin();
        var app = new AppEntry { Name = "x", Kind = AppKind.Executable, Path = @"C:\x.exe", WindowStyle = style };
        return new ProbeContext(session, app, null, NullLogger.Instance);
    }
}
