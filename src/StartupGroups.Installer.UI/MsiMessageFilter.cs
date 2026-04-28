using System;

namespace StartupGroups.Installer.UI;

/// <summary>
/// Filters Windows Installer ActionStart messages that aren't useful to
/// surface to the user. MSI fires ActionStart for every component-level
/// action with the bare Component GUID as the message payload — visually
/// meaningless ("Installing {4CBFDE10-...}"). We strip those before
/// updating the progress UI.
/// </summary>
public static class MsiMessageFilter
{
    /// <summary>
    /// Returns true if <paramref name="s"/> looks like a raw GUID — either
    /// 36 chars (no braces) or 38 chars (with braces). Used to suppress
    /// MSI ActionStart messages whose payload is just a Component GUID.
    /// </summary>
    public static bool LooksLikeRawGuid(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var trimmed = s.Trim();
        if (trimmed.Length is not (36 or 38)) return false;
        return Guid.TryParse(trimmed, out _);
    }
}
