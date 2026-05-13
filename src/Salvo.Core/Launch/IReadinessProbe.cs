using System.Runtime.Versioning;

namespace Salvo.Core.Launch;

[SupportedOSPlatform("windows")]
public interface IReadinessProbe
{
    ReadinessSignal Signal { get; }

    bool AppliesTo(ProbeContext context);

    Task<bool> RunAsync(ProbeContext context, CancellationToken cancellationToken);
}
