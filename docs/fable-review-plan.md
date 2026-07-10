# Salvo — Fable Review: Execution Plan

> **Self-contained, execute-from-this-file plan.** Generated 2026-07-11 from a six-reviewer fresh review of the
> post-optimization-sweep codebase (branch `chore/optimization-sweep`, all 18 sweep workstreams landed).
> Every finding was verified against the real source by the reviewing agent before inclusion. Deduplicated
> against the 105 findings in `docs/optimization-sweep-plan.md` — nothing here re-reports those, and nothing
> violates that plan's "Deliberately NOT doing" list (carried forward at the bottom).

## How to execute this plan

1. **Work top-to-bottom in the numbered order** (correctness → data-loss → mechanical passes → structural refactor → docs/dead-code last).
2. **One workstream = one commit.** Build + test after each so every checkpoint is green and revertible:
   `dotnet build Salvo.slnx -c Debug` then `dotnet test Salvo.slnx`.
3. **Add the tests called for in a workstream as part of that workstream.**
4. **Respect the "Deliberately NOT doing" list at the bottom.**
5. Check off `[ ]` → `[x]` as each workstream lands.

**Scope:** ~20.6k LOC · **19 workstreams** · 62 findings (11 high, 26 medium, 25 low).
Review areas: launch pipeline, core services/elevation, App ViewModels, flow editor, app shell, build/CI/tests/docs.

---

## Workstreams (in execution order)

### 01. [P0] MSIX packaging & release-gate correctness  `[ ]`

- **Priority:** P0 · **Effort:** S · **Risk:** low · **Category:** build/ci correctness

**Why:** Three latent failures converge on the first real signed release: the MSIX package ships without the
Elevator (breaking every elevated operation for MSIX installs), the secret-gated signing/Store/winget steps use
an `if:` pattern that evaluates false forever (so the signing path silently no-ops and an **unsigned** package
gets published to the `.appinstaller` channel), and the winget submission runs while the release is still a draft.

**Findings:**

- **[high][build]** `installer/Msix/build.ps1:112-118` — the package publishes only `Salvo.App.csproj`; the App's
  `CopyElevatorOutput` target copies into *build* output which does not flow into `dotnet publish` output.
  `ElevationPaths.ResolveElevatorPath` then points at a nonexistent sidecar inside the package. The Velopack paths
  (ci.yml:116-121, release.yml:64-69) publish the Elevator explicitly — build.ps1 never does.
  **Fix:** add a second `dotnet publish src/Salvo.Elevator ... -o $StageDir` (excluding pdb/xml) before packing.
- **[high][ci]** `.github/workflows/release.yml:135,212,242` — steps gate on `env.X != ''` where X is defined in
  that same step's `env:` block; GitHub requires job-level env for secret-derived conditionals. When the cert
  secret lands, the decode step skips, and the sideload build prints "Building signed MSIX..." while passing
  `-CertPath ''` — build.ps1:149 (`if ($CertPath)`) then quietly produces an unsigned package.
  **Fix:** hoist `HAS_SIGNING_CERT: ${{ secrets.MSIX_SIGNING_CERT_BASE64 != '' }}` (and equivalents for Store/winget)
  to job-level `env:` and gate the steps on that; make build.ps1 **fail** if `-CertPassword` is supplied without a
  usable `-CertPath`.
- **[medium][ci]** `.github/workflows/release.yml:241-250` — winget-releaser runs before the draft→published flip
  (last step); drafts are invisible to the API and asset URLs aren't public yet.
  **Fix:** move "Submit to winget" after "Publish release with auto-generated notes".
- **[medium][ci]** `.github/workflows/release.yml:97` — "Remove any existing draft" lists releases without
  `--paginate`; past ~30 releases the release-drafter draft sinks below page 1 and `vpk upload` hard-fails
  (ci.yml:95 already paginates correctly). **Fix:** add `--paginate`.

**Files:** `installer/Msix/build.ps1`, `.github/workflows/release.yml`.

---

### 02. [P0] App lifecycle: single-instance, window lifetime, `--tray`, early logging  `[ ]`

- **Priority:** P0 · **Effort:** M · **Risk:** low-medium · **Category:** correctness

**Why:** A tray-resident launcher's routine failure modes: second launches create duplicate tray icons and
last-writer-wins config/settings; closing the window with minimize-to-tray off permanently strands the app
(tray click throws on a closed window); auto-start pops the main window on every logon despite the task's
explicit `--tray` intent; and the two earliest startup helpers log into a not-yet-configured Serilog.

**Findings:**

- **[high][bug]** `src/Salvo.App/Program.cs:12` — no single-instance handling anywhere (grep: no Mutex/EventWaitHandle
  in src/). **Fix:** named mutex acquired in `Main`; if already held, signal the first instance (named
  `EventWaitHandle`) to show its main window, then exit. First instance listens and marshals to the dispatcher.
- **[high][bug]** `src/Salvo.App/App.xaml.cs:332` — `MainWindow` is `AddSingleton`, but `TrayViewModel.ShowMainWindow`
  (TrayViewModel.cs:121-129) is written to recreate it after close; with `MinimizeToTrayOnClose` off, re-resolving
  the same closed window makes `Show()` throw `InvalidOperationException` and the window is unreachable until
  restart (`OnClosedUnwatch` has also detached the VM). **Fix:** `services.AddTransient<MainWindow>()`.
- **[medium][bug]** `src/Salvo.App/Services/TaskSchedulerAutoStartService.cs:48` — the logon task passes
  `AppIdentifiers.TrayCommandLineFlag` (`"--tray"`) but `OnStartup` never parses it, and `ShowMainWindowOnLaunch`
  defaults true → window pops on every logon. **Fix:** `--tray` suppresses the main window in the
  `shouldShowOnLaunch` computation.
- **[medium][bug]** `src/Salvo.App/App.xaml.cs:44-53` — `TrySetAppUserModelId()` and
  `TryRelaunchAsAdminIfConfigured()` run before `Log.Logger` is configured, so their `Log.Warning` failure paths
  vanish into the silent default logger. **Fix:** move the Serilog configuration block to the top of `OnStartup`.

**Tests:** single-instance mutex naming/contention unit test where feasible; manual verify of tray-reshow after close.

**Files:** `src/Salvo.App/Program.cs`, `src/Salvo.App/App.xaml.cs`, `src/Salvo.App/ViewModels/TrayViewModel.cs`,
`src/Salvo.App/Services/TaskSchedulerAutoStartService.cs`.

---

### 03. [P0] Orchestrator & group execution correctness  `[ ]`

- **Priority:** P0 · **Effort:** M · **Risk:** medium · **Category:** correctness

**Why:** Four independent defects make group execution wrong or janky: launch/stop block the WPF dispatcher for
up to ~20s, results are matched to app rows by index when they arrive in completion order, groups created
in-session never get a Start node (and persist permanently broken), and sequential GroupCalls to the same group
are dropped as "recursive".

**Findings:**

- **[high][bug]** `src/Salvo.Core/Services/AppOrchestrator.cs:191` — `StopGroupAsync` is fully synchronous behind
  `Task.FromResult` (up to ~20s in `WaitForStatus`, 5s per killed process) and is awaited directly on the
  dispatcher (MainWindowViewModel.cs:794); launch-node bodies likewise run synchronously until their first await,
  serializing the "parallel" fan-out on the UI thread. **Fix:** wrap graph execution and the stop loop in
  `Task.Run` inside the orchestrator (contract is thread-agnostic).
- **[high][bug]** `src/Salvo.App/ViewModels/MainWindowViewModel.cs:851-857` — `AddGroup` builds a `GroupViewModel`
  with an empty graph and never seeds a Start node (both *load* paths do); first `AddNode`/`AppendAppNode` finds no
  leaves and adds the node **edgeless**; `ExecuteGraphAsync` skips zero-`firedIn` non-Start roots, so the group is
  a silent no-op — permanently, once persisted (`Apps` stays empty so `FlowMigration.Rebuild` never rescues it).
  **Fix:** seed `StartNodeViewModel` in `AddGroup` (or the `GroupViewModel` ctor) + defensive Start-seed on load of
  a node-list lacking one. **Test:** create-group → add app → execute reaches the app.
- **[high][bug]** `src/Salvo.App/ViewModels/MainWindowViewModel.cs:1326-1332` — `ApplyResults` maps
  `group.Apps[i].LastStatus = results[i].Message`, but results are appended in **completion order** under a lock and
  include ServiceStart/ServiceStop/RunCommand/GroupCall entries; `StopGroupAsync` also iterates branch apps while
  `GroupViewModel.Apps` holds only outer nodes. **Fix:** match on `OperationResult.Source` via
  `AppIdentity.ComputeAppId(result.Source?.Path, result.Source?.Name)` against `app.ComputedAppId`. **Test:** pin it.
- **[medium][bug]** `src/Salvo.Core/Services/AppOrchestrator.cs:241` — `callChain` entries are never removed, so a
  boot sequence calling Group A, waiting, then calling A again drops the second call as "recursive"; the shared
  `HashSet` is also mutated from parallel thread-pool continuations unsynchronized. **Fix:**
  `try { … } finally { callChain.Remove(group.Id); }` + lock the set (it models a *chain*, not a visited set).
  **Test:** sequential repeat GroupCall executes twice; true recursion still refused.

**Files:** `src/Salvo.Core/Services/AppOrchestrator.cs`, `src/Salvo.App/ViewModels/MainWindowViewModel.cs`,
`tests/Salvo.Core.Tests/GraphOrchestratorTests.cs`, `tests/Salvo.App.Tests/`.

---

### 04. [P0] Child-process tracking: remove silent-breakaway  `[ ]`

- **Priority:** P0 · **Effort:** S · **Risk:** low · **Category:** correctness

**Findings:**

- **[high][bug]** `src/Salvo.Core/Native/ChildProcessTracker.cs:112` — `TryConfigureBreakawayOk()` sets
  `JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK`, whose documented semantics create **all children outside the job** — the
  tracker can only ever see the assigned root. Because `LaunchSession.EnumerateDescendantPidsUncached` returns the
  job list whenever non-empty (it always contains the live root), the toolhelp fallback never runs while the root
  is alive: readiness/telemetry never see the real app behind launcher stubs, and `IsTreeAlive` degrades.
  **Fix:** delete `TryConfigureBreakawayOk` (nested jobs are supported on the 10.0.19041 target; no
  `KILL_ON_JOB_CLOSE` is set, so keeping children in the job has no side effects). **Test:** job-tracked child
  process appears in `EnumerateDescendantPids` (spawn `cmd /c start`-style child where CI permits, else pin via
  toolhelp fallback path assertions).

**Files:** `src/Salvo.Core/Native/ChildProcessTracker.cs`, `src/Salvo.Core/Native/JobObjectInterop.cs`.

---

### 05. [P0] Flow editor data-loss fixes  `[ ]`

- **Priority:** P0 · **Effort:** M · **Risk:** medium · **Category:** correctness

**Why:** The editor can silently lose or corrupt user work today: five editable node fields have no persistence
trigger and no save-on-exit; the branch round-trip transform corrupts graphs the editor itself can create; a
top-boundary drop orphans branch nodes invisibly; stale drag state can drag the wrong node; and branch apps are
invisible to every app aggregate (no icons, stale running dots).

**Findings:**

- **[high][bug]** `src/Salvo.App/ViewModels/Flow/GroupGraphViewModel.cs:101-112` — `WriteTo`/`CollapseBranches`
  round-trip corrupts any graph where an If node has ≥2 unlabeled outgoing edges (creatable via
  `MergeIntoStage`'s `stagePreds` wiring at :509-518): `WriteTo` expands branches once *per* unlabeled edge;
  reload's `FollowLabeledChain` takes `FirstOrDefault`, computes the wrong join, and the cleanup deletes all
  labeled edges but restores one — other successors end up disconnected next to START.
  **Fix:** expand each If's branches exactly once against the *set* of unlabeled successor targets (tail edge fans
  out to every join, all labeled); make `CollapseBranches` restore one opaque edge per join.
  **Test:** multi-successor If round-trip (none exists today).
- **[high][bug]** `src/Salvo.App/Views/Flow/GroupFlowView.xaml:183,402,428,467,495` — Wait duration, service names
  (×2), run-command text, and group-call picker update the VM but nothing persists them (only the If condition has
  flush handlers); no save-on-exit in `App.OnExit` or `MainWindow.OnClosing`; an external config reload clobbers
  pending edits. **Fix:** wire the same LostFocus/DropDownClosed flush pattern onto all five inputs (per-field
  handlers routed to `PersistConfig`), and add a safety-net flush on window close.
- **[medium][bug]** `src/Salvo.App/Views/Flow/GroupFlowView.xaml.cs:298-352` — `_dragSource`/`_dragSourceElement`
  set on mouse-down are never cleared on mouse-up (only in the `DoDragDrop` finally), so a plain click arms a
  later spurious drag of the wrong node from a TextBox or empty-space move. **Fix:** clear both in an
  `OnPreviewMouseLeftButtonUp` override and at the top of `OnPreviewMouseLeftButtonDown`.
- **[medium][bug]** `src/Salvo.App/Views/Flow/GroupFlowView.xaml.cs:529-531` +
  `GroupGraphViewModel.cs:543-544` — `Stage_OnDrop` calls `ExtractBranchNodeToOuter` (adds node edgeless, no
  rebuild) *before* deciding the target; boundary 0 routes to `MoveNodeBeforeStage` which returns at `idx <= 0` —
  the node vanishes and persists disconnected. The dead top boundary also advertises an insertion line that no-ops.
  **Fix:** validate/decide the boundary before extraction; remove boundary 0 from `CollectRowBoundaries` (or make
  `MoveNodeBeforeStage` fall back to "after stage 0").
- **[medium][bug]** `src/Salvo.App/ViewModels/GroupViewModel.cs:41-42` — `Apps` scans only outer
  `AppNodeViewModel`s; branch apps live on `IfElseNodeViewModel.ThenNodes/ElseNodes`, so icon loading,
  `RefreshRunningStates`, and group-level indicators never see them (permanently stale dot, no icon after restart).
  **Fix:** flatten branch apps into `Apps` and subscribe to branch collection changes in `OnGraphNodesChanged`.
- **[low][idiom]** `src/Salvo.App/ViewModels/Flow/NodeViewModel.cs:180-185` — condition-kind strings
  (`"fileExists"`/`"processRunning"`/…) are raw literals in four places (VM setter switch, `FlowConditionViewModel`
  Kind overrides, XAML ComboBoxItem Tags, JSON discriminators) with a silent fall-through default — the exact
  hazard `NodeKinds` was introduced to kill. **Fix:** add `ConditionKinds` consts and route all C# sites through it.

**Files:** `src/Salvo.App/ViewModels/Flow/GroupGraphViewModel.cs`, `src/Salvo.App/Views/Flow/GroupFlowView.xaml(.cs)`,
`src/Salvo.App/ViewModels/GroupViewModel.cs`, `src/Salvo.App/ViewModels/Flow/NodeViewModel.cs`,
`src/Salvo.App/ViewModels/Flow/FlowConditionViewModel.cs`, `tests/Salvo.App.Tests/FlowBranchRoundTripTests.cs`.

---

### 06. [P1] Launch pipeline robustness  `[ ]`

- **Priority:** P1 · **Effort:** M · **Risk:** low-medium · **Category:** correctness/design

**Why:** The launch stack's lifetime edges are frayed: cancelled probes race session/job-handle disposal; ETW
evidence is systematically under-captured; fast windowless apps always ride out the full 30s timeout; known-dead
services poll to timeout because probes can't say "definitively failed"; and process/task ownership contracts are
implicit.

**Findings:**

- **[medium][bug]** `src/Salvo.Core/Launch/ReadinessDetector.cs:53-65` — on a winner the detector cancels
  `linkedCts` and returns without draining `allWatched`; `ObserveAsync`'s finally disposes the session and closes
  the job handle while a probe can still call `EnumerateDescendantPids()` → check-then-use on a closed
  (OS-recyclable) Win32 handle. **Fix:** `await Task.WhenAll(allWatched)` after cancel (probes complete promptly),
  plus a disposal guard in `ChildProcessTracker`.
- **[medium][bug]** `src/Salvo.Core/Launch/EtwResourceMonitor.cs:97` — file events stamped with dispatch-time
  `UtcNow` instead of the ETW `data.TimeStamp`; buffered events get stamped past `to` and excluded; events still in
  kernel buffers at query time are missed. **Fix:** stamp with `data.TimeStamp` (UTC), call `_session.Flush()`
  before querying, and change the ascending-order `break` (:46) to `continue` (cross-CPU merge isn't monotonic).
- **[medium][bug]** `src/Salvo.Core/Launch/Probes/ActivityQuietProbe.cs:45-56,71` — CPU is measured only as deltas
  between samples, so startup CPU before the first ~500ms tick is invisible; fast-initializing windowless apps keep
  `maxCpuSeen ≈ 0` and the `MinMaxCpuSeen` gate blocks firing forever → always TimedOut, stalling downstream flow
  nodes 30s. **Fix:** seed the activity gate from the first sample's `TotalProcessorTime`. **Test:** pin it.
- **[medium][design]** `src/Salvo.Core/Launch/IReadinessProbe.cs:12` — `Task<bool>` collapses "definitively failed"
  into "never fired": `ServiceRunningProbe` returns false on `NotFound` and the detector waits out the full timeout
  (no early-exit watcher is viable for services). **Fix:** tri-state probe result (`Fired`/`GaveUp`/`Failed`); the
  detector short-circuits when all probes have definitively resolved and no early-exit watcher is viable.
- **[medium][design]** `src/Salvo.Core/Launch/LaunchTelemetryService.cs:51-61` — `BeginObservation` silently takes
  ownership of (and disposes) the caller's `Process`; the no-telemetry path in `AppOrchestrator.LaunchAppCore`
  leaks the handle. **Fix:** document ownership transfer on `ILaunchTelemetryService`; orchestrator disposes the
  process itself when telemetry is absent.
- **[low][bug]** `src/Salvo.Core/Launch/LaunchTelemetryService.cs:65,71-99` — `ResolvePidAsync` is fire-and-forget
  with no lifetime tie to the observation (polls up to 5s after completion; can touch a disposed session); the
  `try { await Task.Delay } catch { break; }` is a dead catch (token-less delay can't throw). **Fix:** link to a
  per-observation CTS cancelled when `ObserveAsync` finishes; drop the dead catch.
- **[low][bug]** `src/Salvo.Core/Launch/EtwResourceMonitor.cs:34,57-86` — `Dispose` racing the background
  `TryStart` can strand the named kernel ETW session past process scope (`TryStart` never checks `_disposed`).
  **Fix:** guard `TryStart` with the disposed flag under a lock.
- **[low][design]** `src/Salvo.Core/Launch/ReadinessDetector.cs:34-38` — zero applicable probes records
  `TimedOut` with 0ms elapsed, polluting benchmarks with fake timeouts. **Fix:** return `Unknown` with
  `ReadinessSignal.None`.
- **[low][idiom]** `src/Salvo.Core/Launch/SqliteLaunchBenchmarkStore.cs:75` — `InitializeAsync` ignores its
  CancellationToken. **Fix:** `=> _initTask.WaitAsync(cancellationToken);`.
- **[low][design]** `src/Salvo.Core/Launch/LaunchSession.cs:37-38` — dead public surface: `SignalFired` and
  `JobAssigned` have no callers; `LaunchOutcome.Failed` is never produced (only display-mapped).
  **Fix:** delete the two properties; wire `Failed` in `ObserveAsync`'s catch (more honest than `Unknown`).

**Files:** `src/Salvo.Core/Launch/**`, `src/Salvo.Core/Native/ChildProcessTracker.cs`,
`src/Salvo.Core/Services/AppOrchestrator.cs`, `tests/Salvo.Core.Tests/**`.

---

### 07. [P1] Config-store resilience  `[ ]`

- **Priority:** P1 · **Effort:** S · **Risk:** low · **Category:** correctness

**Findings:**

- **[medium][bug]** `src/Salvo.Core/Services/JsonConfigStore.cs:73` — an unparseable or whitespace config silently
  loads as empty `Configuration`, which the next `Save` permanently overwrites; the file-watcher path
  (`OnFileChanged` → `Load` mid-external-write) can publish the empty config via `Changed`, after which any user
  edit destroys all groups. **Fix:** on parse failure, (1) copy the file aside as `config.json.bad`, (2) log
  loudly, (3) on watcher reloads keep the previous good in-memory configuration instead of publishing empty,
  (4) expose a `LastLoadFailed` state the App can surface. **Test:** corrupt-file load → quarantine + previous
  config retained; save after failed load does not clobber the `.bad` snapshot.

**Files:** `src/Salvo.Core/Services/JsonConfigStore.cs`, `tests/Salvo.Core.Tests/`.

---

### 08. [P1] Leaks, lifetime & state staleness  `[ ]`

- **Priority:** P1 · **Effort:** M · **Risk:** low · **Category:** correctness

**Findings:**

- **[medium][bug]** `src/Salvo.App/ViewModels/UpdateFlyoutViewModel.cs:34-44` — transient VM subscribes an
  instance-capturing lambda to the static `LocalizationManager.Instance.PropertyChanged` and never unsubscribes:
  one VM (+ `DownloadSpeedTracker`) rooted per flyout open, forever. (Found independently by two reviewers.)
  **Fix:** detach on `CloseRequested`/`Closed` (window already unhooks its own handler) or `WeakEventManager`.
- **[medium][bug]** `src/Salvo.App/ViewModels/MainWindowViewModel.cs:272-298` — navigation history retains stale
  `GroupViewModel` refs across config reloads/deletion; Back can "resurrect" a deleted group and the title-bar
  commands operate on it. **Fix:** store `(ActiveView, string? GroupId)` in `NavigationState`, resolve against
  `Groups` at apply time, drop unresolvable entries.
- **[low][bug]** `src/Salvo.App/ViewModels/MainWindowViewModel.cs:728-762` — `_refreshInFlight` is not released in
  try/finally inside the fire-and-forget `Task.Run`; one exception (e.g. dispatcher shutdown mid-tick) permanently
  disables running-state polling, silently. **Fix:** `try/finally { Interlocked.Exchange(ref _refreshInFlight, 0); }`.
- **[low][bug]** `src/Salvo.App/ViewModels/AddAppPickerViewModel.cs:152-193` — a restarted `LoadAsync` races the
  cancelled one's `finally { IsLoading = false; }` (clears the busy indicator while the replacement scan runs); CTS
  instances are never disposed. **Fix:** capture the CTS locally; only clear `IsLoading` when the token matches the
  current one; dispose the old CTS.
- **[low][design]** `src/Salvo.App/ViewModels/AddAppPickerViewModel.cs:195-228` — `StartIconLoading` re-implements
  the STA icon loop that the sweep centralized into `IconLoad`, and bypasses `AppIconCache`. **Fix:** add an
  optional `CancellationToken` to `IconLoad.Start` and delete the local thread.

**Files:** `src/Salvo.App/ViewModels/UpdateFlyoutViewModel.cs`, `src/Salvo.App/Views/UpdateFlyoutWindow.xaml.cs`,
`src/Salvo.App/ViewModels/MainWindowViewModel.cs`, `src/Salvo.App/ViewModels/AddAppPickerViewModel.cs`,
`src/Salvo.App/Services/IconLoad.cs`.

---

### 09. [P1] Background churn & interaction perf  `[ ]`

- **Priority:** P1 · **Effort:** M · **Risk:** medium · **Category:** perf

**Findings:**

- **[medium][perf]** `src/Salvo.Core/Services/ProcessInspector.cs:142` — every AUMID `IsRunning` check scans all
  system processes (`OpenProcess`+`GetApplicationUserModelId` per PID), per app entry, per 3s tick, on the UI
  thread. **Fix:** one PID→AUMID snapshot per refresh pass (short-TTL cache in the inspector, like
  `LaunchSession`'s 200ms PID cache).
- **[medium][perf]** `src/Salvo.App/Views/MainWindow.xaml.cs:256` — minimize-to-tray `Hide()`s the window but the
  3s `DispatcherTimer` keeps running `RefreshRunningStates` (full groups snapshot + process-table sweep) forever
  while invisible. **Fix:** pause/resume the timer on `IsVisibleChanged`.
- **[medium][perf]** `src/Salvo.App/ViewModels/BenchmarksViewModel.cs:55-58` — every `MetricsSaved` triggers a full
  store reload + re-aggregation + icon reload + dependency re-analysis regardless of view visibility (a 10-app
  launch = ~10 sequential full refreshes). **Fix:** trailing debounce (~1-2s) + dirty flag consumed on view
  activation (`OnActiveViewChanged` already refreshes on entry).
- **[medium][perf]** `src/Salvo.App/Views/Flow/GroupFlowView.xaml.cs:382-398,440-452,641-660` — every DragOver runs
  two full recursive visual-tree walks with O(elements×nodes) membership checks; OLE fires DragOver continuously.
  **Fix:** build the row/boundary list once per drag (invalidate on `RebuildStages`), reuse for boundary snap and
  `FindRowAtY`.
- **[medium][perf]** `src/Salvo.App/ViewModels/Flow/GroupGraphViewModel.cs:379-387` — `RebuildStages`'s comment
  claims reconcile-in-place; the code does `Stages.Clear()` + all-new `StageViewModel`s, re-templating the whole
  list per structural edit (kills TextBox focus, fights Phase E's overlay). Node VMs already survive.
  **Fix:** reconcile: reuse `StageViewModel` instances by index and sync their `Nodes` collections in place.
- **[medium][perf]** `src/Salvo.App/Views/MainWindow.xaml.cs:56` — `GroupsList.LayoutUpdated` fires on every layout
  pass window-wide and `UpdateIndicator` unconditionally restarts a `DoubleAnimation` even when target Y/opacity
  are unchanged. **Fix:** remember the last computed target and early-return when unchanged (mirror the
  `_indicatorSettled` fast path).

**Files:** `src/Salvo.Core/Services/ProcessInspector.cs`, `src/Salvo.App/Views/MainWindow.xaml.cs`,
`src/Salvo.App/ViewModels/BenchmarksViewModel.cs`, `src/Salvo.App/Views/Flow/GroupFlowView.xaml.cs`,
`src/Salvo.App/ViewModels/Flow/GroupGraphViewModel.cs`.

---

### 10. [P1] Localization completeness  `[ ]`

- **Priority:** P1 · **Effort:** S · **Risk:** low · **Category:** i18n

**Findings:**

- **[medium][design]** `src/Salvo.App/Views/TrayMenuFactory.cs:28` + `TrayViewModel.cs:46,62,84` — tray menu
  headers use static `Strings.*` snapshots (rebuilt only on config change) and the tooltip is set once at
  `Initialize`; after a live language switch the tray stays in the old language. **Fix:** subscribe `TrayViewModel`
  to `LocalizationManager.Instance.PropertyChanged` and rebuild menu + tooltip.
- **[low][bug]** `src/Salvo.App/ViewModels/AddAppPickerViewModel.cs:160,178,187` — hardcoded English status strings
  ("Scanning installed apps...", "Found {n} installed apps", "Failed to list installed apps."). Same pattern in
  `AppEntryViewModel.cs:46-73` (readiness labels + tooltip) and `BenchmarkRowViewModel.cs:32,49-58`.
  **Fix:** move to `Strings.resx` (all 8 satellite cultures get the keys; machine-translate placeholders are fine
  pre-1.0).
- **[low][bug]** `src/Salvo.App/Localization/SupportedLanguages.cs:19` — hardcoded English "System default" as a
  NativeDisplayName. **Fix:** resx key resolved at display time.
- **[low][idiom]** `src/Salvo.App/Localization/LocalizationManager.cs:37` — `AttachLogger` is never called, so
  missing-translation warnings go to `NullLogger` forever. **Fix:** wire it after host build in `OnStartup`.

**Files:** `src/Salvo.App/Views/TrayMenuFactory.cs`, `src/Salvo.App/ViewModels/TrayViewModel.cs`,
`src/Salvo.App/ViewModels/AddAppPickerViewModel.cs`, `src/Salvo.App/ViewModels/AppEntryViewModel.cs`,
`src/Salvo.App/ViewModels/BenchmarkRowViewModel.cs`, `src/Salvo.App/Localization/**`, `src/Salvo.App/Resources/*.resx`.

---

### 11. [P1] CI & build hygiene  `[ ]`

- **Priority:** P1 · **Effort:** S · **Risk:** low · **Category:** ci/build

**Findings:**

- **[medium][security]** `release.yml:246`, `release-drafter.yml:18,30`, `dependabot-auto-merge.yml:16` —
  third-party actions with write-capable tokens pinned to mutable major tags (`winget-releaser@v2` receives a PAT;
  `release-drafter@v7` has `contents: write`; `fetch-metadata@v3` sits with `contents: write` +
  `pull-requests: write`). **Fix:** pin to full commit SHAs (Dependabot updates SHA pins). Also: `ci.yml:25-26`
  grants workflow-level `contents: write` that PR runs don't need — scope it to the canary-publish job.
- **[medium][ci]** `ci.yml:38-39`, `release.yml:20-21` — NuGet cache key hashes only `**/*.csproj`; under CPM all
  versions live in `Directory.Packages.props`, so version bumps never refresh the cache.
  **Fix:** add `Directory.Packages.props` to `cache-dependency-path`.
- **[low][ci]** `ci.yml:44-48` — only Release is ever compiled; `#if DEBUG` paths and Debug-only diagnostics can
  ship broken for local dev. **Fix:** add a `dotnet build -c Debug` step (build-only, no double test run).
- **[low][test]** `tests/*/*.csproj:8-9` — `coverlet.collector` is referenced but CI never collects, and no TRX
  artifacts are uploaded on failure. **Fix:** add `--collect:"XPlat Code Coverage" --logger trx` +
  `actions/upload-artifact` (on failure at minimum).
- **[low][build]** `Directory.Build.props:7-8` — `AnalysisLevel=latest` without `AnalysisMode` leaves the minimal
  CA set; `EnforceCodeStyleInBuild` is inert with no `.editorconfig` (deliberately rejected — not re-proposing).
  **Fix:** try `<AnalysisMode>Recommended</AnalysisMode>` and fix resulting diagnostics; if the volume is
  unreasonable, revert to default mode and delete the inert `EnforceCodeStyleInBuild`, documenting the choice.

**Files:** `.github/workflows/*.yml`, `Directory.Build.props`, `Directory.Packages.props`, `tests/*/*.csproj`.

---

### 12. [P2] Core robustness (elevator, globs, STA, cancellation)  `[ ]`

- **Priority:** P2 · **Effort:** S · **Risk:** low · **Category:** correctness/security

**Findings:**

- **[low][security]** `src/Salvo.Elevator/Program.cs:106-136` — the elevated helper applies any `RegistryEdit`
  handed to it, including HKCU sources; under over-the-shoulder UAC, `Registry.CurrentUser` is the **admin's**
  hive and the write silently "succeeds" in the wrong place. **Fix:** return `BadRequest` for
  non-`RegistryRunMachine*` sources at the trust boundary.
- **[low][bug]** `src/Salvo.Core/Services/PathResolver.cs:32` — `ResolveGlob` re-walks the root's own tokens as
  segments: UNC globs (`\\server\share\app*\x.exe`) always fail; drive paths survive only via a `Path.Combine`
  re-rooting accident. **Fix:** split only the portion after `Path.GetPathRoot(pattern)`, seed candidates with the
  root. **Test:** UNC-shaped and drive-shaped glob unit tests (pure path logic).
- **[low][bug]** `src/Salvo.Core/Services/ShellInstalledAppsProvider.cs:106` — the enumeration-level
  `catch (Exception)` swallows the `OperationCanceledException` from `ThrowIfCancellationRequested` (:53): logged
  as an error, partial results returned as success, composite never observes cancellation.
  **Fix:** `catch (OperationCanceledException) { throw; }` above the generic catch.
- **[low][design]** `src/Salvo.Core/Services/ProcessMatcherResolver.cs:74` — `ResolveViaShell` drives
  `Shell.Application` COM from MTA thread-pool continuations while `ShellInstalledAppsProvider` deliberately uses a
  dedicated STA thread for the same object; failures are swallowed into an empty matcher list → missed
  "already running" check → duplicate launch. **Fix:** extract the STA helper (`RunOnStaAsync`) and route both
  through it; log the previously swallowed failure.

**Files:** `src/Salvo.Elevator/Program.cs`, `src/Salvo.Core/Services/PathResolver.cs`,
`src/Salvo.Core/Services/ShellInstalledAppsProvider.cs`, `src/Salvo.Core/Services/ProcessMatcherResolver.cs`,
`tests/Salvo.Core.Tests/`.

---

### 13. [P2] Graph model versioning & load dedup  `[ ]`

- **Priority:** P2 · **Effort:** S · **Risk:** low · **Category:** design

**Findings:**

- **[medium][design]** `src/Salvo.App/ViewModels/GroupViewModel.cs:113-142` — the group load path duplicates
  `GroupGraphViewModel.FromModel`'s migrate/seed/populate/collapse/rebuild sequence line-for-line; the two already
  diverged once (pinned by FlowBranchRoundTripTests.cs:131-146). **Fix:** instance `Load(Group)` method on
  `GroupGraphViewModel`; both factories call it.
- **[low][design]** `src/Salvo.Core/Models/Flow/Node.cs:9-17` — no schema version on the persisted graph (unknown
  `type` discriminators hard-fail older builds with no "newer schema" gate), and branch/join edge GUIDs churn every
  save/load cycle so nothing (Phase E overlays) can key on them. **Fix:** add `SchemaVersion` to the root config,
  gate load with a friendly "written by a newer version" error, and preserve branch-edge IDs across
  collapse/expand. **Test:** round-trip preserves edge IDs; newer-schema load fails cleanly.

**Files:** `src/Salvo.Core/Models/Flow/**`, `src/Salvo.Core/Models/Configuration.cs` (or equivalent root),
`src/Salvo.App/ViewModels/GroupViewModel.cs`, `src/Salvo.App/ViewModels/Flow/GroupGraphViewModel.cs`,
`tests/Salvo.App.Tests/FlowBranchRoundTripTests.cs`.

---

### 14. [P2] ArgumentChips consolidation  `[ ]`

- **Priority:** P2 · **Effort:** M · **Risk:** low · **Category:** design

**Findings:**

- **[medium][design]** `AppEntryEditorViewModel.cs:51-89,134-192,336-381` vs
  `RegistryRunValueEditorViewModel.cs:38-55,148-170,364-410` — the entire argument-chip subsystem is duplicated
  (collection + commit/remove commands + empty-value removal + flag-value combining + `StripQuotes`), **including a
  shared bug**: chips are unsubscribed only via `e.OldItems`, which is null on `Clear()`, leaving stale chips
  subscribed whose `Value` changes can clobber `Args`. **Fix:** extract an `ArgumentChipsController` (owned
  collection, commands, tokenizer, Reset-aware unsubscription) used by both editors. **Test:** Clear() unsubscribes;
  stale chip mutation cannot touch `Args`.
- **[low][design]** `AppEntryEditorWindow.xaml.cs:33-70` ≡ `RegistryRunValueEditorWindow.xaml.cs:37-74` — the
  `ChipEditBox_*` focus trio is verbatim-identical. **Fix:** one attached behavior.

**Files:** `src/Salvo.App/ViewModels/AppEntryEditorViewModel.cs`,
`src/Salvo.App/ViewModels/RegistryRunValueEditorViewModel.cs`, both editor windows, new
`src/Salvo.App/ViewModels/ArgumentChipsController.cs`, `tests/Salvo.App.Tests/`.

---

### 15. [P2] Dialog service widening  `[ ]`

- **Priority:** P2 · **Effort:** M · **Risk:** low · **Category:** design

**Findings:**

- **[medium][design]** `MainWindowViewModel.cs:557-558,832-834,874-875,949-950,1060-1061,1082-1083` +
  `WindowsStartupViewModel.cs:256-261` — VMs instantiate concrete `Window`s and call `ShowDialog()` directly
  despite `IDialogService` existing (it only covers confirm/error/file-picker); six repetitions of the
  resolve-configure-show-apply dance, blocking headless testing of every CRUD path. **Fix:** add typed methods
  (e.g. `bool? ShowGroupEditor(GroupEditorViewModel vm)`, `ShowAppEntryEditor`, `ShowAddAppPicker`,
  `ShowRegistryRunValueEditor`, `ShowUpdateFlyout`) to the dialog abstraction; VMs depend only on it.

**Files:** `src/Salvo.App/Services/IDialogService.cs` (+ implementation), `src/Salvo.App/ViewModels/*.cs`.

---

### 16. [P2] Native interop consolidation  `[ ]`

- **Priority:** P2 · **Effort:** M · **Risk:** medium · **Category:** design

**Findings:**

- **[low][design]** `ProcessInspector.cs:233-236`, `JobObjectInterop.cs:83`, `ProcessIoInterop.cs:25`,
  `Toolhelp32Interop.cs:39` — four private `CloseHandle` declarations (ProcessInspector's uniquely lacks
  `SetLastError`), duplicated `OpenProcess` + `PROCESS_QUERY_LIMITED_INFORMATION`, raw `IntPtr` throughout.
  **Fix:** one shared kernel32 interop class using `[LibraryImport]` + `SafeHandle`s (`SafeProcessHandle`, derived
  handles for jobs/snapshots), removing the manual try/finally close pattern. Distinct from the sweep's
  icon/duration/self-elevation dedup.

**Files:** `src/Salvo.Core/Native/**`, `src/Salvo.Core/Services/ProcessInspector.cs`.

---

### 17. [P2] MainWindowViewModel decomposition (5-way split)  `[ ]`

- **Priority:** P2 · **Effort:** L · **Risk:** medium-high · **Category:** design

**Why:** The deliberately-deferred sweep workstream, now with a concrete map. MainWindowViewModel (~1386 lines)
holds six responsibilities; `GroupFlowView` reaches into it (`vm.InsertNodeAfterStage`, `vm.PersistConfigPublic`)
for topology mutations. Extract in dependency order — FlowEditor first is highest-value, lowest-entanglement.

**Extraction map (line ranges from the review snapshot):**

1. **FlowEditorViewModel** (~430 lines) — `AppendAppNode` (903-938); `AllGroups`/`CreateNode`/`AddNode`/
   `BuildAppNodeViaEditor`/`PickAppNodes`/`ToAppNode`/`InsertNodeAfterStage`/`AddNodeToBranch`/`MoveNodeIntoBranch`/
   `ExtractBranchNodeToOuter`/`RemoveFromAnyBranch`/`MakeStageSequential`/`RemoveNodeAsync` (968-1300);
   `PersistConfigPublic` (765). Expose as a `Flow` property so `GroupFlowView` binds directly — retires the
   `PersistConfigPublic` back-door and the fully-qualified `Salvo.App.ViewModels.Flow.*` noise.
2. **GroupsViewModel** (~350 lines) — `Groups`/`SelectedGroup`; config load/hydration/metrics events; persist;
   running-state polling incl. timer; launch/stop + elevation prompts; group CRUD; `Slugify`.
3. **SettingsViewModel** (~200 lines) — settings init, theme/language options, toggles, `PersistSettings`,
   shell-launch commands/folders, `ConfigPath`. Admin banner state stays on the shell (needs `HasAnyGroupContent`
   + `ActiveView`) or becomes `ElevationStatusViewModel`.
4. **UpdateViewModel** (~120 lines) — version labels, update state, check/details commands; pairs with the
   existing `UpdateFlyoutViewModel`.
5. **NavigationViewModel** (~150 lines) — `ActiveView`, history stacks, view-switch commands, changed-hooks.
   `NavigationState` becomes `(ActiveView, string? GroupId)` (see W08).

Residual `MainWindowViewModel` ≈ 150-line composition root exposing the five children. Shared state:
`SelectedGroup` (navigation+groups+flow) and `RefreshRunningStates` (groups+flow) flow through the children's
constructor dependencies, not back-references.

**Files:** `src/Salvo.App/ViewModels/MainWindowViewModel.cs` (+5 new VM files), `src/Salvo.App/Views/MainWindow.xaml`,
`src/Salvo.App/Views/Flow/GroupFlowView.xaml(.cs)`, `src/Salvo.App/App.xaml.cs` (DI), tests.

---

### 18. [P2] Flow drop-policy extraction & one-level undo  `[ ]`

- **Priority:** P2 · **Effort:** M · **Risk:** medium · **Category:** design

**Findings:**

- **[medium][design]** `src/Salvo.App/Views/Flow/GroupFlowView.xaml.cs:519-578,698-735` — the drop-decision policy
  (merge bands 0.30–0.70 arm / 0.10–0.90 release, `IsParallelizable`, boundary→operation translation) is the most
  bug-prone logic in the editor (W05's drag findings all route through it), lives untestable in code-behind, and
  every drop persists immediately with no undo (an accepted If-deletion discards all branch children irreversibly).
  **Fix:** extract a pure `DropPlanner` (inputs: boundary list, cursor Y, dragged node → output: operation) into
  the VM layer with unit tests; add a one-level undo snapshot (serialize `Group` before each mutating op, Ctrl+Z
  restores) as cheap insurance until real undo lands.

**Files:** `src/Salvo.App/Views/Flow/GroupFlowView.xaml.cs`, new `src/Salvo.App/ViewModels/Flow/DropPlanner.cs`,
`src/Salvo.App/ViewModels/Flow/GroupGraphViewModel.cs`, `tests/Salvo.App.Tests/`.

---

### 19. [P2] Dead code, docs & UI polish  `[ ]`

- **Priority:** P2 · **Effort:** M · **Risk:** low · **Category:** hygiene

**Findings:**

- **[high][docs]** `ROADMAP.md:5-313` — ~300 lines describe the retired Velopack+Burn stack as the current locked
  plan (rules MSIX out, documents a nonexistent `nightly.yml`, canonizes Burn/MSI decisions the MSIX plan
  reverses). **Fix:** gut the installer section to a pointer at `docs/MIGRATION_PLAN.md`, keeping the still-true
  battle scars (Velopack pins, FluentAssertions license, release.yml ordering) and the v1.0 feature-gap table.
- **[medium][build]** `Directory.Packages.props:39`, `Directory.Build.props:25-27` — `WixToolset.BootstrapperApplicationApi`
  has no consuming project; `AppUpgradeCode`/`AppBundleUpgradeCode` reference a Burn bundle that no longer exists.
  **Fix:** delete all three.
- **[low][docs]** `README.md:75,21,100` — claims tests live only in `tests/Salvo.Core.Tests`; claims "10 languages"
  (9 ship); footer copyright disagrees with `Directory.Build.props`. **Fix:** one-line edits.
- **[low][bug]** `src/Salvo.App/Styles/Tokens.xaml:51` — the app-wide ScrollBar template hard-codes
  `IsDirectionReversed="True"` on `PART_Track` (correct only for vertical); the style's own Horizontal trigger
  anticipates horizontal use where this inverts drag direction. **Fix:** set it from an Orientation trigger.
- **[low][idiom]** `src/Salvo.App/Controls/MarkdownView.cs:53,42` — body text snapshots theme brush/font via
  `TryFindResource` (code blocks/hyperlinks correctly use `SetResourceReference`); `Loaded += Render` re-renders on
  every reshow. **Fix:** `document.SetResourceReference(TextElement.ForegroundProperty, …)`; render once.
- **[low][design]** `src/Salvo.App/Views/MainWindow.xaml:230` — three copy-pasted ~30-line inline nav-button
  ControlTemplates differing only in the active-view trigger; the keyed `SidebarNavButton` style is effectively
  dead. **Fix:** one parameterized style (Tag/attached property).
- **[low][idiom]** `src/Salvo.App/App.xaml.cs:31,225-229` — the static `App.Services` locator has zero call sites
  (delete); `TrySeedChannelFromRunningVersion` mutates `settings.Current` in place, violating the documented
  `Clone()` contract (works by accident). **Fix:** delete locator; use `Current.Clone()`.
- **[low][idiom]** `src/Salvo.App/ViewModels/MainWindowViewModel.cs:269,583` — `Benchmarks` is publicly mutable
  non-observable (make get-only/init); manual `NotifyCanExecuteChanged` duplicates the `OnIsUpdateAvailableChanged`
  hook. Median math/formatting duplicated between `BenchmarksViewModel.cs:293-302` and
  `AppBenchmarkSummaryViewModel.cs:75-96` — fold into a shared helper next to `DurationFormat`.
- **[low][idiom]** `src/Salvo.App/ViewModels/Flow/GroupGraphViewModel.cs:264,762,15-20` +
  `StageViewModel.cs:30-31` — dead API: `AddSiblingOf`, `MakeStagesParallel` (no callers),
  `DropIndicatorTop/Bottom` (never set/bound); class doc references methods that don't exist. **Fix:** delete +
  correct docs. *(Skip any members W17/W18 already removed or repurposed.)*

**Files:** `ROADMAP.md`, `README.md`, `Directory.Build.props`, `Directory.Packages.props`,
`src/Salvo.App/Styles/Tokens.xaml`, `src/Salvo.App/Controls/MarkdownView.cs`, `src/Salvo.App/Views/MainWindow.xaml`,
`src/Salvo.App/App.xaml.cs`, `src/Salvo.App/ViewModels/**`.

---

## Verified clean (spot-checked by the reviewers — do not "fix")

- SQLite store: WAL-at-init, per-connection busy_timeout, parameterized SQL, correct transactions, UTC-normalized
  `"O"` timestamps (lexicographic compares are chronologically correct).
- Settings clone→mutate→save contract intact everywhere (except the one `TrySeedChannelFromRunningVersion` nit in W19).
- No `async void` in the ViewModel layer; fire-and-forget entry points all target methods with internal catch-alls.
- Cross-thread collection discipline (snapshot on UI thread → background query → dispatcher marshal) holds throughout.
- HttpClient lifetimes (static, long-lived, sane timeouts) in both update services; MSIX version comparison correct.
- Elevation IPC: base64 single-argv-token payload (injection-proof), `requireAdministrator` manifest, no silent path,
  UAC-decline handled as expected outcome.
- Startup-registry matrix incl. the HKCU 32-bit literal-`WOW6432Node` fix; StartupApproved blob format correct.
- Virtualization configured correctly on all growing lists; resource dictionaries merged once at App scope;
  Static/DynamicResource split consistently correct for theming.
- Test-suite discipline: no `Thread.Sleep`, no real registry mutation, fakes for services, shared temp-dir helpers,
  TCS-synchronized readiness tests with widened margins.
- CPM: transitive pinning, SQLitePCLRaw advisory pin, FluentAssertions 7.x hold + dependabot ignore, Velopack pin
  matching vpk CLI in both workflows.

## Deliberately NOT doing (carried forward from the sweep + new rejections)

- All items on the sweep plan's list (Burn project polish, Velopack-spanning HTTP/DTO abstraction, full
  AppOrchestrator split beyond what W03 does, OperationResult/StartupOperationResult merge, NodeKind enum,
  root `.editorconfig`, re-runnable SQLite init, EventLog in elevator, single-use magic-value extraction).
- **No full undo/redo stack** — W18 adds a one-level snapshot only; real undo is a Phase B+ feature.
- **No probe-framework rewrite** — W06's tri-state result is the proportionate fix; the probe set and fan-out
  design stay as-is.
- **No `Stages` virtualization or full incremental layout** — W09's reconcile-in-place is the proportionate fix at
  current graph sizes; revisit at Phase E.
- **No IHostedService/BackgroundService conversion** of the status timer or config watcher — lifetime is
  window-scoped by design.
