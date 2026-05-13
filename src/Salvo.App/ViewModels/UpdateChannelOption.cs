using System.ComponentModel;
using Salvo.App.Localization;
using Salvo.App.Resources;
using Salvo.App.Services;

namespace Salvo.App.ViewModels;

public sealed class UpdateChannelOption : INotifyPropertyChanged
{
    public UpdateChannelOption(UpdateChannel value)
    {
        Value = value;
        LocalizationManager.Instance.PropertyChanged += OnLocalizationChanged;
    }

    public UpdateChannel Value { get; }

    public string DisplayName => Value switch
    {
        UpdateChannel.Stable => Strings.Settings_UpdateChannel_Stable,
        UpdateChannel.Canary => Strings.Settings_UpdateChannel_Canary,
        _ => Value.ToString(),
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnLocalizationChanged(object? sender, PropertyChangedEventArgs e) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));

    public override bool Equals(object? obj) => obj is UpdateChannelOption o && o.Value == Value;
    public override int GetHashCode() => (int)Value;
}
