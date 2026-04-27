using StartupGroups.Core.Models;

namespace StartupGroups.Core.Services;

public interface IProcessInspector
{
    bool IsRunning(IReadOnlyList<ProcessMatcher> matchers);

    bool TryKill(IReadOnlyList<ProcessMatcher> matchers, out string message);

    IReadOnlyList<int> FindMatchingPids(IReadOnlyList<ProcessMatcher> matchers);
}
