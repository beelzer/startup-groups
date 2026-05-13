using Microsoft.Extensions.Logging;
using Salvo.Core.Models;

namespace Salvo.Core.Launch;

public sealed record ProbeContext(
    LaunchSession Session,
    AppEntry App,
    string? ResolvedPath,
    ILogger Logger);
