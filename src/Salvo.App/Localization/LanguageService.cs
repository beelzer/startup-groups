using System.Globalization;
using Microsoft.Extensions.Logging;
using Salvo.App.Services;

namespace Salvo.App.Localization;

public sealed class LanguageService : ILanguageService
{
    private readonly ISettingsStore _settings;
    private readonly ILogger<LanguageService> _logger;

    public LanguageService(ISettingsStore settings, ILogger<LanguageService> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public SupportedLanguage Current =>
        SupportedLanguages.FindOrDefault(_settings.Current.UiCulture);

    public void ApplyPersisted()
    {
        var culture = ResolveCulture(_settings.Current.UiCulture);
        _logger.LogInformation("Applying UI culture {Culture}", culture?.Name ?? "system");
        LocalizationManager.Instance.ApplyCulture(culture);
    }

    public void SetLanguage(SupportedLanguage language)
    {
        var clone = _settings.Current.Clone();
        clone.UiCulture = language.CultureName;
        _settings.Save(clone);

        LocalizationManager.Instance.ApplyCulture(ResolveCulture(language.CultureName));
    }

    private static CultureInfo? ResolveCulture(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        try
        {
            return CultureInfo.GetCultureInfo(name);
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }
}

public interface ILanguageService
{
    SupportedLanguage Current { get; }
    void ApplyPersisted();
    void SetLanguage(SupportedLanguage language);
}
