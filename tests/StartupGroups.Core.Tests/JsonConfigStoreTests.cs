using StartupGroups.Core.Models;
using StartupGroups.Core.Services;

namespace StartupGroups.Core.Tests;

public sealed class JsonConfigStoreTests : IDisposable
{
    private readonly string _tempRoot;

    public JsonConfigStoreTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "sg-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void Load_ReturnsEmptyConfig_WhenMissing_AndCreatesFile()
    {
        var path = Path.Combine(_tempRoot, "config.json");
        var store = new JsonConfigStore(path);

        var config = store.Load();

        config.Groups.Should().BeEmpty();
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public void SaveThenLoad_RoundTripsConfiguration()
    {
        var path = Path.Combine(_tempRoot, "rt.json");
        var store = new JsonConfigStore(path);

        var config = new Configuration
        {
            Groups =
            [
                new Group
                {
                    Id = "gaming",
                    Name = "Gaming",
                    Apps =
                    [
                        new AppEntry { Name = "Steam", Path = @"C:\Program Files (x86)\Steam\steam.exe" },
                        new AppEntry { Name = "Discord", Path = @"C:\discord.exe", Args = "--minimized", DelayAfterSeconds = 3 }
                    ]
                },
                new Group
                {
                    Id = "arr",
                    Name = "Arr Stack",
                    Apps =
                    [
                        new AppEntry { Name = "Radarr", Kind = AppKind.Service, Service = "Radarr" }
                    ]
                }
            ]
        };

        store.Save(config);
        var loaded = store.Load();

        loaded.Groups.Should().HaveCount(2);
        loaded.Groups[0].Apps.Should().HaveCount(2);
        loaded.Groups[0].Apps[1].DelayAfterSeconds.Should().Be(3);
        loaded.Groups[1].Apps[0].Kind.Should().Be(AppKind.Service);
        loaded.Groups[1].Apps[0].Service.Should().Be("Radarr");
    }

    [Fact]
    public void Load_ReturnsEmpty_WhenJsonInvalid()
    {
        var path = Path.Combine(_tempRoot, "bad.json");
        File.WriteAllText(path, "{ broken");
        var store = new JsonConfigStore(path);

        var config = store.Load();

        config.Groups.Should().BeEmpty();
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
