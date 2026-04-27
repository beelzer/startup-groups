namespace StartupGroups.Core.Launch;

public sealed record DependencyEdge(string FromAppId, string ToAppId, int Confidence);

public sealed record DependencyHint(
    string GroupId,
    IReadOnlyList<string> CurrentOrder,
    IReadOnlyList<string> SuggestedOrder,
    IReadOnlyDictionary<string, string> AppIdToName,
    IReadOnlyList<DependencyEdge> Edges)
{
    public bool IsReorderSuggested =>
        CurrentOrder.Count == SuggestedOrder.Count
        && !CurrentOrder.SequenceEqual(SuggestedOrder);

    public bool HasEvidence => Edges.Count > 0;
}
