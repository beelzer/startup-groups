using CommunityToolkit.Mvvm.ComponentModel;
using Salvo.Core.Models.Flow;

namespace Salvo.App.ViewModels.Flow;

/// <summary>
/// Base for every node card in the Simple view (and node body in the
/// future Flow view). Holds the stable graph identity plus the X/Y
/// position used only by the Flow view; the Simple view computes its
/// own layout topologically.
/// </summary>
public abstract partial class NodeViewModel : ObservableObject
{
    [ObservableProperty] private string _id = string.Empty;
    [ObservableProperty] private double _positionX;
    [ObservableProperty] private double _positionY;

    /// <summary>
    /// True while this node is the one currently executing during a
    /// group launch. Drives the highlight pulse on the card. Set by the
    /// orchestrator-side run overlay in a future phase; the property
    /// lives here so the bindings already exist.
    /// </summary>
    [ObservableProperty] private bool _isExecuting;

    /// <summary>
    /// True once this node has finished a launch in the current run.
    /// Drives the green-tick adornment.
    /// </summary>
    [ObservableProperty] private bool _hasCompleted;

    /// <summary>
    /// True while a drag is hovering over this row's center area —
    /// dropping here merges the dragged node into this row's stage as
    /// a parallel sibling. Drives the row's "drop to merge" highlight.
    /// </summary>
    [ObservableProperty] private bool _isMergeTarget;

    public abstract string Kind { get; }

    public abstract Node ToModel();

    public static NodeViewModel FromModel(Node node) => node switch
    {
        StartNode n => new StartNodeViewModel { Id = n.Id, PositionX = n.Position.X, PositionY = n.Position.Y },
        AppNode n => new AppNodeViewModel
        {
            Id = n.Id, PositionX = n.Position.X, PositionY = n.Position.Y,
            App = AppEntryViewModel.FromModel(n.App),
        },
        WaitNode n => new WaitNodeViewModel
        {
            Id = n.Id, PositionX = n.Position.X, PositionY = n.Position.Y,
            DurationSeconds = n.DurationSeconds,
        },
        IfElseNode n => new IfElseNodeViewModel
        {
            Id = n.Id, PositionX = n.Position.X, PositionY = n.Position.Y,
            Condition = FlowConditionViewModel.FromModel(n.Condition),
        },
        ServiceStartNode n => new ServiceStartNodeViewModel
        {
            Id = n.Id, PositionX = n.Position.X, PositionY = n.Position.Y,
            ServiceName = n.ServiceName,
        },
        ServiceStopNode n => new ServiceStopNodeViewModel
        {
            Id = n.Id, PositionX = n.Position.X, PositionY = n.Position.Y,
            ServiceName = n.ServiceName,
        },
        RunCommandNode n => new RunCommandNodeViewModel
        {
            Id = n.Id, PositionX = n.Position.X, PositionY = n.Position.Y,
            Command = n.Command, Interpreter = n.Interpreter, WorkingDirectory = n.WorkingDirectory,
        },
        GroupCallNode n => new GroupCallNodeViewModel
        {
            Id = n.Id, PositionX = n.Position.X, PositionY = n.Position.Y,
            GroupId = n.GroupId,
        },
        _ => throw new ArgumentException($"Unknown node type {node.GetType().Name}", nameof(node)),
    };

    protected NodePosition CurrentPosition() => new() { X = PositionX, Y = PositionY };
}

public sealed partial class StartNodeViewModel : NodeViewModel
{
    public override string Kind => "Start";
    public override Node ToModel() => new StartNode { Id = Id, Position = CurrentPosition() };
}

public sealed partial class AppNodeViewModel : NodeViewModel
{
    public AppEntryViewModel App { get; set; } = new();

    public override string Kind => "App";
    public override Node ToModel() => new AppNode { Id = Id, Position = CurrentPosition(), App = App.ToModel() };
}

public sealed partial class WaitNodeViewModel : NodeViewModel
{
    [ObservableProperty] private int _durationSeconds;

    public override string Kind => "Wait";
    public override Node ToModel() => new WaitNode { Id = Id, Position = CurrentPosition(), DurationSeconds = DurationSeconds };
}

public sealed partial class IfElseNodeViewModel : NodeViewModel
{
    [ObservableProperty] private FlowConditionViewModel _condition = new ServiceRunningConditionViewModel();

    public override string Kind => "IfElse";
    public override Node ToModel() => new IfElseNode { Id = Id, Position = CurrentPosition(), Condition = Condition.ToModel() };
}

public sealed partial class ServiceStartNodeViewModel : NodeViewModel
{
    [ObservableProperty] private string _serviceName = string.Empty;

    public override string Kind => "ServiceStart";
    public override Node ToModel() => new ServiceStartNode { Id = Id, Position = CurrentPosition(), ServiceName = ServiceName };
}

public sealed partial class ServiceStopNodeViewModel : NodeViewModel
{
    [ObservableProperty] private string _serviceName = string.Empty;

    public override string Kind => "ServiceStop";
    public override Node ToModel() => new ServiceStopNode { Id = Id, Position = CurrentPosition(), ServiceName = ServiceName };
}

public sealed partial class RunCommandNodeViewModel : NodeViewModel
{
    [ObservableProperty] private string _command = string.Empty;
    [ObservableProperty] private string _interpreter = "shell";
    [ObservableProperty] private string? _workingDirectory;

    public override string Kind => "RunCommand";
    public override Node ToModel() => new RunCommandNode
    {
        Id = Id, Position = CurrentPosition(),
        Command = Command, Interpreter = Interpreter, WorkingDirectory = WorkingDirectory,
    };
}

public sealed partial class GroupCallNodeViewModel : NodeViewModel
{
    [ObservableProperty] private string _groupId = string.Empty;

    public override string Kind => "GroupCall";
    public override Node ToModel() => new GroupCallNode { Id = Id, Position = CurrentPosition(), GroupId = GroupId };
}
