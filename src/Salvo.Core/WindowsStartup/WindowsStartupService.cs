using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Salvo.Core.WindowsStartup;

[SupportedOSPlatform("windows")]
public sealed class WindowsStartupService : IWindowsStartupService
{
    public IReadOnlyList<WindowsStartupEntry> Enumerate()
    {
        var entries = new List<WindowsStartupEntry>();

        ReadRegistryEntries(entries, Registry.CurrentUser, StartupRegistryLocations.RunPath, StartupRegistryLocations.StartupApprovedRun, StartupEntrySource.RegistryRunUser, canModify: true);
        ReadRegistryEntries(entries, Registry.CurrentUser, StartupRegistryLocations.RunWow64Path, StartupRegistryLocations.StartupApprovedRun32, StartupEntrySource.RegistryRunUser32, canModify: true);
        ReadRegistryEntries(entries, Registry.LocalMachine, StartupRegistryLocations.RunPath, StartupRegistryLocations.StartupApprovedRun, StartupEntrySource.RegistryRunMachine, canModify: false);
        ReadRegistryEntries(entries, Registry.LocalMachine, StartupRegistryLocations.RunWow64Path, StartupRegistryLocations.StartupApprovedRun32, StartupEntrySource.RegistryRunMachine32, canModify: false);

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
        switch (entry.Source)
        {
            case StartupEntrySource.RegistryRunUser:
            case StartupEntrySource.RegistryRunUser32:
            case StartupEntrySource.RegistryRunMachine:
            case StartupEntrySource.RegistryRunMachine32:
                // Run value + StartupApproved entry deleted together via the
                // shared writer, so this path can't drift from the elevator's.
                return RegistryRunValueWriter.Delete(entry.Source, entry.Name);

            case StartupEntrySource.StartupFolderUser:
            case StartupEntrySource.StartupFolderCommon:
                try
                {
                    if (!string.IsNullOrEmpty(entry.SourceDescription) && File.Exists(entry.SourceDescription))
                    {
                        File.Delete(entry.SourceDescription);
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

            default:
                return StartupOperationResult.Failed("Unsupported source");
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
            using var key = Registry.CurrentUser.CreateSubKey(StartupRegistryLocations.RunPath, writable: true)
                ?? throw new InvalidOperationException("Could not open Run key");

            // Don't silently clobber an existing Run value of the same name
            // (mirrors the uniqueness guard in RegistryRunValueWriter.Write).
            if (key.GetValueNames().Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                return StartupOperationResult.Failed($"A startup entry named '{name}' already exists");
            }

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
        // Same key the entry was enumerated from and is edited in — the *32
        // variants resolve to the literal WOW6432Node path, not the 32-bit view
        // of the normal Run path.
        var location = StartupRegistryLocations.ResolveRun(source);
        if (location is null) return Array.Empty<string>();

        try
        {
            using var key = location.Value.RunRoot.OpenSubKey(location.Value.RunPath, writable: false);
            return key?.GetValueNames() ?? Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    // HKLM-sourced entries are approved/disapproved via HKLM\StartupApproved; everything else via HKCU.
    private static (RegistryKey Root, string Path)? ResolveApprovedLocation(StartupEntrySource source) =>
        StartupRegistryLocations.ResolveApproved(source);

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
            using var approved = Registry.CurrentUser.OpenSubKey(StartupRegistryLocations.StartupApprovedFolder, writable: false);

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
