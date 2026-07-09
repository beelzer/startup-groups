using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Salvo.Core.Services;

namespace Salvo.App.Services;

public sealed class SettingsStore : ISettingsStore
{
    private readonly string _settingsPath;
    private readonly ILogger<SettingsStore> _logger;
    private AppSettings _current;

    // Logger is optional (defaults to NullLogger) so the standalone
    // `new SettingsStore()` used before the DI container exists still
    // compiles; the DI registration injects the real logger.
    public SettingsStore(ILogger<SettingsStore>? logger = null)
    {
        _logger = logger ?? NullLogger<SettingsStore>.Instance;
        AppPaths.EnsureUserDirectories();
        _settingsPath = AppPaths.SettingsFilePath;
        _current = Load();
    }

    public AppSettings Current => _current;

    public event EventHandler<AppSettings>? Changed;

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _current = settings;

        // Atomic write (temp file + File.Move) mirrors JsonConfigStore: a
        // crash or power loss mid-write must never leave a truncated
        // settings.json, which Load would then discard back to defaults.
        var json = JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings);
        var tempPath = _settingsPath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _settingsPath, overwrite: true);
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
        catch (Exception ex)
        {
            // A corrupt/unreadable file falls back to defaults rather than
            // crashing launch, but log it so the silent settings reset
            // leaves a diagnostic trail.
            _logger.LogError(ex, "Failed to load settings from {Path}; falling back to defaults", _settingsPath);
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
