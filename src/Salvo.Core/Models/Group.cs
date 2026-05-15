using Salvo.Core.Models.Flow;

namespace Salvo.Core.Models;

public sealed class Group
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Icon { get; set; } = "Apps24";

    /// <summary>
    /// Flow graph that defines what the group launches and in what order.
    /// Authoritative; the Simple and Flow views both read/write here.
    /// </summary>
    public List<Node> Nodes { get; set; } = [];

    /// <summary>
    /// Edges between <see cref="Nodes"/>. See <see cref="Edge"/> for the
    /// execution semantics (fan-out on outgoing, fan-in on incoming).
    /// </summary>
    public List<Edge> Edges { get; set; } = [];

    /// <summary>
    /// Legacy pre-graph storage. Read on first load to seed
    /// <see cref="Nodes"/>/<see cref="Edges"/> via <c>FlowMigration</c>,
    /// then cleared. Kept on the type so old configs deserialize
    /// without dropping data.
    /// </summary>
    public List<AppEntry> Apps { get; set; } = [];
}
