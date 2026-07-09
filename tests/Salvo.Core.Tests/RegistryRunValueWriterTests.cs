using Salvo.Core.WindowsStartup;

namespace Salvo.Core.Tests;

/// <summary>
/// Pins the pure, registry-free logic of the startup writer: Write's
/// pre-registry validation gates, the source→hive/path mapping surfaced by
/// FormatKeyPath, and the StartupApproved enabled-bit encode/decode. No test
/// mutates the real registry.
/// </summary>
public sealed class RegistryRunValueWriterTests
{
    [Fact]
    public void Write_Fails_OnEmptyOriginalName()
    {
        var result = RegistryRunValueWriter.Write(new RegistryRunValueEdit
        {
            OriginalName = "",
            NewName = "x",
            Command = "c",
            Source = StartupEntrySource.RegistryRunUser,
        });
        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public void Write_Fails_OnEmptyNewName()
    {
        var result = RegistryRunValueWriter.Write(new RegistryRunValueEdit
        {
            OriginalName = "x",
            NewName = "",
            Command = "c",
            Source = StartupEntrySource.RegistryRunUser,
        });
        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public void Write_Fails_OnEmptyCommand()
    {
        var result = RegistryRunValueWriter.Write(new RegistryRunValueEdit
        {
            OriginalName = "x",
            NewName = "x",
            Command = "",
            Source = StartupEntrySource.RegistryRunUser,
        });
        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public void Write_Fails_OnUnsupportedSource_BeforeTouchingRegistry()
    {
        // All fields valid but a Startup-folder source has no Run location, so
        // Write returns before any registry access.
        var result = RegistryRunValueWriter.Write(new RegistryRunValueEdit
        {
            OriginalName = "x",
            NewName = "x",
            Command = "c",
            Source = StartupEntrySource.StartupFolderUser,
        });
        result.Succeeded.Should().BeFalse();
        result.Message.Should().Be("Unsupported source");
    }

    [Theory]
    [InlineData(StartupEntrySource.RegistryRunUser, @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run")]
    [InlineData(StartupEntrySource.RegistryRunMachine, @"HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\Run")]
    [InlineData(StartupEntrySource.RegistryRunUser32, @"HKEY_CURRENT_USER\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run")]
    [InlineData(StartupEntrySource.RegistryRunMachine32, @"HKEY_LOCAL_MACHINE\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run")]
    public void FormatKeyPath_MapsSourceToHiveAndPath(StartupEntrySource source, string expected)
    {
        RegistryRunValueWriter.FormatKeyPath(source).Should().Be(expected);
    }

    [Fact]
    public void FormatKeyPath_ReturnsEmpty_ForStartupFolderSource()
    {
        RegistryRunValueWriter.FormatKeyPath(StartupEntrySource.StartupFolderUser).Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ApprovedValue_RoundTrips(bool enabled)
    {
        var blob = WindowsStartupService.BuildApprovedValue(enabled);
        WindowsStartupService.ParseApprovedEnabled(blob).Should().Be(enabled);
    }

    [Fact]
    public void ParseApprovedEnabled_TreatsMissingOrEmptyAsEnabled()
    {
        WindowsStartupService.ParseApprovedEnabled(null).Should().BeTrue();
        WindowsStartupService.ParseApprovedEnabled(System.Array.Empty<byte>()).Should().BeTrue();
    }
}
