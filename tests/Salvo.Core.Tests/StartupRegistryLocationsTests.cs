using Microsoft.Win32;
using Salvo.Core.WindowsStartup;

namespace Salvo.Core.Tests;

/// <summary>
/// Locks the source → (root, path, approved-key) mapping, in particular the
/// HKCU 32-bit fix: the *32 sources must resolve to the literal WOW6432Node
/// path under the plain hive root — the same physical key Enumerate reads them
/// from — not the 32-bit registry view of the normal Run path (which coincides
/// for HKLM but diverges for HKCU, whose Run key is not WOW-redirected).
/// </summary>
public sealed class StartupRegistryLocationsTests
{
    [Fact]
    public void ResolveRun_User32_UsesWow6432NodePath_UnderCurrentUser()
    {
        var loc = StartupRegistryLocations.ResolveRun(StartupEntrySource.RegistryRunUser32);

        loc.Should().NotBeNull();
        loc!.Value.RunPath.Should().Be(StartupRegistryLocations.RunWow64Path);
        loc.Value.RunRoot.Name.Should().Be(Registry.CurrentUser.Name);
        loc.Value.ApprovedPath.Should().Be(StartupRegistryLocations.StartupApprovedRun32);
    }

    [Fact]
    public void ResolveRun_User_UsesNormalRunPath_UnderCurrentUser()
    {
        var loc = StartupRegistryLocations.ResolveRun(StartupEntrySource.RegistryRunUser);

        loc!.Value.RunPath.Should().Be(StartupRegistryLocations.RunPath);
        loc.Value.RunRoot.Name.Should().Be(Registry.CurrentUser.Name);
        loc.Value.ApprovedPath.Should().Be(StartupRegistryLocations.StartupApprovedRun);
    }

    [Fact]
    public void ResolveRun_Machine32_UsesWow6432NodePath_UnderLocalMachine()
    {
        var loc = StartupRegistryLocations.ResolveRun(StartupEntrySource.RegistryRunMachine32);

        loc!.Value.RunPath.Should().Be(StartupRegistryLocations.RunWow64Path);
        loc.Value.RunRoot.Name.Should().Be(Registry.LocalMachine.Name);
    }

    [Fact]
    public void ResolveRun_StartupFolderSource_IsNull()
    {
        StartupRegistryLocations.ResolveRun(StartupEntrySource.StartupFolderUser).Should().BeNull();
    }

    [Fact]
    public void ResolveApproved_CoversFolderSources_UnderCurrentUser()
    {
        var loc = StartupRegistryLocations.ResolveApproved(StartupEntrySource.StartupFolderUser);

        loc.Should().NotBeNull();
        loc!.Value.Path.Should().Be(StartupRegistryLocations.StartupApprovedFolder);
        loc.Value.Root.Name.Should().Be(Registry.CurrentUser.Name);
    }
}
