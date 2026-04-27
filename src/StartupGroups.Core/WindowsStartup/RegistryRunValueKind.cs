namespace StartupGroups.Core.WindowsStartup;

// Run keys legitimately use only REG_SZ and REG_EXPAND_SZ; anything else is rejected
// by Windows' startup pipeline. Limiting the editor to these two keeps the UI honest.
public enum RegistryRunValueKind
{
    String,
    ExpandString
}
