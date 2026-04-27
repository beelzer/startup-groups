using StartupGroups.Core.Models;
using StartupGroups.Core.Services;

namespace StartupGroups.Core.Tests;

public sealed class ScoopInstalledAppsProviderTests : IDisposable
{
    private readonly string _root;

    public ScoopInstalledAppsProviderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sg-scoop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "apps"));
        Directory.CreateDirectory(Path.Combine(_root, "shims"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Enumerate_FindsApp_UsingManifestBinExe()
    {
        CreateApp("flaresolverr",
            manifest: """{"version": "3.3.21", "bin": "flaresolverr.exe"}""",
            filesInCurrent: new[] { "flaresolverr.exe" });

        var apps = await EnumerateAsync();

        var app = apps.Should().ContainSingle(a => a.Name == "flaresolverr").Subject;
        app.Source.Should().Be(InstalledAppSource.Scoop);
        app.Launch.Should().EndWith("flaresolverr.exe");
        app.Launch.Should().Contain(Path.Combine("flaresolverr", "current"));
        app.ExecutablePath.Should().Be(app.Launch);
        app.IconPath.Should().Be(app.Launch);
    }

    [Fact]
    public async Task Enumerate_HandlesBinArrayWithAliasForm()
    {
        // Scoop's nested form: [ [path, alias, args...] ]
        CreateApp("python",
            manifest: """{"bin": [["python.exe", "python"], ["Scripts/pip.exe", "pip"]]}""",
            filesInCurrent: new[] { "python.exe", "Scripts/pip.exe" });

        var apps = await EnumerateAsync();

        var app = apps.Should().ContainSingle(a => a.Name == "python").Subject;
        app.Launch.Should().EndWith("python.exe");
    }

    [Fact]
    public async Task Enumerate_FallsBackToShim_WhenBinIsScript()
    {
        CreateApp("some-cli",
            manifest: """{"bin": "some-cli.ps1"}""",
            filesInCurrent: new[] { "some-cli.ps1" });
        CreateShim("some-cli");

        var apps = await EnumerateAsync();

        var app = apps.Should().ContainSingle(a => a.Name == "some-cli").Subject;
        app.Launch.Should().Be(Path.Combine(_root, "shims", "some-cli.exe"));
    }

    [Fact]
    public async Task Enumerate_FallsBackToGuessedExe_WhenManifestMissing()
    {
        // No manifest, no shim — but current\<name>.exe exists.
        var currentDir = Path.Combine(_root, "apps", "flaresolverr", "current");
        Directory.CreateDirectory(currentDir);
        File.WriteAllBytes(Path.Combine(currentDir, "flaresolverr.exe"), Array.Empty<byte>());

        var apps = await EnumerateAsync();

        apps.Should().ContainSingle(a => a.Name == "flaresolverr");
    }

    [Fact]
    public async Task Enumerate_SkipsAppWithNoCurrentFolder()
    {
        Directory.CreateDirectory(Path.Combine(_root, "apps", "half-installed"));

        var apps = await EnumerateAsync();

        apps.Should().BeEmpty();
    }

    [Fact]
    public async Task Enumerate_SkipsScoopItself()
    {
        CreateApp("scoop",
            manifest: """{"bin": "scoop.ps1"}""",
            filesInCurrent: new[] { "scoop.ps1" });
        CreateShim("scoop");

        var apps = await EnumerateAsync();

        apps.Should().BeEmpty();
    }

    [Fact]
    public async Task Enumerate_SkipsAppWhenNothingIsLaunchable()
    {
        // Script bin, no shim, no exe — nothing we can launch.
        CreateApp("library-only",
            manifest: """{"bin": "lib.ps1"}""",
            filesInCurrent: new[] { "lib.ps1" });

        var apps = await EnumerateAsync();

        apps.Should().BeEmpty();
    }

    [Fact]
    public async Task Enumerate_SortsResultsByName()
    {
        CreateApp("zulu", manifest: """{"bin": "zulu.exe"}""", filesInCurrent: new[] { "zulu.exe" });
        CreateApp("alpha", manifest: """{"bin": "alpha.exe"}""", filesInCurrent: new[] { "alpha.exe" });
        CreateApp("mike", manifest: """{"bin": "mike.exe"}""", filesInCurrent: new[] { "mike.exe" });

        var apps = await EnumerateAsync();

        apps.Select(a => a.Name).Should().Equal("alpha", "mike", "zulu");
    }

    [Fact]
    public async Task Enumerate_ReturnsEmpty_WhenRootDoesNotExist()
    {
        var missing = Path.Combine(Path.GetTempPath(), "sg-scoop-missing-" + Guid.NewGuid().ToString("N"));
        var provider = new ScoopInstalledAppsProvider(logger: null, rootsOverride: new[] { missing });

        var apps = await provider.EnumerateAsync();

        apps.Should().BeEmpty();
    }

    private Task<IReadOnlyList<InstalledApp>> EnumerateAsync()
    {
        var provider = new ScoopInstalledAppsProvider(logger: null, rootsOverride: new[] { _root });
        return provider.EnumerateAsync();
    }

    private void CreateApp(string name, string manifest, string[] filesInCurrent)
    {
        var currentDir = Path.Combine(_root, "apps", name, "current");
        Directory.CreateDirectory(currentDir);
        File.WriteAllText(Path.Combine(currentDir, "manifest.json"), manifest);
        foreach (var rel in filesInCurrent)
        {
            var full = Path.Combine(currentDir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, Array.Empty<byte>());
        }
    }

    private void CreateShim(string name)
    {
        File.WriteAllBytes(Path.Combine(_root, "shims", name + ".exe"), Array.Empty<byte>());
    }
}
