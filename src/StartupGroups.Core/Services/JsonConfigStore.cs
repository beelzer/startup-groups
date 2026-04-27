using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StartupGroups.Core.Models;

namespace StartupGroups.Core.Services;

public sealed class JsonConfigStore : IConfigStore, IDisposable
{
    private readonly ILogger<JsonConfigStore> _logger;
    private readonly Lock _writeLock = new();
    private FileSystemWatcher? _watcher;
    private DateTime _lastLoadUtc;

    public JsonConfigStore(string configPath, ILogger<JsonConfigStore>? logger = null)
    {
        ConfigPath = configPath ?? throw new ArgumentNullException(nameof(configPath));
        _logger = logger ?? NullLogger<JsonConfigStore>.Instance;
    }

    public string ConfigPath { get; }

    public event EventHandler<Configuration>? Changed;

    public Configuration Load()
    {
        lock (_writeLock)
        {
            if (!File.Exists(ConfigPath))
            {
                var empty = new Configuration();
                SaveInternal(empty);
                _lastLoadUtc = DateTime.UtcNow;
                return empty;
            }

            var json = File.ReadAllText(ConfigPath);

            if (string.IsNullOrWhiteSpace(json))
            {
                return new Configuration();
            }

            try
            {
                var config = JsonSerializer.Deserialize(json, ConfigurationJsonContext.Default.Configuration);
                _lastLoadUtc = DateTime.UtcNow;
                return config ?? new Configuration();
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Failed to parse configuration at {Path}", ConfigPath);
                return new Configuration();
            }
        }
    }

    public void Save(Configuration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        lock (_writeLock)
        {
            SaveInternal(configuration);
        }
    }

    private void SaveInternal(Configuration configuration)
    {
        var directory = Path.GetDirectoryName(ConfigPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(configuration, ConfigurationJsonContext.Default.Configuration);
        var tempPath = ConfigPath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, ConfigPath, overwrite: true);
        _lastLoadUtc = DateTime.UtcNow;
    }

    public void BeginWatching()
    {
        if (_watcher is not null)
        {
            return;
        }

        var directory = Path.GetDirectoryName(ConfigPath);
        var fileName = Path.GetFileName(ConfigPath);
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(fileName))
        {
            return;
        }

        Directory.CreateDirectory(directory);

        _watcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };

        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
        _watcher.Renamed += OnFileChanged;
    }

    public void StopWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if ((DateTime.UtcNow - _lastLoadUtc) < Timeouts.ConfigPersistDebounce)
        {
            return;
        }

        try
        {
            Thread.Sleep(Timeouts.ConfigPersistCooldown);
            var config = Load();
            Changed?.Invoke(this, config);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed reacting to config change");
        }
    }

    public void Dispose() => StopWatching();
}
