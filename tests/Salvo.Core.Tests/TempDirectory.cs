using Microsoft.Data.Sqlite;

namespace Salvo.Core.Tests;

/// <summary>
/// A throwaway temp directory for tests, replacing the per-class copies that
/// were scattered across the suite.
/// </summary>
internal sealed class TempDirectory : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "salvo-test-" + Guid.NewGuid().ToString("N"));

    public TempDirectory() => Directory.CreateDirectory(Root);

    public string CreateFile(string name, string contents = "")
    {
        var path = Path.Combine(Root, name);
        File.WriteAllText(path, contents);
        return path;
    }

    public string Combine(string name) => Path.Combine(Root, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // Best-effort.
        }
    }
}

/// <summary>
/// Temp directory for tests that open a SQLite database in it. Dispose clears
/// the Microsoft.Data.Sqlite connection pools first — otherwise pooled handles
/// keep the file open and the recursive delete fails on Windows CI.
/// </summary>
internal sealed class SqliteTempDirectory : IDisposable
{
    private readonly TempDirectory _dir = new();

    public string Root => _dir.Root;

    public string DbPath(string name = "test.db") => _dir.Combine(name);

    public void Dispose()
    {
        try
        {
            SqliteConnection.ClearAllPools();
        }
        catch
        {
            // Best-effort.
        }
        _dir.Dispose();
    }
}
