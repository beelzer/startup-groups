using System;
using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Threading;
using System.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StartupGroups.App.Resources;

namespace StartupGroups.App.Localization;

public sealed class LocalizationManager : INotifyPropertyChanged
{
    private static LocalizationManager? _instance;
    public static LocalizationManager Instance => _instance ??= new LocalizationManager();

    private readonly ResourceManager _resources;
    private ILogger<LocalizationManager> _logger = NullLogger<LocalizationManager>.Instance;

    private LocalizationManager()
    {
        _resources = Strings.ResourceManager;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] => GetString(key);

    public CultureInfo CurrentCulture => CultureInfo.CurrentUICulture;

    public FlowDirection FlowDirection =>
        CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft
            ? FlowDirection.RightToLeft
            : FlowDirection.LeftToRight;

    public void AttachLogger(ILogger<LocalizationManager> logger) => _logger = logger;

    public void ApplyCulture(CultureInfo? culture)
    {
        var target = culture ?? CultureInfo.InstalledUICulture;
        CultureInfo.DefaultThreadCurrentCulture = target;
        CultureInfo.DefaultThreadCurrentUICulture = target;
        Thread.CurrentThread.CurrentCulture = target;
        Thread.CurrentThread.CurrentUICulture = target;

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentCulture)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FlowDirection)));
    }

    public string GetString(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }

        try
        {
            var value = _resources.GetString(key, CultureInfo.CurrentUICulture);
            if (value is not null)
            {
                return value;
            }

            if (!CultureInfo.CurrentUICulture.Equals(CultureInfo.InvariantCulture))
            {
                var fallback = _resources.GetString(key, CultureInfo.InvariantCulture);
                if (fallback is not null)
                {
                    _logger.LogDebug("Missing translation for {Key} in {Culture}, using invariant", key, CultureInfo.CurrentUICulture.Name);
                    return fallback;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve resource {Key}", key);
        }

        _logger.LogWarning("Missing resource key: {Key}", key);
        return $"[[{key}]]";
    }

    public string Format(string key, params object?[] args)
    {
        var template = GetString(key);
        try
        {
            return string.Format(CultureInfo.CurrentUICulture, template, args);
        }
        catch (FormatException)
        {
            return template;
        }
    }
}
