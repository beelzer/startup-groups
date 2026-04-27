using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace StartupGroups.App.Localization;

public sealed record SupportedLanguage(string CultureName, string NativeDisplayName)
{
    public bool IsSystemDefault => CultureName.Length == 0;

    public CultureInfo? Culture =>
        IsSystemDefault ? null : CultureInfo.GetCultureInfo(CultureName);
}

public static class SupportedLanguages
{
    public static IReadOnlyList<SupportedLanguage> All { get; } =
    [
        new("", "System default"),
        new("en", "English"),
        new("fr", "Français"),
        new("de", "Deutsch"),
        new("ja", "日本語"),
        new("ru", "Русский"),
        new("ar", "العربية"),
        new("he", "עברית"),
        new("th", "ไทย"),
        new("hi", "हिन्दी"),
    ];

    public static SupportedLanguage FindOrDefault(string? cultureName) =>
        All.FirstOrDefault(l => string.Equals(l.CultureName, cultureName ?? string.Empty, System.StringComparison.OrdinalIgnoreCase))
        ?? All[0];
}
