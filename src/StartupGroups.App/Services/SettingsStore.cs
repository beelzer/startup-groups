using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using StartupGroups.Core.Services;

namespace StartupGroups.App.Services;

public sealed class SettingsStore : ISettingsStore
{
    private readonly string _settingsPath;
    private AppSettings _current;

    public SettingsStore()
    {
        AppPaths.EnsureUserDirectories();
        _settingsPath = Path.Combine(AppPaths.UserDataFolder, "settings.json");
        _current = Load();
    }

    public AppSettings Current => _current;

    public event EventHandler<AppSettings>? Changed;

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _current = settings;

        var json = JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings);
        File.WriteAllText(_settingsPath, json);
        Changed?.Invoke(this, settings);
    }

    private AppSettings Load()
    {
        if (!File.Exists(_settingsPath))
        {
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
