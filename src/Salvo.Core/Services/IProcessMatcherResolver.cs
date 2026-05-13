using Salvo.Core.Models;

namespace Salvo.Core.Services;

public interface IProcessMatcherResolver
{
    IReadOnlyList<ProcessMatcher> GetMatchers(AppEntry app);
}
