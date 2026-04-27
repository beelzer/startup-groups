using System.ComponentModel;
using StartupGroups.App.Localization;
using StartupGroups.App.Resources;
using StartupGroups.App.Services;

namespace StartupGroups.App.ViewModels;

public sealed class ThemeOption : INotifyPropertyChanged
{
    public ThemeOption(AppTheme value)
    {
        Value = value;
        LocalizationManager.Instance.PropertyChanged += OnLocalizationChanged;
    }

    public AppTheme Value { get; }

    public string DisplayName => Value switch
    {
        AppTheme.System => Strings.Settings_Theme_System,
        AppTheme.Light => Strings.Settings_Theme_Light,
        AppTheme.Dark => Strings.Settings_Theme_Dark,
        _ => Value.ToString(),
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));

    public override bool Equals(object? obj) => obj is ThemeOption o && o.Value == Value;
    public override int GetHashCode() => (int)Value;
}
