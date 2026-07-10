using CommunityToolkit.Mvvm.ComponentModel;
using Salvo.Core.Models.Flow;

namespace Salvo.App.ViewModels.Flow;

/// <summary>
/// Editor-side counterpart of <see cref="FlowCondition"/>. The condition
/// editor in the IfElse node card swaps between subtypes via a picker
/// (Phase A2 ships the three structured predicates below; custom-script
/// arrives later).
/// </summary>
public abstract partial class FlowConditionViewModel : ObservableObject
{
    public abstract string DisplayName { get; }

    /// <summary>Stable discriminator matching the model's "type" ("serviceRunning", etc.).</summary>
    public abstract string Kind { get; }

    /// <summary>
    /// The single free-text operand of this condition, projected onto the
    /// concrete subtype's field (service name / path / process name). Lets
    /// the If card bind one value box regardless of the condition kind.
    /// </summary>
    public abstract string Value { get; set; }

    /// <summary>Prompt shown in the value box for this condition kind.</summary>
    public abstract string ValueLabel { get; }

    public abstract FlowCondition ToModel();

    public static FlowConditionViewModel FromModel(FlowCondition cond) => cond switch
    {
        ServiceRunningCondition c => new ServiceRunningConditionViewModel { ServiceName = c.ServiceName },
        FileExistsCondition c => new FileExistsConditionViewModel { Path = c.Path },
        ProcessRunningCondition c => new ProcessRunningConditionViewModel { ProcessName = c.ProcessName },
        _ => throw new ArgumentException($"Unknown condition type {cond.GetType().Name}", nameof(cond)),
    };
}

public sealed partial class ServiceRunningConditionViewModel : FlowConditionViewModel
{
    [ObservableProperty][NotifyPropertyChangedFor(nameof(Value))] private string _serviceName = string.Empty;
    public override string DisplayName => "Service running";
    public override string Kind => ConditionKinds.ServiceRunning;
    public override string ValueLabel => "Service name";
    public override string Value { get => ServiceName; set => ServiceName = value; }
    public override FlowCondition ToModel() => new ServiceRunningCondition { ServiceName = ServiceName };
}

public sealed partial class FileExistsConditionViewModel : FlowConditionViewModel
{
    [ObservableProperty][NotifyPropertyChangedFor(nameof(Value))] private string _path = string.Empty;
    public override string DisplayName => "File exists";
    public override string Kind => ConditionKinds.FileExists;
    public override string ValueLabel => "File or folder path";
    public override string Value { get => Path; set => Path = value; }
    public override FlowCondition ToModel() => new FileExistsCondition { Path = Path };
}

public sealed partial class ProcessRunningConditionViewModel : FlowConditionViewModel
{
    [ObservableProperty][NotifyPropertyChangedFor(nameof(Value))] private string _processName = string.Empty;
    public override string DisplayName => "Process running";
    public override string Kind => ConditionKinds.ProcessRunning;
    public override string ValueLabel => "Process name (e.g. chrome)";
    public override string Value { get => ProcessName; set => ProcessName = value; }
    public override FlowCondition ToModel() => new ProcessRunningCondition { ProcessName = ProcessName };
}
