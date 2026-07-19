using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Salvo.App.Services;
using Salvo.Core.Models;
using Salvo.Core.Services;

namespace Salvo.App.ViewModels;

public partial class AppEntryEditorViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private readonly IKnownAppsDatabase _knownApps;
    private readonly IProcessMatcherResolver _matchers;

    public AppEntryEditorViewModel(
        IDialogService dialogs,
        IKnownAppsDatabase knownApps,
        IProcessMatcherResolver matchers)
    {
        _dialogs = dialogs;
        _knownApps = knownApps;
        _matchers = matchers;
        EnsureChipSubscriptions();
    }

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private AppKind _kind = AppKind.Executable;
    [ObservableProperty] private string? _path;
    [ObservableProperty] private string? _service;
    [ObservableProperty] private string? _args;
    [ObservableProperty] private string? _workingDirectory;
    [ObservableProperty] private LaunchWindowStyle _windowStyle = LaunchWindowStyle.Normal;
    [ObservableProperty] private int _delayAfterSeconds;
    [ObservableProperty] private bool _enabled = true;

    /// <summary>Icon extracted from the current target, shown in the editor header.</summary>
    [ObservableProperty] private BitmapSource? _icon;

    public bool IsNew { get; set; } = true;

    public AppKind[] AvailableKinds { get; } = [AppKind.Executable, AppKind.Service];

    public LaunchWindowStyle[] AvailableWindowStyles { get; } =
        [LaunchWindowStyle.Normal, LaunchWindowStyle.Minimized, LaunchWindowStyle.Maximized, LaunchWindowStyle.Hidden];

    public bool IsExecutable => Kind == AppKind.Executable;
    public bool IsService => Kind == AppKind.Service;

    public ObservableCollection<SuggestedArgumentViewModel> SuggestedArguments { get; } = [];
    public bool HasSuggestions => SuggestedArguments.Count > 0;

    public ObservableCollection<ArgumentChipViewModel> ArgumentChips { get; } = [];
    [ObservableProperty] private string _argumentInput = string.Empty;
    private bool _syncingFromChips;
    private bool _chipsInitialized;

    private void EnsureChipSubscriptions()
    {
        if (_chipsInitialized) return;
        _chipsInitialized = true;
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

    private void OnChipPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not ArgumentChipViewModel chip) return;
        if (e.PropertyName != nameof(ArgumentChipViewModel.Value)) return;

        if (string.IsNullOrEmpty(chip.Value))
        {
            ArgumentChips.Remove(chip);
        }
        UpdateArgsFromChips();
    }

    partial void OnKindChanged(AppKind value)
    {
        OnPropertyChanged(nameof(IsExecutable));
        OnPropertyChanged(nameof(IsService));
        RefreshSuggestions();
        RefreshIcon();
    }

    partial void OnPathChanged(string? value)
    {
        RefreshSuggestions();
        RefreshIcon();
    }

    // Pull the target's icon for the header preview. Runs on the UI thread
    // (STA), which the shell icon extractor requires; best-effort, so a
    // missing/odd target just shows no icon.
    private void RefreshIcon()
    {
        if (Kind != AppKind.Executable || string.IsNullOrWhiteSpace(Path))
        {
            Icon = null;
            return;
        }
        try
        {
            Icon = ShellIconExtractor.GetImage(Path, 48);
        }
        catch
        {
            Icon = null;
        }
    }
    partial void OnServiceChanged(string? value) => RefreshSuggestions();
    partial void OnArgsChanged(string? value)
    {
        if (!_syncingFromChips)
        {
            RefreshArgumentChips();
        }
        RefreshSuggestionSelections();
    }

    [RelayCommand]
    private void CommitArgumentInput()
    {
        var value = (ArgumentInput ?? string.Empty).Trim();
        if (value.Length == 0) return;
        ArgumentChips.Add(new ArgumentChipViewModel(value));
        ArgumentInput = string.Empty;
        UpdateArgsFromChips();
    }

    [RelayCommand]
    private void RemoveArgumentChip(ArgumentChipViewModel? chip)
    {
        if (chip is null) return;
        ArgumentChips.Remove(chip);
        UpdateArgsFromChips();
    }

    [RelayCommand]
    private void RemoveLastArgumentChip()
    {
        if (!string.IsNullOrEmpty(ArgumentInput)) return;
        if (ArgumentChips.Count == 0) return;
        ArgumentChips.RemoveAt(ArgumentChips.Count - 1);
        UpdateArgsFromChips();
    }

    private void RefreshArgumentChips()
    {
        EnsureChipSubscriptions();
        ArgumentChips.Clear();

        var tokens = SplitTokens(Args ?? string.Empty);
        var needsValueFlags = GetNeedsValueFlagSet();

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            var normalized = NormalizeToken(token);
            var rawInnerContainsEquals = StripQuotes(token).Contains('=');

            if (needsValueFlags.Contains(normalized)
                && !rawInnerContainsEquals
                && i + 1 < tokens.Count)
            {
                var nextRaw = StripQuotes(tokens[i + 1]);
                if (nextRaw.Length > 0
                    && !nextRaw.StartsWith('-')
                    && !nextRaw.StartsWith('/'))
                {
                    ArgumentChips.Add(new ArgumentChipViewModel(token + ' ' + tokens[i + 1]));
                    i++;
                    continue;
                }
            }

            ArgumentChips.Add(new ArgumentChipViewModel(token));
        }
    }

    private HashSet<string> GetNeedsValueFlagSet()
    {
        var entry = new AppEntry
        {
            Name = Name,
            Kind = Kind,
            Path = Path,
            Service = Service,
            Args = Args,
            WorkingDirectory = WorkingDirectory,
            DelayAfterSeconds = DelayAfterSeconds,
            Enabled = Enabled
        };
        var matchers = _matchers.GetMatchers(entry);
        var known = _knownApps.FindMatch(entry, matchers);
        if (known is null)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return known.Arguments
            .Where(a => a.NeedsValue)
            .Select(a => NormalizeToken(SplitTokens(a.Flag).FirstOrDefault() ?? a.Flag))
            .Where(s => !string.IsNullOrEmpty(s))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private void UpdateArgsFromChips()
    {
        _syncingFromChips = true;
        try
        {
            Args = ArgumentChips.Count == 0
                ? null
                : string.Join(' ', ArgumentChips.Select(c => c.Value));
        }
        finally
        {
            _syncingFromChips = false;
        }
        RefreshSuggestionSelections();
    }

    [RelayCommand]
    private void BrowseExecutable()
    {
        var selected = _dialogs.PromptForExecutable(Path);
        if (!string.IsNullOrEmpty(selected))
        {
            Path = selected;
            if (string.IsNullOrWhiteSpace(Name))
            {
                Name = System.IO.Path.GetFileNameWithoutExtension(selected);
            }
        }
    }

    [RelayCommand]
    private void BrowseWorkingDirectory()
    {
        var selected = _dialogs.PromptForDirectory(WorkingDirectory);
        if (!string.IsNullOrEmpty(selected))
        {
            WorkingDirectory = selected;
        }
    }

    [RelayCommand]
    private void ToggleSuggestion(SuggestedArgumentViewModel? suggestion)
    {
        if (suggestion is null) return;

        if (suggestion.IsSelected)
        {
            var flagNorm = NormalizeToken(SplitTokens(suggestion.Flag).FirstOrDefault() ?? suggestion.Flag);
            var match = ArgumentChips.FirstOrDefault(c =>
            {
                var firstToken = SplitTokens(c.Value).FirstOrDefault() ?? c.Value;
                return string.Equals(NormalizeToken(firstToken), flagNorm, StringComparison.OrdinalIgnoreCase);
            });
            if (match is not null)
            {
                ArgumentChips.Remove(match);
                UpdateArgsFromChips();
            }
        }
        else
        {
            var initial = suggestion.NeedsValue
                ? (suggestion.Flag.EndsWith('=') ? suggestion.Flag : suggestion.Flag + " ")
                : suggestion.Flag;
            var chip = new ArgumentChipViewModel(initial);
            ArgumentChips.Add(chip);
            UpdateArgsFromChips();

            if (suggestion.NeedsValue)
            {
                chip.EditText = initial;
                chip.IsEditing = true;
            }
        }
    }

    private void RefreshSuggestions()
    {
        SuggestedArguments.Clear();

        var entry = new AppEntry
        {
            Name = Name,
            Kind = Kind,
            Path = Path,
            Service = Service,
            Args = Args,
            WorkingDirectory = WorkingDirectory,
            DelayAfterSeconds = DelayAfterSeconds,
            Enabled = Enabled
        };

        var matchers = _matchers.GetMatchers(entry);
        var known = _knownApps.FindMatch(entry, matchers);
        if (known is not null)
        {
            foreach (var arg in known.Arguments)
            {
                SuggestedArguments.Add(new SuggestedArgumentViewModel(arg.Flag, arg.Description, arg.NeedsValue));
            }
        }

        RefreshSuggestionSelections();
        OnPropertyChanged(nameof(HasSuggestions));
    }

    private void RefreshSuggestionSelections()
    {
        var current = Args ?? string.Empty;
        foreach (var s in SuggestedArguments)
        {
            s.IsSelected = ContainsFlag(current, s.Flag);
        }
    }

    private static bool ContainsFlag(string args, string flag)
    {
        if (string.IsNullOrWhiteSpace(args) || string.IsNullOrWhiteSpace(flag)) return false;
        var flagTokens = SplitTokens(flag).Select(NormalizeToken).ToList();
        var argTokens = SplitTokens(args).Select(NormalizeToken).ToList();
        for (var i = 0; i + flagTokens.Count <= argTokens.Count; i++)
        {
            if (argTokens.Skip(i).Take(flagTokens.Count).SequenceEqual(flagTokens, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string NormalizeToken(string token)
    {
        var t = StripQuotes(token);
        var eq = t.IndexOf('=');
        return eq >= 0 ? t[..eq] : t;
    }

    private static string StripQuotes(string token)
    {
        var t = token.Trim();
        return t.Length >= 2 && t[0] == '"' && t[^1] == '"' ? t[1..^1] : t;
    }

    private static List<string> SplitTokens(string input)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        foreach (var ch in input)
        {
            if (ch == '"') { inQuotes = !inQuotes; current.Append(ch); continue; }
            if (!inQuotes && char.IsWhiteSpace(ch))
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(ch);
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }

    public void LoadFrom(AppEntryViewModel source)
    {
        Name = source.Name;
        Kind = source.Kind;
        Path = source.Path;
        Service = source.Service;
        Args = source.Args;
        WorkingDirectory = source.WorkingDirectory;
        WindowStyle = source.WindowStyle;
        DelayAfterSeconds = source.DelayAfterSeconds;
        Enabled = source.Enabled;
    }

    public void LoadFromInstalled(InstalledApp installed)
    {
        Name = installed.Name;
        if (installed.Source == InstalledAppSource.Service)
        {
            Kind = AppKind.Service;
            Service = installed.ServiceName;
            Path = null;
        }
        else
        {
            Kind = AppKind.Executable;
            Path = installed.Launch;
            Service = null;
        }
        Args = null;
        WorkingDirectory = null;
        WindowStyle = LaunchWindowStyle.Normal;
        DelayAfterSeconds = 0;
        Enabled = true;
    }

    public void ApplyTo(AppEntryViewModel target)
    {
        target.Name = Name;
        target.Kind = Kind;
        target.Path = Path;
        target.Service = Service;
        target.Args = Args;
        target.WorkingDirectory = WorkingDirectory;
        target.WindowStyle = WindowStyle;
        target.DelayAfterSeconds = DelayAfterSeconds;
        target.Enabled = Enabled;
    }
}

public partial class SuggestedArgumentViewModel : ObservableObject
{
    public SuggestedArgumentViewModel(string flag, string description, bool needsValue)
    {
        Flag = flag;
        Description = description;
        NeedsValue = needsValue;
    }

    public string Flag { get; }
    public string Description { get; }
    public bool NeedsValue { get; }

    [ObservableProperty] private bool _isSelected;
}

public partial class ArgumentChipViewModel : ObservableObject
{
    public ArgumentChipViewModel(string value)
    {
        Value = value;
        EditText = value;
    }

    [ObservableProperty] private string _value = string.Empty;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editText = string.Empty;

    [RelayCommand]
    private void BeginEdit()
    {
        EditText = Value;
        IsEditing = true;
    }

    [RelayCommand]
    private void CommitEdit()
    {
        var text = (EditText ?? string.Empty).Trim();
        Value = text;
        IsEditing = false;
    }

    [RelayCommand]
    private void CancelEdit()
    {
        EditText = Value;
        IsEditing = false;
    }
}
