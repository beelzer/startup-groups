using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Salvo.Core.Models;
using Salvo.Core.Models.Flow;

namespace Salvo.Core.Services;

public sealed class JsonConfigStore : IConfigStore, IDisposable
{
    private readonly ILogger<JsonConfigStore> _logger;
    private readonly Lock _writeLock = new();
    private FileSystemWatcher? _watcher;
    private DateTime _lastLoadUtc;

    // The last configuration that successfully loaded or saved. On a
    // corrupt read this is what Load returns instead of an empty
    // Configuration — an empty result would flow into the UI and the
    // very next Save would permanently destroy every group.
    private Configuration? _lastGood;

    public JsonConfigStore(string configPath, ILogger<JsonConfigStore>? logger = null)
    {
        ConfigPath = configPath ?? throw new ArgumentNullException(nameof(configPath));
        _logger = logger ?? NullLogger<JsonConfigStore>.Instance;
    }

    public string ConfigPath { get; }

    public bool LastLoadFailed { get; private set; }

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

            // A whitespace-only file is a torn/failed write, not a valid
            // empty config — treat it exactly like a parse failure.
            if (string.IsNullOrWhiteSpace(json))
            {
                return HandleCorruptFile(null);
            }

            try
            {
                var config = JsonSerializer.Deserialize(json, ConfigurationJsonContext.Default.Configuration);
                _lastLoadUtc = DateTime.UtcNow;
                if (config is null)
                {
                    return HandleCorruptFile(null);
                }

                // One-shot migration of any legacy Apps[] lists into the
                // new flow-graph form. Persists immediately so the next
                // load sees the migrated representation.
                var migrated = false;
                foreach (var group in config.Groups)
                {
                    if (FlowMigration.NeedsMigration(group))
                    {
                        FlowMigration.Migrate(group);
                        migrated = true;
                    }
                }
                if (migrated)
                {
                    SaveInternal(config);
                }

                LastLoadFailed = false;
                _lastGood = config;
                return config;
            }
            catch (JsonException ex)
            {
                return HandleCorruptFile(ex);
            }
        }
    }

    /// <summary>
    /// Called under the write lock when the config file cannot be read.
    /// Quarantines the corrupt bytes next to the original (so nothing is
    /// lost when a later Save overwrites the file), flags the failure,
    /// and returns the last known-good configuration — falling back to
    /// empty only when this store has never seen a good one.
    /// </summary>
    private Configuration HandleCorruptFile(Exception? cause)
    {
        var quarantinePath = ConfigPath + ".bad";
        try
        {
            File.Copy(ConfigPath, quarantinePath, overwrite: true);
            _logger.LogError(cause,
                "Configuration at {Path} is unreadable; corrupt copy quarantined at {Quarantine}. " +
                "Keeping the last known-good configuration in memory.",
                ConfigPath, quarantinePath);
        }
        catch (Exception copyEx)
        {
            _logger.LogError(copyEx, "Configuration at {Path} is unreadable and quarantining it also failed", ConfigPath);
        }

        LastLoadFailed = true;
        return _lastGood ?? new Configuration();
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

        // Phase A1: the list-based UI is the authoring source; rebuild
        // the graph from Apps on every save so the orchestrator (which
        // reads only Nodes/Edges) always sees the latest topology. This
        // step goes away in Phase A2 when the graph editor replaces the
        // list view as the authoring surface.
        foreach (var group in configuration.Groups)
        {
            if (group.Apps.Count > 0)
            {
                FlowMigration.Rebuild(group);
            }
        }

        var json = JsonSerializer.Serialize(configuration, ConfigurationJsonContext.Default.Configuration);
        var tempPath = ConfigPath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, ConfigPath, overwrite: true);
        _lastLoadUtc = DateTime.UtcNow;

        // A successful save defines the new known-good state (and makes
        // the on-disk file valid again after a corrupt read).
        _lastGood = configuration;
        LastLoadFailed = false;
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
            // An external write that corrupted the file must not be
            // published: subscribers would rebuild their state from the
            // fallback and the user's next edit would persist it over
            // whatever the external writer intended. The quarantine +
            // LastLoadFailed flag carry the failure; the in-memory state
            // stays as it was.
            if (!LastLoadFailed)
            {
                Changed?.Invoke(this, config);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed reacting to config change");
        }
    }

    public void Dispose() => StopWatching();
}
