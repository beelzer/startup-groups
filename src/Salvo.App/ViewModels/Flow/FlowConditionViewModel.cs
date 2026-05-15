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
    public abstract FlowCondition ToModel();

    public static FlowConditionViewModel FromModel(FlowCondition cond) => cond switch
    {
        ServiceRunningCondition c => new ServiceRunningConditionViewModel { ServiceName = c.ServiceName },
        FileExistsCondition c => new FileExistsConditionViewModel { Path = c.Path },
        ProcessRunningCondition c => new ProcessRunningConditionViewModel { ProcessName = c.ProcessName },
        _ => new ServiceRunningConditionViewModel(),
    };
}

public sealed partial class ServiceRunningConditionViewModel : FlowConditionViewModel
{
    [ObservableProperty] private string _serviceName = string.Empty;
    public override string DisplayName => "Service running";
    public override FlowCondition ToModel() => new ServiceRunningCondition { ServiceName = ServiceName };
}

public sealed partial class FileExistsConditionViewModel : FlowConditionViewModel
{
    [ObservableProperty] private string _path = string.Empty;
    public override string DisplayName => "File exists";
    public override FlowCondition ToModel() => new FileExistsCondition { Path = Path };
}

public sealed partial class ProcessRunningConditionViewModel : FlowConditionViewModel
{
    [ObservableProperty] private string _processName = string.Empty;
    public override string DisplayName => "Process running";
    public override FlowCondition ToModel() => new ProcessRunningCondition { ProcessName = ProcessName };
}
