using Microsoft.Extensions.Logging;
using StartupGroups.Core.Models;

namespace StartupGroups.Core.Launch;

public sealed record ProbeContext(
    LaunchSession Session,
    AppEntry App,
    string? ResolvedPath,
    ILogger Logger);
