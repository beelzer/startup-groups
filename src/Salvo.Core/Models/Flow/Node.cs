using System.Text.Json.Serialization;

namespace Salvo.Core.Models.Flow;

/// <summary>
/// Base for every node in a group's flow graph. The JSON discriminator
/// "type" picks the concrete subclass on deserialize.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(StartNode), "start")]
[JsonDerivedType(typeof(AppNode), "app")]
[JsonDerivedType(typeof(WaitNode), "wait")]
[JsonDerivedType(typeof(IfElseNode), "ifElse")]
public abstract class Node
{
    public string Id { get; set; } = string.Empty;

    public NodePosition Position { get; set; } = new();
}

/// <summary>
/// Position in the Flow (graph) view. Ignored by the Simple view, which
/// computes its own layout topologically. Defaults to (0,0) for new nodes;
/// the Flow view auto-arranges on first open and persists user moves.
/// </summary>
public sealed class NodePosition
{
    public double X { get; set; }

    public double Y { get; set; }
}

/// <summary>
/// Implicit graph entry. Every group has exactly one Start node;
/// auto-created during migration / new-group setup; cannot be deleted.
/// </summary>
public sealed class StartNode : Node
{
}

/// <summary>
/// Launch (or stop) an app/service. Embeds the full <see cref="AppEntry"/>
/// inline so nodes are self-contained — no shared lookup table to keep
/// in sync. <see cref="AppEntry.DelayAfterSeconds"/> is unused here; the
/// graph expresses delay as explicit <see cref="WaitNode"/> nodes.
/// </summary>
public sealed class AppNode : Node
{
    public AppEntry App { get; set; } = new();
}

/// <summary>
/// Hold the flow for a fixed duration before firing outgoing edges. A
/// Wait with multiple incoming edges acts as a barrier — it waits for all
/// upstream nodes to complete, *then* waits the duration.
/// </summary>
public sealed class WaitNode : Node
{
    public int DurationSeconds { get; set; }
}

/// <summary>
/// Branch on a runtime condition. Has two outgoing edges, labeled
/// "then" and "else". Both branches typically reconverge at a downstream
/// node; if not, the unselected branch's subgraph never runs.
/// </summary>
public sealed class IfElseNode : Node
{
    public FlowCondition Condition { get; set; } = new ServiceRunningCondition();
}
