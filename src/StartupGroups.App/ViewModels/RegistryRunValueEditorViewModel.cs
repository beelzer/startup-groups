using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using StartupGroups.App.Resources;
using StartupGroups.App.Services;
using StartupGroups.Core.Elevation;
using StartupGroups.Core.Launch;
using StartupGroups.Core.WindowsStartup;

namespace StartupGroups.App.ViewModels;

public partial class RegistryRunValueEditorViewModel : ObservableObject
{
    private readonly IWindowsStartupService _service;
    private readonly IElevationClient _elevation;
    private readonly IDialogService _dialogs;
    private readonly ILogger<RegistryRunValueEditorViewModel> _logger;

    private WindowsStartupEntry? _entry;
    private string[] _siblingNames = [];

    public RegistryRunValueEditorViewModel(
        IWindowsStartupService service,
        IElevationClient elevation,
        IDialogService dialogs,
        ILogger<RegistryRunValueEditorViewModel> logger)
    {
        _service = service;
        _elevation = elevation;
        _dialogs = dialogs;
        _logger = logger;
        ArgumentChips.CollectionChanged += (_, e) =>
        {
            if (e.OldItems is not null)
            {
                foreach (ArgumentChipViewModel c in e.OldItems)
                {
                    c.PropertyChanged -= OnChipPropertyChanged;
                }
            }
            if (e.NewItems is not null)
            {
                foreach (ArgumentChipViewModel c in e.NewItems)
                {
                    c.PropertyChanged += OnChipPropertyChanged;
                }
            }
        };
    }

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _path = string.Empty;
    [ObservableProperty] private string _command = string.Empty;
    [ObservableProperty] private string _argumentInput = string.Empty;
    [ObservableProperty] private bool _useRawMode;
    [ObservableProperty] private RegistryRunValueKind _kind = RegistryRunValueKind.String;
    [ObservableProperty] private string _keyPathDisplay = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private bool _requiresAdmin;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _saved;
    [ObservableProperty] private bool _deleted;

    public ObservableCollection<ArgumentChipViewModel> ArgumentChips { get; } = [];

    // Type is shown read-only — regedit doesn't let you change it either, and the
    // only legitimate use case (fixing a REG_SZ value that needed REG_EXPAND_SZ for
    // %VAR% expansion) is rare enough that exposing it as an editable control was
    // mostly a foot-gun. Save preserves whatever kind the entry already had.
    public string KindDisplay => Kind == RegistryRunValueKind.ExpandString
        ? Strings.RegEditor_Type_ExpandString
        : Strings.RegEditor_Type_String;

    partial void OnKindChanged(RegistryRunValueKind value) => OnPropertyChanged(nameof(KindDisplay));

    public string OriginalName { get; private set; } = string.Empty;

    public StartupEntrySource? Source => _entry?.Source;

    public string SourceLabel => _entry?.SourceShortLabel ?? string.Empty;

    public bool LoadFrom(WindowsStartupEntry entry)
    {
        _entry = entry;
        OriginalName = entry.Name;
        Name = entry.Name;
        KeyPathDisplay = RegistryRunValueWriter.FormatKeyPath(entry.Source);

        var details = _service.TryReadRunValue(entry);
        var rawCommand = details?.Command ?? entry.Command;
        Kind = details?.Kind ?? RegistryRunValueKind.String;

        if (details is null)
        {
            StatusMessage = Strings.RegEditor_ReadFailed;
            HasError = true;
        }

        // Try to split rawCommand into path + args. If the parse is ambiguous
        // (unbalanced quotes, etc.) fall back to a single raw textbox so we don't
        // mangle the value on save.
        var parsed = TryParseCommand(rawCommand);
        if (parsed is not null)
        {
            UseRawMode = false;
            Path = parsed.Value.Path;
            Command = rawCommand;
            ReplaceChips(parsed.Value.Args);
        }
        else
        {
            UseRawMode = true;
            Path = string.Empty;
            Command = rawCommand;
            ArgumentChips.Clear();
        }

        // HKLM rows from a non-elevated app need UAC on save; surface that up front.
        RequiresAdmin = _entry.Source is StartupEntrySource.RegistryRunMachine
            or StartupEntrySource.RegistryRunMachine32
            && !ElevationDetector.IsElevated;

        _siblingNames = [.. _service.GetSiblingValueNames(entry.Source)];
        return details is not null;
    }

    [RelayCommand]
    private void BrowseExecutable()
    {
        var picked = _dialogs.PromptForExecutable(Path);
        if (string.IsNullOrEmpty(picked))
        {
            return;
        }

        // Don't pre-quote the Path field — quoting happens at save time so the
        // textbox stays clean to read/edit.
        Path = picked;
    }

    [RelayCommand]
    private void CommitArgumentInput()
    {
        var value = (ArgumentInput ?? string.Empty).Trim();
        if (value.Length == 0) return;
        ArgumentChips.Add(new ArgumentChipViewModel(value));
        ArgumentInput = string.Empty;
    }

    [RelayCommand]
    private void RemoveArgumentChip(ArgumentChipViewModel? chip)
    {
        if (chip is null) return;
        ArgumentChips.Remove(chip);
    }

    [RelayCommand]
    private void RemoveLastArgumentChip()
    {
        if (!string.IsNullOrEmpty(ArgumentInput)) return;
        if (ArgumentChips.Count == 0) return;
        ArgumentChips.RemoveAt(ArgumentChips.Count - 1);
    }

    [RelayCommand]
    private void CopyKeyPath()
    {
        if (string.IsNullOrEmpty(KeyPathDisplay)) return;
        try
        {
            Clipboard.SetText($@"{KeyPathDisplay}\{Name}");
            StatusMessage = Strings.RegEditor_KeyPathCopied;
            HasError = false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Clipboard copy failed");
        }
    }

    // Deep-links regedit to the current key by priming HKCU\...\Regedit\LastKey,
    // which regedit reads on launch. Doesn't work if regedit is already running —
    // that's a regedit limitation, not something we can route around.
    [RelayCommand]
    private void OpenInRegedit()
    {
        if (string.IsNullOrEmpty(KeyPathDisplay)) return;

        try
        {
            using (var applets = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit",
                writable: true))
            {
                applets?.SetValue("LastKey", $@"Computer\{KeyPathDisplay}",
                    Microsoft.Win32.RegistryValueKind.String);
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "regedit.exe",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to launch regedit");
            HasError = true;
            StatusMessage = string.Format(CultureInfo.CurrentUICulture,
                Strings.RegEditor_OpenInRegeditFailedFormat, ex.Message);
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_entry is null) return;

        var trimmedName = (Name ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(trimmedName))
        {
            HasError = true;
            StatusMessage = Strings.RegEditor_NameRequired;
            return;
        }

        var commandToSave = UseRawMode
            ? Command
            : BuildCommand(Path, ArgumentChips.Select(c => c.Value));

        if (string.IsNullOrEmpty(commandToSave))
        {
            HasError = true;
            StatusMessage = Strings.RegEditor_CommandRequired;
            return;
        }

        var renamed = !string.Equals(trimmedName, OriginalName, StringComparison.OrdinalIgnoreCase);
        if (renamed && _siblingNames.Any(s => s.Equals(trimmedName, StringComparison.OrdinalIgnoreCase)))
        {
            HasError = true;
            StatusMessage = string.Format(CultureInfo.CurrentUICulture,
                Strings.RegEditor_NameConflictFormat, trimmedName);
            return;
        }

        var edit = new RegistryRunValueEdit
        {
            Source = _entry.Source,
            OriginalName = OriginalName,
            NewName = trimmedName,
            Command = commandToSave,
            Kind = Kind
        };

        IsBusy = true;
        try
        {
            var result = _service.TryUpdateRunValue(edit);
            if (result.Status == StartupOperationStatus.NeedsAdmin)
            {
                var elevated = await _elevation.InvokeAsync(new ElevationRequest
                {
                    Action = ElevationAction.WriteRegistryRunValue,
                    RegistryEdit = edit
                }).ConfigureAwait(true);

                if (!elevated)
                {
                    HasError = true;
                    StatusMessage = Strings.RegEditor_ElevationDeclined;
                    return;
                }
            }
            else if (!result.Succeeded)
            {
                HasError = true;
                StatusMessage = string.Format(CultureInfo.CurrentUICulture,
                    Strings.RegEditor_SaveFailedFormat, result.Message);
                return;
            }

            Saved = true;
            OriginalName = trimmedName;
            HasError = false;
            StatusMessage = Strings.RegEditor_Saved;
            CloseRequested?.Invoke(this, true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (_entry is null) return;

        var confirmed = await _dialogs.ConfirmAsync(
            Strings.Dialog_RemoveStartupItem_Title,
            string.Format(CultureInfo.CurrentUICulture,
                Strings.Dialog_RemoveStartupItem_MessageFormat, OriginalName)).ConfigureAwait(true);
        if (!confirmed) return;

        IsBusy = true;
        try
        {
            var result = _service.TryRemove(_entry);
            if (result.Status == StartupOperationStatus.NeedsAdmin)
            {
                var elevated = await _elevation.InvokeAsync(new ElevationRequest
                {
                    Action = ElevationAction.DeleteRegistryRunValue,
                    RegistryEdit = new RegistryRunValueEdit
                    {
                        Source = _entry.Source,
                        OriginalName = OriginalName,
                        NewName = OriginalName,
                        Command = Command,
                        Kind = Kind
                    }
                }).ConfigureAwait(true);

                if (!elevated)
                {
                    HasError = true;
                    StatusMessage = Strings.RegEditor_ElevationDeclined;
                    return;
                }
            }
            else if (!result.Succeeded)
            {
                HasError = true;
                StatusMessage = string.Format(CultureInfo.CurrentUICulture,
                    Strings.RegEditor_SaveFailedFormat, result.Message);
                return;
            }

            Deleted = true;
            CloseRequested?.Invoke(this, true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        CloseRequested?.Invoke(this, false);
    }

    public event EventHandler<bool>? CloseRequested;

    private void OnChipPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not ArgumentChipViewModel chip) return;
        if (e.PropertyName != nameof(ArgumentChipViewModel.Value)) return;
        if (string.IsNullOrEmpty(chip.Value))
        {
            ArgumentChips.Remove(chip);
        }
    }

    // Combines `--flag <next>` (or `/flag <next>`) into a single chip whenever the
    // flag doesn't already carry an inline `=value` and the next token doesn't itself
    // look like a flag. Without per-app knowledge there's no way to know which flags
    // truly take a value, so we lean toward combining — Run-key commands almost
    // exclusively use the `--flag value` shape.
    private void ReplaceChips(IList<string> args)
    {
        ArgumentChips.Clear();
        for (var i = 0; i < args.Count; i++)
        {
            var token = args[i];
            if (string.IsNullOrWhiteSpace(token)) continue;

            var stripped = StripQuotes(token);
            var looksLikeFlag = stripped.StartsWith('-') || stripped.StartsWith('/');
            var hasInlineValue = stripped.Contains('=');

            if (looksLikeFlag && !hasInlineValue && i + 1 < args.Count)
            {
                var next = StripQuotes(args[i + 1]);
                if (next.Length > 0 && !next.StartsWith('-') && !next.StartsWith('/'))
                {
                    ArgumentChips.Add(new ArgumentChipViewModel(token + ' ' + args[i + 1]));
                    i++;
                    continue;
                }
            }

            ArgumentChips.Add(new ArgumentChipViewModel(token));
        }
    }

    private static string StripQuotes(string token)
    {
        var t = token.Trim();
        return t.Length >= 2 && t[0] == '"' && t[^1] == '"' ? t[1..^1] : t;
    }

    // Splits a command string into a leading executable path and the trailing
    // argument tokens. Returns null if the command can't be parsed unambiguously
    // (unbalanced quotes, etc.) so the caller can fall back to a raw textbox.
    private static (string Path, List<string> Args)? TryParseCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return (string.Empty, []);
        }

        var trimmed = command.TrimStart();
        string path;
        string remainder;

        if (trimmed.StartsWith('"'))
        {
            var closeIdx = trimmed.IndexOf('"', 1);
            if (closeIdx < 1) return null;
            path = trimmed[1..closeIdx];
            remainder = closeIdx + 1 < trimmed.Length ? trimmed[(closeIdx + 1)..].TrimStart() : string.Empty;
        }
        else
        {
            var spaceIdx = trimmed.IndexOf(' ');
            path = spaceIdx > 0 ? trimmed[..spaceIdx] : trimmed;
            remainder = spaceIdx > 0 ? trimmed[(spaceIdx + 1)..].TrimStart() : string.Empty;
        }

        var args = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        foreach (var ch in remainder)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                current.Append(ch);
                continue;
            }
            if (!inQuotes && char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    args.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }
            current.Append(ch);
        }
        if (current.Length > 0) args.Add(current.ToString());

        if (inQuotes) return null;

        return (path, args);
    }

    private static string BuildCommand(string path, IEnumerable<string> args)
    {
        var trimmedPath = (path ?? string.Empty).Trim();
        var quotedPath = trimmedPath.Contains(' ') && !trimmedPath.StartsWith('"')
            ? $"\"{trimmedPath}\""
            : trimmedPath;

        var argsJoined = string.Join(' ', args.Where(a => !string.IsNullOrEmpty(a)));
        return string.IsNullOrEmpty(argsJoined) ? quotedPath : $"{quotedPath} {argsJoined}";
    }
}
