using StartupGroups.Core.Services;

namespace StartupGroups.Core.Tests;

public sealed class PathResolverTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly PathResolver _resolver = new();

    public PathResolverTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "sg-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void Resolve_ReturnsNull_ForNullOrEmpty()
    {
        _resolver.Resolve(null).Should().BeNull();
        _resolver.Resolve("").Should().BeNull();
        _resolver.Resolve("   ").Should().BeNull();
    }

    [Fact]
    public void Resolve_ReturnsPath_ForExistingFile()
    {
        var path = Path.Combine(_tempRoot, "app.exe");
        File.WriteAllText(path, "");

        _resolver.Resolve(path).Should().Be(path);
    }

    [Fact]
    public void Resolve_ReturnsNull_ForMissingFile()
    {
        _resolver.Resolve(Path.Combine(_tempRoot, "nope.exe")).Should().BeNull();
    }

    [Fact]
    public void Resolve_ExpandsEnvironmentVariables()
    {
        var sentinel = Path.Combine(_tempRoot, "env-test.exe");
        File.WriteAllText(sentinel, "");
        Environment.SetEnvironmentVariable("SG_TEST_DIR", _tempRoot);

        try
        {
            _resolver.Resolve("%SG_TEST_DIR%\\env-test.exe").Should().Be(sentinel);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SG_TEST_DIR", null);
        }
    }

    [Fact]
    public void Resolve_Glob_PicksHighestSortedMatch()
    {
        Directory.CreateDirectory(Path.Combine(_tempRoot, "v1.0"));
        Directory.CreateDirectory(Path.Combine(_tempRoot, "v2.0"));
        Directory.CreateDirectory(Path.Combine(_tempRoot, "v10.0"));

        File.WriteAllText(Path.Combine(_tempRoot, "v1.0", "foo.exe"), "");
        File.WriteAllText(Path.Combine(_tempRoot, "v2.0", "foo.exe"), "");
        File.WriteAllText(Path.Combine(_tempRoot, "v10.0", "foo.exe"), "");

        var resolved = _resolver.Resolve(Path.Combine(_tempRoot, "*", "foo.exe"));

        resolved.Should().EndWith(Path.Combine("v2.0", "foo.exe"));
    }

    [Fact]
    public void Resolve_Glob_ReturnsNull_WhenNoMatches()
    {
        var resolved = _resolver.Resolve(Path.Combine(_tempRoot, "missing-*", "x.exe"));
        resolved.Should().BeNull();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
