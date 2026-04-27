using StartupGroups.Core.Models;

namespace StartupGroups.Core.Services;

public interface IProcessMatcherResolver
{
    IReadOnlyList<ProcessMatcher> GetMatchers(AppEntry app);
}
