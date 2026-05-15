using Salvo.Core.Models;
using Salvo.Core.Models.Flow;

namespace Salvo.Core.Tests;

/// <summary>
/// Locks in the wave-list → graph mapping. The legacy
/// <c>DelayAfterSeconds</c> semantics — apps within a wave run in
/// parallel, a wave closes when <c>DelayAfterSeconds &gt; 0</c>, next wave
/// waits for all of the previous wave to complete — must be preserved
/// exactly by the topology produced here.
/// </summary>
public sealed class FlowMigrationTests
{
    [Fact]
    public void NeedsMigration_True_WhenAppsPresent_AndGraphEmpty()
    {
        var group = new Group { Apps = [new AppEntry { Name = "one" }] };
        FlowMigration.NeedsMigration(group).Should().BeTrue();
    }

    [Fact]
    public void NeedsMigration_False_WhenGraphAlreadyExists()
    {
        var group = new Group
        {
            Apps = [new AppEntry { Name = "one" }],
            Nodes = [new StartNode { Id = "s" }],
        };
        FlowMigration.NeedsMigration(group).Should().BeFalse();
    }

    [Fact]
    public void Migrate_SingleApp_ProducesStartToApp()
    {
        var group = new Group
        {
            Apps = [new AppEntry { Name = "one", Path = "C:\\one.exe" }]
        };

        FlowMigration.Migrate(group);

        group.Nodes.Should().HaveCount(2); // Start + AppNode
        group.Nodes.OfType<StartNode>().Should().ContainSingle();
        var appNode = group.Nodes.OfType<AppNode>().Single();
        appNode.App.Name.Should().Be("one");
        group.Edges.Should().ContainSingle();
        group.Edges[0].From.Should().Be(group.Nodes.OfType<StartNode>().Single().Id);
        group.Edges[0].To.Should().Be(appNode.Id);
    }

    [Fact]
    public void Migrate_TwoAppsInOneWave_FanOutFromStart()
    {
        var group = new Group
        {
            Apps =
            [
                new AppEntry { Name = "one" },
                new AppEntry { Name = "two" },
            ]
        };

        FlowMigration.Migrate(group);

        var start = group.Nodes.OfType<StartNode>().Single();
        var apps = group.Nodes.OfType<AppNode>().ToList();
        apps.Should().HaveCount(2);

        // Both apps edged from Start (parallel fan-out).
        group.Edges.Should().HaveCount(2);
        group.Edges.Should().OnlyContain(e => e.From == start.Id);
        group.Edges.Select(e => e.To).Should().BeEquivalentTo(apps.Select(a => a.Id));
    }

    [Fact]
    public void Migrate_TwoWaves_InsertWaitWithFanIn_FanOut()
    {
        var group = new Group
        {
            Apps =
            [
                new AppEntry { Name = "one", DelayAfterSeconds = 0 },
                new AppEntry { Name = "two", DelayAfterSeconds = 5 }, // closes wave 1
                new AppEntry { Name = "three", DelayAfterSeconds = 0 },
            ]
        };

        FlowMigration.Migrate(group);

        var start = group.Nodes.OfType<StartNode>().Single();
        var apps = group.Nodes.OfType<AppNode>().ToList();
        var wait = group.Nodes.OfType<WaitNode>().Single();

        apps.Should().HaveCount(3);
        wait.DurationSeconds.Should().Be(5);

        // Wave 1: Start fans out to "one" and "two".
        var one = apps.Single(a => a.App.Name == "one");
        var two = apps.Single(a => a.App.Name == "two");
        var three = apps.Single(a => a.App.Name == "three");

        group.Edges.Should().Contain(e => e.From == start.Id && e.To == one.Id);
        group.Edges.Should().Contain(e => e.From == start.Id && e.To == two.Id);
        // Fan-in: both → Wait.
        group.Edges.Should().Contain(e => e.From == one.Id && e.To == wait.Id);
        group.Edges.Should().Contain(e => e.From == two.Id && e.To == wait.Id);
        // Wait fans out to wave 2.
        group.Edges.Should().Contain(e => e.From == wait.Id && e.To == three.Id);
    }

    [Fact]
    public void Migrate_StripsDelayAfterSeconds_FromAppNode()
    {
        var group = new Group
        {
            Apps = [new AppEntry { Name = "one", DelayAfterSeconds = 10 }]
        };

        FlowMigration.Migrate(group);

        // Delay is now expressed by the graph (would be a Wait if not last).
        group.Nodes.OfType<AppNode>().Single().App.DelayAfterSeconds.Should().Be(0);
    }

    [Fact]
    public void Rebuild_DiscardsExistingGraph_AndRebuildsFromApps()
    {
        var group = new Group
        {
            Apps = [new AppEntry { Name = "fresh" }],
            // Pretend a stale graph from a previous migration exists.
            Nodes = [new StartNode { Id = "stale" }, new AppNode { Id = "stale-app" }],
            Edges = [new Edge { Id = "stale-edge", From = "stale", To = "stale-app" }],
        };

        FlowMigration.Rebuild(group);

        group.Nodes.Should().NotContain(n => n.Id == "stale");
        group.Nodes.OfType<AppNode>().Single().App.Name.Should().Be("fresh");
    }
}
