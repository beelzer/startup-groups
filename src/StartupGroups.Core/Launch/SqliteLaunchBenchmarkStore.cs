using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace StartupGroups.Core.Launch;

[SupportedOSPlatform("windows")]
public sealed class SqliteLaunchBenchmarkStore : ILaunchBenchmarkStore
{
    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS launches (
            launch_id TEXT PRIMARY KEY,
            app_id TEXT NOT NULL,
            app_name TEXT,
            group_id TEXT,
            resolved_path TEXT,
            resolved_path_hash TEXT,
            root_pid INTEGER,
            requested_at_utc TEXT NOT NULL,
            process_start_returned_at_utc TEXT,
            pid_resolved_at_utc TEXT,
            main_window_at_utc TEXT,
            input_idle_at_utc TEXT,
            quiet_at_utc TEXT,
            ready_at_utc TEXT,
            outcome INTEGER NOT NULL,
            signal_fired INTEGER NOT NULL,
            is_cold INTEGER NOT NULL,
            boot_epoch_utc TEXT NOT NULL,
            app_version TEXT,
            notes TEXT
        );
        CREATE INDEX IF NOT EXISTS idx_launches_app_id ON launches(app_id);
        CREATE INDEX IF NOT EXISTS idx_launches_requested_at ON launches(requested_at_utc);
        CREATE INDEX IF NOT EXISTS idx_launches_boot_epoch ON launches(boot_epoch_utc);
        CREATE TABLE IF NOT EXISTS launch_resources (
            launch_id TEXT NOT NULL,
            path TEXT NOT NULL,
            PRIMARY KEY (launch_id, path)
        );
        CREATE INDEX IF NOT EXISTS idx_launch_resources_launch_id ON launch_resources(launch_id);
        """;

    private readonly string _connectionString;
    private readonly ILogger<SqliteLaunchBenchmarkStore> _logger;

    public SqliteLaunchBenchmarkStore(string? databasePath = null, ILogger<SqliteLaunchBenchmarkStore>? logger = null)
    {
        var path = databasePath ?? Services.AppPaths.BenchmarksDbPath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
        _logger = logger ?? NullLogger<SqliteLaunchBenchmarkStore>.Instance;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var dir = Path.GetDirectoryName(new SqliteConnectionStringBuilder(_connectionString).DataSource);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SchemaSql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("Launch benchmark store initialized at {Path}", new SqliteConnectionStringBuilder(_connectionString).DataSource);
    }

    public async Task SaveAsync(LaunchMetrics metrics, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metrics);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO launches (
                launch_id, app_id, app_name, group_id, resolved_path, resolved_path_hash,
                root_pid, requested_at_utc, process_start_returned_at_utc, pid_resolved_at_utc,
                main_window_at_utc, input_idle_at_utc, quiet_at_utc, ready_at_utc,
                outcome, signal_fired, is_cold, boot_epoch_utc, app_version, notes
            ) VALUES (
                $launch_id, $app_id, $app_name, $group_id, $resolved_path, $resolved_path_hash,
                $root_pid, $requested_at_utc, $process_start_returned_at_utc, $pid_resolved_at_utc,
                $main_window_at_utc, $input_idle_at_utc, $quiet_at_utc, $ready_at_utc,
                $outcome, $signal_fired, $is_cold, $boot_epoch_utc, $app_version, $notes
            );
            """;

        command.Parameters.AddWithValue("$launch_id", metrics.LaunchId.ToString("D", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$app_id", metrics.AppId);
        command.Parameters.AddWithValue("$app_name", (object?)metrics.AppName ?? DBNull.Value);
        command.Parameters.AddWithValue("$group_id", (object?)metrics.GroupId ?? DBNull.Value);
        command.Parameters.AddWithValue("$resolved_path", (object?)metrics.ResolvedPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$resolved_path_hash", (object?)metrics.ResolvedPathHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$root_pid", (object?)metrics.RootPid ?? DBNull.Value);
        command.Parameters.AddWithValue("$requested_at_utc", FormatTimestamp(metrics.RequestedAt));
        command.Parameters.AddWithValue("$process_start_returned_at_utc", FormatTimestampOrNull(metrics.ProcessStartReturnedAt));
        command.Parameters.AddWithValue("$pid_resolved_at_utc", FormatTimestampOrNull(metrics.PidResolvedAt));
        command.Parameters.AddWithValue("$main_window_at_utc", FormatTimestampOrNull(metrics.MainWindowAt));
        command.Parameters.AddWithValue("$input_idle_at_utc", FormatTimestampOrNull(metrics.InputIdleAt));
        command.Parameters.AddWithValue("$quiet_at_utc", FormatTimestampOrNull(metrics.QuietAt));
        command.Parameters.AddWithValue("$ready_at_utc", FormatTimestampOrNull(metrics.ReadyAt));
        command.Parameters.AddWithValue("$outcome", (int)metrics.Outcome);
        command.Parameters.AddWithValue("$signal_fired", (int)metrics.SignalFired);
        command.Parameters.AddWithValue("$is_cold", metrics.IsCold ? 1 : 0);
        command.Parameters.AddWithValue("$boot_epoch_utc", FormatTimestamp(metrics.BootEpochUtc));
        command.Parameters.AddWithValue("$app_version", (object?)metrics.AppVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$notes", (object?)metrics.Notes ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<LaunchMetrics>> GetRecentAsync(string appId, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(appId);
        if (limit <= 0)
        {
            return Array.Empty<LaunchMetrics>();
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM launches
            WHERE app_id = $app_id
            ORDER BY requested_at_utc DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$app_id", appId);
        command.Parameters.AddWithValue("$limit", limit);

        return await ReadAllAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<LaunchMetrics>> GetAllSinceAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM launches
            WHERE requested_at_utc >= $since
            ORDER BY requested_at_utc DESC;
            """;
        command.Parameters.AddWithValue("$since", FormatTimestamp(sinceUtc));

        return await ReadAllAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> HasReadyLaunchSinceBootAsync(string appId, DateTimeOffset bootEpochUtc, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(appId);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1 FROM launches
            WHERE app_id = $app_id
              AND requested_at_utc >= $boot_epoch
              AND outcome = $ready_outcome
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$app_id", appId);
        command.Parameters.AddWithValue("$boot_epoch", FormatTimestamp(bootEpochUtc));
        command.Parameters.AddWithValue("$ready_outcome", (int)LaunchOutcome.Ready);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is not null;
    }

    public async Task SaveResourcesAsync(Guid launchId, IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var distinct = paths.Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (distinct.Count == 0) return;

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO launch_resources (launch_id, path) VALUES ($launch_id, $path);";
        var idParam = command.Parameters.Add("$launch_id", SqliteType.Text);
        var pathParam = command.Parameters.Add("$path", SqliteType.Text);
        idParam.Value = launchId.ToString("D", CultureInfo.InvariantCulture);

        foreach (var p in distinct)
        {
            pathParam.Value = p;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> GetResourcesSinceAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.launch_id, r.path
            FROM launch_resources r
            INNER JOIN launches l ON l.launch_id = r.launch_id
            WHERE l.requested_at_utc >= $since;
            """;
        command.Parameters.AddWithValue("$since", FormatTimestamp(sinceUtc));

        var result = new Dictionary<Guid, List<string>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = Guid.ParseExact(reader.GetString(0), "D");
            var path = reader.GetString(1);
            if (!result.TryGetValue(id, out var list))
            {
                list = new List<string>();
                result[id] = list;
            }
            list.Add(path);
        }

        return result.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value);
    }

    private static async Task<IReadOnlyList<LaunchMetrics>> ReadAllAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        var list = new List<LaunchMetrics>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(ReadRow(reader));
        }
        return list;
    }

    private static LaunchMetrics ReadRow(SqliteDataReader r) => new()
    {
        LaunchId = Guid.ParseExact(r.GetString(r.GetOrdinal("launch_id")), "D"),
        AppId = r.GetString(r.GetOrdinal("app_id")),
        AppName = GetNullableString(r, "app_name"),
        GroupId = GetNullableString(r, "group_id"),
        ResolvedPath = GetNullableString(r, "resolved_path"),
        ResolvedPathHash = GetNullableString(r, "resolved_path_hash"),
        RootPid = GetNullableInt(r, "root_pid"),
        RequestedAt = ParseTimestamp(r.GetString(r.GetOrdinal("requested_at_utc"))),
        ProcessStartReturnedAt = ParseNullableTimestamp(r, "process_start_returned_at_utc"),
        PidResolvedAt = ParseNullableTimestamp(r, "pid_resolved_at_utc"),
        MainWindowAt = ParseNullableTimestamp(r, "main_window_at_utc"),
        InputIdleAt = ParseNullableTimestamp(r, "input_idle_at_utc"),
        QuietAt = ParseNullableTimestamp(r, "quiet_at_utc"),
        ReadyAt = ParseNullableTimestamp(r, "ready_at_utc"),
        Outcome = (LaunchOutcome)r.GetInt32(r.GetOrdinal("outcome")),
        SignalFired = (ReadinessSignal)r.GetInt32(r.GetOrdinal("signal_fired")),
        IsCold = r.GetInt32(r.GetOrdinal("is_cold")) != 0,
        BootEpochUtc = ParseTimestamp(r.GetString(r.GetOrdinal("boot_epoch_utc"))),
        AppVersion = GetNullableString(r, "app_version"),
        Notes = GetNullableString(r, "notes"),
    };

    private static string? GetNullableString(SqliteDataReader r, string column)
    {
        var ord = r.GetOrdinal(column);
        return r.IsDBNull(ord) ? null : r.GetString(ord);
    }

    private static int? GetNullableInt(SqliteDataReader r, string column)
    {
        var ord = r.GetOrdinal(column);
        return r.IsDBNull(ord) ? null : r.GetInt32(ord);
    }

    private static DateTimeOffset? ParseNullableTimestamp(SqliteDataReader r, string column)
    {
        var ord = r.GetOrdinal(column);
        return r.IsDBNull(ord) ? null : ParseTimestamp(r.GetString(ord));
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static object FormatTimestampOrNull(DateTimeOffset? value) =>
        value is null ? DBNull.Value : FormatTimestamp(value.Value);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
