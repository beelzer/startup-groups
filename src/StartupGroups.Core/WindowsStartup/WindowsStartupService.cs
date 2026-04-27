using System.Runtime.Versioning;
using Microsoft.Win32;

namespace StartupGroups.Core.WindowsStartup;

[SupportedOSPlatform("windows")]
public sealed class WindowsStartupService : IWindowsStartupService
{
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunWow64Path = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupApprovedRun = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string StartupApprovedRun32 = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";
    private const string StartupApprovedFolder = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

    public IReadOnlyList<WindowsStartupEntry> Enumerate()
    {
        var entries = new List<WindowsStartupEntry>();

        ReadRegistryEntries(entries, Registry.CurrentUser, RunPath, StartupApprovedRun, StartupEntrySource.RegistryRunUser, canModify: true);
        ReadRegistryEntries(entries, Registry.CurrentUser, RunWow64Path, StartupApprovedRun32, StartupEntrySource.RegistryRunUser32, canModify: true);
        ReadRegistryEntries(entries, Registry.LocalMachine, RunPath, StartupApprovedRun, StartupEntrySource.RegistryRunMachine, canModify: false);
        ReadRegistryEntries(entries, Registry.LocalMachine, RunWow64Path, StartupApprovedRun32, StartupEntrySource.RegistryRunMachine32, canModify: false);

        ReadFolderEntries(entries, Environment.GetFolderPath(Environment.SpecialFolder.Startup), StartupEntrySource.StartupFolderUser, canModify: true);
        ReadFolderEntries(entries, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), StartupEntrySource.StartupFolderCommon, canModify: false);

        return entries
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public StartupOperationResult TrySetEnabled(WindowsStartupEntry entry, bool enabled)
    {
        var location = ResolveApprovedLocation(entry.Source);
        if (location is null)
        {
            return StartupOperationResult.Failed("Unsupported source");
        }

        try
        {
            using var key = location.Value.Root.CreateSubKey(location.Value.Path, writable: true)
                ?? throw new InvalidOperationException("Could not open StartupApproved key");

            key.SetValue(entry.Name, BuildApprovedValue(enabled), RegistryValueKind.Binary);
            return StartupOperationResult.Ok(enabled ? "Enabled" : "Disabled");
        }
        catch (UnauthorizedAccessException)
        {
            return StartupOperationResult.NeedsAdmin();
        }
        catch (System.Security.SecurityException)
        {
            return StartupOperationResult.NeedsAdmin();
        }
        catch (Exception ex)
        {
            return StartupOperationResult.Failed(ex.Message);
        }
    }

    public StartupOperationResult TryRemove(WindowsStartupEntry entry)
    {
        try
        {
            switch (entry.Source)
            {
                case StartupEntrySource.RegistryRunUser:
                    RemoveRegistryValue(Registry.CurrentUser, RunPath, entry.Name);
                    break;
                case StartupEntrySource.RegistryRunUser32:
                    RemoveRegistryValue(Registry.CurrentUser, RunWow64Path, entry.Name);
                    break;
                case StartupEntrySource.RegistryRunMachine:
                    RemoveRegistryValue(Registry.LocalMachine, RunPath, entry.Name);
                    break;
                case StartupEntrySource.RegistryRunMachine32:
                    RemoveRegistryValue(Registry.LocalMachine, RunWow64Path, entry.Name);
                    break;
                case StartupEntrySource.StartupFolderUser:
                case StartupEntrySource.StartupFolderCommon:
                    if (!string.IsNullOrEmpty(entry.SourceDescription) && File.Exists(entry.SourceDescription))
                    {
                        File.Delete(entry.SourceDescription);
                    }
                    break;
                default:
                    return StartupOperationResult.Failed("Unsupported source");
            }

            RemoveFromApproved(entry);
            return StartupOperationResult.Ok("Removed");
        }
        catch (UnauthorizedAccessException)
        {
            return StartupOperationResult.NeedsAdmin();
        }
        catch (System.Security.SecurityException)
        {
            return StartupOperationResult.NeedsAdmin();
        }
        catch (Exception ex)
        {
            return StartupOperationResult.Failed(ex.Message);
        }
    }

    public StartupOperationResult TryAddUserRunEntry(string name, string command)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return StartupOperationResult.Failed("Name required");
        }

        if (string.IsNullOrWhiteSpace(command))
        {
            return StartupOperationResult.Failed("Command required");
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunPath, writable: true)
                ?? throw new InvalidOperationException("Could not open Run key");

            key.SetValue(name, command, RegistryValueKind.String);
            return StartupOperationResult.Ok("Added");
        }
        catch (UnauthorizedAccessException)
        {
            return StartupOperationResult.NeedsAdmin();
        }
        catch (Exception ex)
        {
            return StartupOperationResult.Failed(ex.Message);
        }
    }

    public RegistryRunValueDetails? TryReadRunValue(WindowsStartupEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return RegistryRunValueWriter.Read(entry.Source, entry.Name);
    }

    public StartupOperationResult TryUpdateRunValue(RegistryRunValueEdit edit)
    {
        return RegistryRunValueWriter.Write(edit);
    }

    public IReadOnlyList<string> GetSiblingValueNames(StartupEntrySource source)
    {
        var (root, path) = source switch
        {
            StartupEntrySource.RegistryRunUser => (Registry.CurrentUser, RunPath),
            StartupEntrySource.RegistryRunUser32 => (RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry32), RunPath),
            StartupEntrySource.RegistryRunMachine => (Registry.LocalMachine, RunPath),
            StartupEntrySource.RegistryRunMachine32 => (RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32), RunPath),
            _ => ((RegistryKey?)null, string.Empty),
        };

        if (root is null) return Array.Empty<string>();

        try
        {
            using var key = root.OpenSubKey(path, writable: false);
            return key?.GetValueNames() ?? Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    // HKLM-sourced entries are approved/disapproved via HKLM\StartupApproved; everything else via HKCU.
    private static (RegistryKey Root, string Path)? ResolveApprovedLocation(StartupEntrySource source) => source switch
    {
        StartupEntrySource.RegistryRunUser => (Registry.CurrentUser, StartupApprovedRun),
        StartupEntrySource.RegistryRunUser32 => (Registry.CurrentUser, StartupApprovedRun32),
        StartupEntrySource.RegistryRunMachine => (Registry.LocalMachine, StartupApprovedRun),
        StartupEntrySource.RegistryRunMachine32 => (Registry.LocalMachine, StartupApprovedRun32),
        StartupEntrySource.StartupFolderUser => (Registry.CurrentUser, StartupApprovedFolder),
        StartupEntrySource.StartupFolderCommon => (Registry.CurrentUser, StartupApprovedFolder),
        _ => null
    };

    private static void ReadRegistryEntries(
        List<WindowsStartupEntry> entries,
        RegistryKey root,
        string subKey,
        string approvedSubKey,
        StartupEntrySource source,
        bool canModify)
    {
        try
        {
            using var runKey = root.OpenSubKey(subKey, writable: false);
            if (runKey is null)
            {
                return;
            }

            // Approval flags for HKLM-sourced entries live in HKLM\StartupApproved; HKCU entries in HKCU\StartupApproved.
            var approvedRoot = source is StartupEntrySource.RegistryRunMachine or StartupEntrySource.RegistryRunMachine32
                ? Registry.LocalMachine
                : Registry.CurrentUser;
            using var approved = approvedRoot.OpenSubKey(approvedSubKey, writable: false);

            foreach (var valueName in runKey.GetValueNames())
            {
                var raw = runKey.GetValue(valueName)?.ToString();
                if (string.IsNullOrEmpty(raw))
                {
                    continue;
                }

                var enabled = IsApproved(approved, valueName);

                entries.Add(new WindowsStartupEntry
                {
                    Name = valueName,
                    Command = raw,
                    Source = source,
                    Enabled = enabled,
                    CanModifyWithoutAdmin = canModify,
                    SourceDescription = $@"{(root == Registry.CurrentUser ? "HKCU" : "HKLM")}\{subKey}"
                });
            }
        }
        catch
        {
            // Ignore unreadable keys.
        }
    }

    private static void ReadFolderEntries(
        List<WindowsStartupEntry> entries,
        string folder,
        StartupEntrySource source,
        bool canModify)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            return;
        }

        try
        {
            using var approved = Registry.CurrentUser.OpenSubKey(StartupApprovedFolder, writable: false);

            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly))
            {
                if (Path.GetFileName(file).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var name = Path.GetFileName(file);
                var enabled = IsApproved(approved, name);

                entries.Add(new WindowsStartupEntry
                {
                    Name = name,
                    Command = file,
                    Source = source,
                    Enabled = enabled,
                    CanModifyWithoutAdmin = canModify,
                    SourceDescription = file
                });
            }
        }
        catch
        {
        }
    }

    private static bool IsApproved(RegistryKey? approvedKey, string name)
    {
        if (approvedKey is null)
        {
            return true;
        }

        var raw = approvedKey.GetValue(name);
        if (raw is not byte[] bytes || bytes.Length == 0)
        {
            return true;
        }

        // Byte 0 low bit: 0 = enabled, 1 = disabled.
        // Windows uses 0x02 for enabled and 0x03 for disabled.
        return (bytes[0] & 0x01) == 0;
    }

    private static byte[] BuildApprovedValue(bool enabled)
    {
        var value = new byte[12];
        value[0] = enabled ? (byte)0x02 : (byte)0x03;
        var fileTime = BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc());
        Array.Copy(fileTime, 0, value, 4, 8);
        return value;
    }

    private static void RemoveRegistryValue(RegistryKey root, string subKey, string valueName)
    {
        using var key = root.OpenSubKey(subKey, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }

    private static void RemoveFromApproved(WindowsStartupEntry entry)
    {
        var location = ResolveApprovedLocation(entry.Source);
        if (location is null)
        {
            return;
        }

        try
        {
            using var key = location.Value.Root.OpenSubKey(location.Value.Path, writable: true);
            key?.DeleteValue(entry.Name, throwOnMissingValue: false);
        }
        catch
        {
        }
    }
}
