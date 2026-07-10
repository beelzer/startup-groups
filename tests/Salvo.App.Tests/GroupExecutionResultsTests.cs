using Salvo.App.ViewModels;
using Salvo.App.ViewModels.Flow;
using Salvo.Core.Models;
using Salvo.Core.Models.Flow;

namespace Salvo.App.Tests;

/// <summary>
/// Pins two group-execution regressions: launch/stop results must map back to
/// app rows by identity (they arrive in completion order and include non-app
/// entries), and a freshly created group must be born with a Start node or
/// everything added to it is an orphan the orchestrator skips.
/// </summary>
public sealed class GroupExecutionResultsTests
{
    [Fact]
    public void ApplyResults_MatchesByAppIdentity_NotByPosition()
    {
        var alpha = new AppEntry { Name = "alpha", Path = @"C:\apps\alpha.exe" };
        var beta = new AppEntry { Name = "beta", Path = @"C:\apps\beta.exe" };
        var group = GroupViewModel.FromModel(new Group
        {
            Id = "g",
            Name = "g",
            Nodes =
            [
                new StartNode { Id = "s" },
                new AppNode { Id = "n1", App = alpha },
                new AppNode { Id = "n2", App = beta },
            ],
            Edges =
            [
                new Edge { Id = "e1", From = "s", To = "n1" },
                new Edge { Id = "e2", From = "s", To = "n2" },
            ],
        });

        // Completion order reversed relative to row order, with a non-app
        // (service) result interleaved — exactly what parallel execution and
        // action nodes produce.
        var results = new List<OperationResult>
        {
            OperationResult.Success("beta done", beta),
            OperationResult.Success("svc done", new AppEntry { Name = "SomeService", Kind = AppKind.Service, Service = "SomeService" }),
            OperationResult.Failed("alpha failed", alpha),
        };

        MainWindowViewModel.ApplyResults(group, results);

        group.Apps.Single(a => a.Name == "alpha").LastStatus.Should().Be("alpha failed");
        group.Apps.Single(a => a.Name == "beta").LastStatus.Should().Be("beta done");
    }

    [Fact]
    public void ApplyResults_IgnoresResultsWithoutASource()
    {
        var alpha = new AppEntry { Name = "alpha", Path = @"C:\apps\alpha.exe" };
        var group = GroupViewModel.FromModel(new Group
        {
            Id = "g",
            Name = "g",
            Nodes = [new StartNode { Id = "s" }, new AppNode { Id = "n1", App = alpha }],
            Edges = [new Edge { Id = "e1", From = "s", To = "n1" }],
        });
        group.Apps[0].LastStatus = "untouched";

        MainWindowViewModel.ApplyResults(group, [OperationResult.Failed("no source")]);

        group.Apps[0].LastStatus.Should().Be("untouched");
    }

    [Fact]
    public void FromModel_EmptyGroup_IsBornWithAStartNode_AndPersistsIt()
    {
        // AddGroup routes through FromModel precisely for this seeding — a
        // group without a Start node executes nothing, forever.
        var vm = GroupViewModel.FromModel(new Group { Id = "new", Name = "New" });

        vm.Graph.Nodes.Should().ContainSingle(n => n is StartNodeViewModel);

        var persisted = vm.ToModel();
        persisted.Nodes.Should().ContainSingle(n => n is StartNode);
    }
}
