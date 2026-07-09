using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Salvo.Core.WindowsStartup;

// Shared write logic used by both WindowsStartupService (in-process for HKCU)
// and the Salvo.Elevator helper (for HKLM after UAC). Keeping the
// rename + approval-table re-keying in one place prevents the two paths from
// drifting apart.
[SupportedOSPlatform("windows")]
public static class RegistryRunValueWriter
{
    public static StartupOperationResult Write(RegistryRunValueEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);

        if (string.IsNullOrWhiteSpace(edit.OriginalName))
        {
            return StartupOperationResult.Failed("Original name required");
        }

        var newName = (edit.NewName ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(newName))
        {
            return StartupOperationResult.Failed("Name required");
        }

        if (string.IsNullOrEmpty(edit.Command))
        {
            return StartupOperationResult.Failed("Command required");
        }

        var location = StartupRegistryLocations.ResolveRun(edit.Source);
        if (location is null)
        {
            return StartupOperationResult.Failed("Unsupported source");
        }

        try
        {
            using var runKey = location.Value.RunRoot.OpenSubKey(location.Value.RunPath, writable: true)
                ?? throw new InvalidOperationException("Run key missing");

            var existingNames = runKey.GetValueNames();
            var renamed = !string.Equals(edit.OriginalName, newName, StringComparison.OrdinalIgnoreCase);

            // Belt-and-braces uniqueness check. The VM also enforces this, but a stale
            // editor (something changed in regedit between open and save) would slip past.
            if (renamed && existingNames.Any(n => n.Equals(newName, StringComparison.OrdinalIgnoreCase)))
            {
                return StartupOperationResult.Failed($"A value named '{newName}' already exists");
            }

            if (!existingNames.Any(n => n.Equals(edit.OriginalName, StringComparison.OrdinalIgnoreCase)))
            {
                return StartupOperationResult.Failed($"Original value '{edit.OriginalName}' no longer exists");
            }

            var kind = edit.Kind == RegistryRunValueKind.ExpandString
                ? RegistryValueKind.ExpandString
                : RegistryValueKind.String;

            runKey.SetValue(newName, edit.Command, kind);

            if (renamed)
            {
                runKey.DeleteValue(edit.OriginalName, throwOnMissingValue: false);
            }

            // Re-key the StartupApproved entry so the Enabled bit follows the rename.
            using var approved = location.Value.ApprovedRoot.OpenSubKey(location.Value.ApprovedPath, writable: true);
            if (approved is not null)
            {
                var existing = approved.GetValue(edit.OriginalName) as byte[];
                if (renamed)
                {
                    approved.DeleteValue(edit.OriginalName, throwOnMissingValue: false);
                }
                if (existing is not null)
                {
                    approved.SetValue(newName, existing, RegistryValueKind.Binary);
                }
            }

            return StartupOperationResult.Ok("Saved");
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

    /// <summary>
    /// Deletes a Run value and its StartupApproved entry in one place, so the
    /// in-process HKCU path and the elevator's post-UAC HKLM path stay aligned.
    /// </summary>
    public static StartupOperationResult Delete(StartupEntrySource source, string name)
    {
        var location = StartupRegistryLocations.ResolveRun(source);
        if (location is null)
        {
            return StartupOperationResult.Failed("Unsupported source");
        }

        try
        {
            using (var runKey = location.Value.RunRoot.OpenSubKey(location.Value.RunPath, writable: true))
            {
                runKey?.DeleteValue(name, throwOnMissingValue: false);
            }

            using var approved = location.Value.ApprovedRoot.OpenSubKey(location.Value.ApprovedPath, writable: true);
            approved?.DeleteValue(name, throwOnMissingValue: false);

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

    public static RegistryRunValueDetails? Read(StartupEntrySource source, string name)
    {
        var location = StartupRegistryLocations.ResolveRun(source);
        if (location is null) return null;

        try
        {
            using var runKey = location.Value.RunRoot.OpenSubKey(location.Value.RunPath, writable: false);
            if (runKey is null) return null;

            var raw = runKey.GetValue(name, defaultValue: null,
                options: RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (raw is null) return null;

            var kind = runKey.GetValueKind(name) == RegistryValueKind.ExpandString
                ? RegistryRunValueKind.ExpandString
                : RegistryRunValueKind.String;

            return new RegistryRunValueDetails
            {
                Source = source,
                Name = name,
                Command = raw.ToString() ?? string.Empty,
                Kind = kind
            };
        }
        catch
        {
            return null;
        }
    }

    public static string FormatKeyPath(StartupEntrySource source)
    {
        var location = StartupRegistryLocations.ResolveRun(source);
        if (location is null) return string.Empty;

        var hivePrefix = location.Value.RunRoot.Name; // "HKEY_CURRENT_USER" or "HKEY_LOCAL_MACHINE"
        return $@"{hivePrefix}\{location.Value.RunPath}";
    }
}
