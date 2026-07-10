using System.Runtime.Versioning;

namespace Salvo.Core.Launch;

/// <summary>
/// How a readiness probe run resolved. Distinguishing "never fired"
/// from "definitively can't fire" lets the detector stop waiting when
/// every probe has proven the launch failed (e.g. a service that does
/// not exist) instead of polling out the whole readiness timeout.
/// </summary>
public enum ProbeOutcome
{
    /// <summary>The readiness signal was observed.</summary>
    Fired,

    /// <summary>
    /// The probe stopped without an answer (cancelled / timed out);
    /// the app may still become ready by other signals.
    /// </summary>
    GaveUp,

    /// <summary>
    /// The probe determined readiness can never fire for this launch
    /// (definitive failure, not mere absence of signal).
    /// </summary>
    Failed,
}

[SupportedOSPlatform("windows")]
public interface IReadinessProbe
{
    ReadinessSignal Signal { get; }

    bool AppliesTo(ProbeContext context);

    Task<ProbeOutcome> RunAsync(ProbeContext context, CancellationToken cancellationToken);
}
