using Salvo.Core.Models;
using Salvo.Core.Services;

namespace Salvo.Core.Tests;

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
                        new AppEntry { Name = "Discord", Path = @"C:\discord.exe", Args = "--minimized", DelayAfterSeconds = 3, WindowStyle = LaunchWindowStyle.Hidden }
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
        loaded.Groups[0].Apps[0].WindowStyle.Should().Be(LaunchWindowStyle.Normal);
        loaded.Groups[0].Apps[1].DelayAfterSeconds.Should().Be(3);
        loaded.Groups[0].Apps[1].WindowStyle.Should().Be(LaunchWindowStyle.Hidden);
        loaded.Groups[1].Apps[0].Kind.Should().Be(AppKind.Service);
        loaded.Groups[1].Apps[0].Service.Should().Be("Radarr");
    }

    [Fact]
    public void Load_ReturnsEmpty_WhenJsonInvalid_AndNoPriorGoodState()
    {
        var path = Path.Combine(_tempRoot, "bad.json");
        File.WriteAllText(path, "{ broken");
        var store = new JsonConfigStore(path);

        var config = store.Load();

        config.Groups.Should().BeEmpty();
        store.LastLoadFailed.Should().BeTrue();
        File.ReadAllText(path + ".bad").Should().Be("{ broken",
            "the unreadable original must be quarantined before anything can overwrite it");
    }

    [Fact]
    public void Load_KeepsLastGoodConfig_WhenFileTurnsCorrupt()
    {
        // The data-loss chain this breaks: corrupt file → Load returns
        // empty → watcher publishes empty → user's next edit saves empty,
        // permanently destroying every group.
        var path = Path.Combine(_tempRoot, "config.json");
        var store = new JsonConfigStore(path);
        store.Save(new Configuration
        {
            Groups = [new Group { Id = "g", Name = "G", Apps = [new AppEntry { Name = "app", Path = @"C:\a.exe" }] }],
        });
        store.Load().Groups.Should().HaveCount(1);

        File.WriteAllText(path, "{ definitely not json");

        var reloaded = store.Load();

        reloaded.Groups.Should().HaveCount(1, "a corrupt read must fall back to the last known-good configuration");
        reloaded.Groups[0].Id.Should().Be("g");
        store.LastLoadFailed.Should().BeTrue();
        File.Exists(path + ".bad").Should().BeTrue();
    }

    [Fact]
    public void Load_TreatsWhitespaceFile_AsCorrupt()
    {
        // A whitespace-only file is a torn write, not a deliberate empty
        // config — it must not silently load as empty.
        var path = Path.Combine(_tempRoot, "config.json");
        var store = new JsonConfigStore(path);
        store.Save(new Configuration { Groups = [new Group { Id = "g", Name = "G" }] });

        File.WriteAllText(path, "   \r\n");

        store.Load().Groups.Should().HaveCount(1);
        store.LastLoadFailed.Should().BeTrue();
    }

    [Fact]
    public void Save_AfterFailedLoad_DoesNotClobberQuarantine_AndClearsFailureState()
    {
        var path = Path.Combine(_tempRoot, "config.json");
        var store = new JsonConfigStore(path);
        store.Save(new Configuration { Groups = [new Group { Id = "g", Name = "G" }] });

        File.WriteAllText(path, "{ corrupt bytes");
        store.Load();
        store.LastLoadFailed.Should().BeTrue();

        store.Save(new Configuration { Groups = [new Group { Id = "g2", Name = "G2" }] });

        File.ReadAllText(path + ".bad").Should().Be("{ corrupt bytes",
            "the quarantined snapshot must survive the save that repairs config.json");
        store.LastLoadFailed.Should().BeFalse("a successful save makes the on-disk file valid again");
        store.Load().Groups.Single().Id.Should().Be("g2");
        store.LastLoadFailed.Should().BeFalse();
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
