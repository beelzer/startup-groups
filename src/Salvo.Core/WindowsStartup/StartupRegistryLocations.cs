using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Salvo.Core.WindowsStartup;

/// <summary>
/// Single source of truth for the Run / StartupApproved key paths and the
/// source → (hive, path, approved-key) mapping. Previously these were
/// copy-pasted across <see cref="RegistryRunValueWriter"/>,
/// <see cref="WindowsStartupService"/>, and the elevator, which let them drift
/// — most notably the HKCU 32-bit read/write mismatch this class fixes.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class StartupRegistryLocations
{
    public const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunWow64Path = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    public const string StartupApprovedRun = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    public const string StartupApprovedRun32 = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";
    public const string StartupApprovedFolder = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

    public readonly record struct RunLocation(
        RegistryKey RunRoot,
        string RunPath,
        RegistryKey ApprovedRoot,
        string ApprovedPath);

    /// <summary>
    /// Resolves the Run value + approval-table location for the four registry
    /// Run sources. The *32 variants address the literal WOW6432Node path with
    /// the default registry view — the same physical key <see cref="WindowsStartupService.Enumerate"/>
    /// reads them from — so an entry is edited and deleted in exactly the key it
    /// was listed from. (Addressing them via the 32-bit view of the normal Run
    /// path instead worked for HKLM by coincidence but missed for HKCU, whose
    /// Run key is not WOW-redirected.)
    /// </summary>
    public static RunLocation? ResolveRun(StartupEntrySource source) => source switch
    {
        StartupEntrySource.RegistryRunUser => new RunLocation(
            Registry.CurrentUser, RunPath, Registry.CurrentUser, StartupApprovedRun),
        StartupEntrySource.RegistryRunUser32 => new RunLocation(
            Registry.CurrentUser, RunWow64Path, Registry.CurrentUser, StartupApprovedRun32),
        StartupEntrySource.RegistryRunMachine => new RunLocation(
            Registry.LocalMachine, RunPath, Registry.LocalMachine, StartupApprovedRun),
        StartupEntrySource.RegistryRunMachine32 => new RunLocation(
            Registry.LocalMachine, RunWow64Path, Registry.LocalMachine, StartupApprovedRun32),
        _ => null,
    };

    /// <summary>
    /// Resolves the StartupApproved location for every source (registry Run
    /// entries plus the Startup-folder entries, whose approval bits live under
    /// HKCU regardless of hive).
    /// </summary>
    public static (RegistryKey Root, string Path)? ResolveApproved(StartupEntrySource source) => source switch
    {
        StartupEntrySource.RegistryRunUser => (Registry.CurrentUser, StartupApprovedRun),
        StartupEntrySource.RegistryRunUser32 => (Registry.CurrentUser, StartupApprovedRun32),
        StartupEntrySource.RegistryRunMachine => (Registry.LocalMachine, StartupApprovedRun),
        StartupEntrySource.RegistryRunMachine32 => (Registry.LocalMachine, StartupApprovedRun32),
        StartupEntrySource.StartupFolderUser => (Registry.CurrentUser, StartupApprovedFolder),
        StartupEntrySource.StartupFolderCommon => (Registry.CurrentUser, StartupApprovedFolder),
        _ => null,
    };
}
