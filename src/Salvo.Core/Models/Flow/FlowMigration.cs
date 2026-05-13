namespace Salvo.Core.Models.Flow;

/// <summary>
/// One-way migration of the legacy <c>Group.Apps</c> list into the
/// equivalent <see cref="Node"/>+<see cref="Edge"/> graph.
///
/// Today's semantics (preserved exactly):
/// - Apps form waves. <c>DelayAfterSeconds &gt; 0</c> closes a wave.
/// - Apps inside a wave launch concurrently.
/// - The next wave waits for <c>max(time_until_all_ready, delay)</c>
///   after the previous wave's last app started.
///
/// Mapped to the graph:
/// - Each app in a wave becomes an <see cref="AppNode"/> with an edge
///   *from* the previous wave's terminator *to* it (parallel fan-out).
/// - If the wave closes with a delay, a <see cref="WaitNode"/> is
///   inserted after, with edges from every app in the wave to it
///   (parallel fan-in). The Wait becomes the next wave's terminator.
/// - The last wave (no closing delay) has no Wait; its apps are leaf
///   nodes of the graph.
/// </summary>
public static class FlowMigration
{
    /// <summary>
    /// True if the group has anything in the legacy Apps list and no
    /// existing graph nodes — i.e. it has not been migrated yet.
    /// </summary>
    public static bool NeedsMigration(Group group) =>
        group.Apps.Count > 0 && group.Nodes.Count == 0;

    /// <summary>
    /// Clear any existing graph and rebuild it from <see cref="Group.Apps"/>.
    /// Used on every save during the Apps-as-authoring-source transition
    /// (Phase A1) so the orchestrator's graph view stays consistent with
    /// list edits from the legacy UI. Once the graph-aware editor lands
    /// (Phase A2) this becomes the one-shot upgrade path instead.
    /// </summary>
    public static void Rebuild(Group group)
    {
        ArgumentNullException.ThrowIfNull(group);
        group.Nodes.Clear();
        group.Edges.Clear();
        Migrate(group);
    }

    /// <summary>
    /// Convert <paramref name="group"/> in place. Populates Nodes + Edges
    /// from the legacy Apps list. Leaves Apps populated so the existing
    /// list-based UI keeps working until the graph editor replaces it.
    /// </summary>
    public static void Migrate(Group group)
    {
        ArgumentNullException.ThrowIfNull(group);

        if (group.Apps.Count == 0)
        {
            return;
        }

        var nodes = group.Nodes;
        var edges = group.Edges;

        var start = new StartNode { Id = Guid.NewGuid().ToString() };
        nodes.Add(start);

        // The set of "current terminators" — nodes that the next wave's
        // app nodes should be edged from. Initially just the Start node;
        // after each Wait-closed wave it becomes [Wait]; for the final
        // (non-Wait) wave it stays as the prior wave's set so the graph
        // doesn't dangle.
        IReadOnlyList<Node> terminators = [start];

        var wave = new List<AppNode>();
        for (var i = 0; i < group.Apps.Count; i++)
        {
            var app = group.Apps[i];
            var isLast = i == group.Apps.Count - 1;
            var closesWave = app.DelayAfterSeconds > 0;

            var appNode = new AppNode
            {
                Id = Guid.NewGuid().ToString(),
                // Strip the delay — now expressed by the WaitNode below.
                App = CloneWithoutDelay(app),
            };
            nodes.Add(appNode);
            wave.Add(appNode);

            // Edge: every current terminator → this app node (fan-out).
            foreach (var term in terminators)
            {
                edges.Add(new Edge
                {
                    Id = Guid.NewGuid().ToString(),
                    From = term.Id,
                    To = appNode.Id,
                });
            }

            if (closesWave && !isLast)
            {
                var wait = new WaitNode
                {
                    Id = Guid.NewGuid().ToString(),
                    DurationSeconds = app.DelayAfterSeconds,
                };
                nodes.Add(wait);

                // Fan-in: every app in this wave → the Wait.
                foreach (var w in wave)
                {
                    edges.Add(new Edge
                    {
                        Id = Guid.NewGuid().ToString(),
                        From = w.Id,
                        To = wait.Id,
                    });
                }

                terminators = [wait];
                wave.Clear();
            }
            else if (closesWave)
            {
                // Last app closes a wave but there's nothing after — no
                // Wait needed; we just drop out of the loop. The wave's
                // apps are the leaves.
                terminators = wave.Cast<Node>().ToArray();
                wave.Clear();
            }
            else if (isLast)
            {
                // Last app, no closing delay: wave's apps are the leaves.
                terminators = wave.Cast<Node>().ToArray();
                wave.Clear();
            }
        }

    }

    private static AppEntry CloneWithoutDelay(AppEntry source) => new()
    {
        Name = source.Name,
        Kind = source.Kind,
        Path = source.Path,
        Service = source.Service,
        Args = source.Args,
        WorkingDirectory = source.WorkingDirectory,
        DelayAfterSeconds = 0,
        Enabled = source.Enabled,
    };
}
