namespace Salvo.Core.Models.Flow;

/// <summary>
/// Directed edge between two nodes in the flow graph.
///
/// Execution semantics:
/// - A node runs when *all* of its incoming edges have fired.
/// - When a node finishes, *all* of its outgoing edges fire in parallel.
///
/// So a node with N outgoing edges is a parallel split; a node with N
/// incoming edges is a parallel join. No explicit Split/Join node types
/// are needed — the topology *is* the semantics.
///
/// <see cref="Label"/> is set only for edges leaving an
/// <see cref="IfElseNode"/> (values: "then" or "else"). Other edges
/// leave it null.
/// </summary>
public sealed class Edge
{
    public string Id { get; set; } = string.Empty;

    public string From { get; set; } = string.Empty;

    public string To { get; set; } = string.Empty;

    public string? Label { get; set; }
}
