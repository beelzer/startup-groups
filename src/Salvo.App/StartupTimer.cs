using System.Diagnostics;
using System.IO;
using Salvo.Core.Services;

namespace Salvo.App;

/// <summary>
/// Records cold-start milestones to a flat file (and the Debug stream)
/// from <see cref="Program.Main"/> onwards. Cannot use Serilog because
/// Serilog isn't initialised until partway through <see cref="App.OnStartup"/>
/// — and the early phases (Velopack, App ctor, InitializeComponent) are
/// the ones we most want to time.
///
/// File format: one line per <see cref="Mark"/> call,
/// <c>{elapsed_ms,6} | {label}</c>. Overwrites the file at process start
/// so each launch gives a fresh timeline. Off by default in Release; flip
/// <see cref="Enabled"/> to true (or set the
/// <c>SALVO_STARTUP_TRACE</c> env var) to capture.
/// </summary>
internal static class StartupTimer
{
    private static readonly Stopwatch _sw = Stopwatch.StartNew();
    private static readonly object _lock = new();
    private static string? _path;
    private static bool _initialized;

    // Enable via env var OR a sentinel file in LocalDataFolder. The file
    // sentinel is what makes the trace survive UAC elevation: env vars
    // don't propagate across the runas verb, but the file does. To trace,
    // create an empty file at <LocalAppData>\Salvo.UserData\.startup-trace
    // (or set SALVO_STARTUP_TRACE=1).
    public static bool Enabled { get; set; } =
        Environment.GetEnvironmentVariable("SALVO_STARTUP_TRACE") == "1"
        || SafeFileExists(Path.Combine(AppPaths.LocalDataFolder, ".startup-trace"));

    private static bool SafeFileExists(string path)
    {
        try { return File.Exists(path); }
        catch { return false; }
    }

    public static void Mark(string label)
    {
        if (!Enabled) return;
        EnsureInitialized();
        // PID prefix lets us disambiguate the launcher process from the
        // relaunched-as-admin child process (TryRelaunchAsAdminIfConfigured
        // re-launches with the elevation verb, producing two trace lines
        // for each milestone).
        var line = $"pid={Environment.ProcessId,5}  {_sw.ElapsedMilliseconds,6}ms | {label}";
        Debug.WriteLine($"[startup] {line}");
        if (_path is null) return;
        try
        {
            lock (_lock)
            {
                File.AppendAllText(_path, line + Environment.NewLine);
            }
        }
        catch
        {
            // Don't let instrumentation crash startup.
        }
    }

    private static void EnsureInitialized()
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            Directory.CreateDirectory(AppPaths.LogFolder);
            _path = Path.Combine(AppPaths.LogFolder, "startup-trace.log");
            // Append (don't truncate) so the elevated child process's
            // marks accumulate alongside the launcher's. The PID prefix
            // makes them disambiguable. A "session header" still gets
            // written so it's easy to scan for the start of a launch
            // when reading back.
            File.AppendAllText(_path, $"=== launch pid={Environment.ProcessId} at {DateTime.Now:O} ===" + Environment.NewLine);
        }
        catch
        {
            _path = null;
        }
    }
}
