using StartupGroups.Installer.UI;

namespace StartupGroups.App.Tests;

/// <summary>
/// Covers the bare-GUID filter on MSI ActionStart messages. Without this,
/// the Burn BA's Progress screen flashed "Installing {4CBFDE10-...}" with
/// raw Component GUIDs while Windows Installer worked through its
/// component table — reported by the user as unreadable noise. These tests
/// lock the filter behaviour so a future tweak doesn't quietly start
/// surfacing GUIDs again.
/// </summary>
public sealed class MsiMessageFilterTests
{
    [Fact]
    public void LooksLikeRawGuid_ReturnsTrue_ForBracedGuid()
    {
        // Standard MSI Component GUID payload format.
        MsiMessageFilter.LooksLikeRawGuid("{4CBFDE10-0000-0000-0000-000000000000}").Should().BeTrue();
    }

    [Fact]
    public void LooksLikeRawGuid_ReturnsTrue_ForUnbracedGuid()
    {
        // Some MSI tables also fire the GUID without braces.
        MsiMessageFilter.LooksLikeRawGuid("4cbfde10-0000-0000-0000-000000000000").Should().BeTrue();
    }

    [Fact]
    public void LooksLikeRawGuid_TrimsWhitespace()
    {
        MsiMessageFilter.LooksLikeRawGuid("  {4CBFDE10-0000-0000-0000-000000000000}  ").Should().BeTrue();
    }

    [Fact]
    public void LooksLikeRawGuid_ReturnsFalse_ForRealActionMessage()
    {
        // The messages we actually want to surface to the user.
        MsiMessageFilter.LooksLikeRawGuid("Copying new files").Should().BeFalse();
        MsiMessageFilter.LooksLikeRawGuid("Updating component registration").Should().BeFalse();
    }

    [Fact]
    public void LooksLikeRawGuid_ReturnsFalse_ForWrongLength()
    {
        // Length gate is the cheap fast-fail before the Guid.TryParse round-trip.
        MsiMessageFilter.LooksLikeRawGuid("{4CBFDE10-0000-0000-0000-00000000}").Should().BeFalse();   // too short
        MsiMessageFilter.LooksLikeRawGuid("{4CBFDE10-0000-0000-0000-000000000000}EXTRA").Should().BeFalse(); // too long
    }

    [Fact]
    public void LooksLikeRawGuid_ReturnsFalse_ForEmptyOrWhitespace()
    {
        MsiMessageFilter.LooksLikeRawGuid("").Should().BeFalse();
        MsiMessageFilter.LooksLikeRawGuid("   ").Should().BeFalse();
    }

    [Fact]
    public void LooksLikeRawGuid_ReturnsFalse_ForCorrectLengthButInvalidGuid()
    {
        // A 38-char string with the right shape but invalid hex characters.
        MsiMessageFilter.LooksLikeRawGuid("{XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX}").Should().BeFalse();
    }
}
