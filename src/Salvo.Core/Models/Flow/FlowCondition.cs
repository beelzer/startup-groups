using System.Text.Json.Serialization;

namespace Salvo.Core.Models.Flow;

/// <summary>
/// The condition-kind discriminator vocabulary: the JSON "type" values below,
/// the editor view-models' Kind overrides, and the condition-kind picker all
/// speak these strings. One set of consts on the C# side means a typo can't
/// silently fall through a switch (the same hazard NodeKinds was introduced
/// to kill). The XAML picker Tags mirror them as literals.
/// </summary>
public static class ConditionKinds
{
    public const string ServiceRunning = "serviceRunning";
    public const string FileExists = "fileExists";
    public const string ProcessRunning = "processRunning";
}

/// <summary>
/// Condition expression evaluated by <see cref="IfElseNode"/> at runtime.
/// Discriminator "type" picks the subclass. Phase A ships the structured
/// predicates below; a custom-script condition can be added later as a
/// power-user escape hatch.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ServiceRunningCondition), ConditionKinds.ServiceRunning)]
[JsonDerivedType(typeof(FileExistsCondition), ConditionKinds.FileExists)]
[JsonDerivedType(typeof(ProcessRunningCondition), ConditionKinds.ProcessRunning)]
public abstract class FlowCondition
{
}

public sealed class ServiceRunningCondition : FlowCondition
{
    public string ServiceName { get; set; } = string.Empty;
}

public sealed class FileExistsCondition : FlowCondition
{
    public string Path { get; set; } = string.Empty;
}

public sealed class ProcessRunningCondition : FlowCondition
{
    public string ProcessName { get; set; } = string.Empty;
}
