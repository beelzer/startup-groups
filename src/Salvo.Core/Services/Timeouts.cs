namespace Salvo.Core.Services;

public static class Timeouts
{
    // Service-orchestrator IPC
    public static readonly TimeSpan OrchestratorServiceOperation = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan ElevatorServiceOperation = TimeSpan.FromSeconds(25);

    // RunCommand graph node: cap on a synchronous command step before it is
    // killed as timed out. Long-running commands belong as background
    // side-effects, not graph steps.
    public static readonly TimeSpan OrchestratorRunCommand = TimeSpan.FromSeconds(60);

    // Settings persistence
    public static readonly TimeSpan ConfigPersistDebounce = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan ConfigPersistCooldown = TimeSpan.FromMilliseconds(100);

    // Host lifecycle
    public static readonly TimeSpan HostShutdownGrace = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan StatusRefreshInterval = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan AutoStartDelay = TimeSpan.FromSeconds(5);
    // How long a deliberate relaunch (elevation, post-update restart) waits
    // for its predecessor to exit and release the single-instance mutex.
    public static readonly TimeSpan SingleInstanceHandoffWait = TimeSpan.FromSeconds(10);

    // Process control
    public static readonly TimeSpan ProcessKillGrace = TimeSpan.FromSeconds(5);

    // Readiness probes
    public static readonly TimeSpan MainWindowResponsivenessProbe = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan ProbePollDefault = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan ProbePollService = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan ProbePollActivity = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan WaitForInputIdlePerAttempt = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan ActivityQuietWindow = TimeSpan.FromMilliseconds(1500);

    // Readiness detector early-exit watcher
    public static readonly TimeSpan ReadinessEarlyExitGrace = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan ReadinessEarlyExitPoll = TimeSpan.FromMilliseconds(250);

    // Launch telemetry
    public static readonly TimeSpan ReadinessDefault = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan PidResolveDeadline = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan PidResolvePoll = TimeSpan.FromMilliseconds(200);
}
