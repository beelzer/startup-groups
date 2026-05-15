using System.Text.Json.Serialization;

namespace Salvo.Core.Models.Flow;

/// <summary>
/// Condition expression evaluated by <see cref="IfElseNode"/> at runtime.
/// Discriminator "type" picks the subclass. Phase A ships the structured
/// predicates below; a custom-script condition can be added later as a
/// power-user escape hatch.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ServiceRunningCondition), "serviceRunning")]
[JsonDerivedType(typeof(FileExistsCondition), "fileExists")]
[JsonDerivedType(typeof(ProcessRunningCondition), "processRunning")]
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
