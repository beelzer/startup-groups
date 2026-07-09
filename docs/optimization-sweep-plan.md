# Salvo — Optimization & Cleanup Sweep: Execution Plan

> **Self-contained, execute-from-this-file plan.** Generated 2026-07-10 from a 128-agent audit of the Salvo codebase
> (branch `perf/cold-start`). Every finding below was adversarially verified against the real code before inclusion
> (113 raised → **105 confirmed**, 8 rejected/downgraded). Companion visual report: the published Artifact
> "Salvo — Optimization Sweep: Plan of Attack".

## How to execute this plan

1. **Work top-to-bottom in the numbered order below** (it is the recommended sequence — correctness first, then dead-weight removal, then mechanical passes, structural refactor last).
2. **One workstream = one commit.** Build + run the test suite after each, so every checkpoint is green and revertible:
   `dotnet build Salvo.slnx -c Debug` then `dotnet test Salvo.slnx`.
3. **Add the tests called for in a workstream as part of that workstream** — several P0/P1 items exist specifically to pin the fix.
4. **Respect the "Deliberately NOT doing" list at the bottom** — those were rejected during verification for good reasons; do not re-introduce them.
5. Check off `[ ]` → `[x]` as you land each workstream.

**Scope:** ~20.6k LOC · 184 C# files · 4 source + 2 test projects · **18 workstreams** · 105 findings
(2 high, 15 medium, 88 low).

---

## Workstreams (in execution order)

### 01. [P0] Settings & config persistence integrity  `[ ]`

- **Priority:** P0 · **Effort:** S · **Change risk:** low · **Category:** correctness

**Why:** Two real, silent, persisted data-loss paths converge on AppSettings: changing UI language wipes 5 unrelated settings (update channel, AlwaysRunAsAdmin, etc.), and a non-atomic settings write can corrupt the file and silently reset everything to defaults. Both stem from hand-rolled per-field copies and a write path that diverges from the already-correct JsonConfigStore. High value, tiny surface, low risk.

**Approach:** Add a single AppSettings.Clone() (all 9 fields) and route both LanguageService.SetLanguage and MainWindowViewModel.PersistSettings through it, deleting the two hand-rolled per-field copies (kills the root cause). Make SettingsStore.Save atomic (write settings.json.tmp then File.Move overwrite) mirroring JsonConfigStore, and give SettingsStore an optional NullLogger-defaulted ILogger so Load logs the swallowed exception before falling back to defaults; keep the parameterless `new SettingsStore()` compiling. Add a test that round-trips every AppSettings property through SetLanguage so a newly added field can't be dropped again.

**Files:**
- `src/Salvo.App/Localization/LanguageService.cs`
- `src/Salvo.App/Services/SettingsStore.cs`
- `src/Salvo.App/Models/AppSettings.cs`
- `src/Salvo.App/ViewModels/MainWindowViewModel.cs`
- `src/Salvo.App/App.xaml.cs`

**Rolled-up findings:**

<details>
<summary><b>[high]</b> Changing UI language silently resets 5 unrelated settings to defaults — `src/Salvo.App/Localization/LanguageService.cs:54`</summary>


**Problem.** CloneSettings copies only 4 of AppSettings' 9 properties (Theme, MinimizeToTrayOnClose, ShowNotifications, UiCulture). SetLanguage() builds this partial clone, sets UiCulture, and calls _settings.Save(clone). SettingsStore.Save (SettingsStore.cs:25-33) replaces _current wholesale and re-serializes the whole object — it does not merge. So every time the user picks a language in Settings, AppsViewMode, AlwaysRunAsAdmin, WarnWhenElevatedAppsPresent, UpdateChannel, and ShowMainWindowOnLaunch are all reset to their defaults and persisted to disk. Concretely: a user on the Canary update channel who changes language is silently moved back to Stable and stops receiving canary updates; a user with AlwaysRunAsAdmin=true loses auto-elevation. This is real, persisted data loss triggered by an unrelated action.


**Fix.** Add a single AppSettings.Clone() method (or copy constructor) that copies all 9 fields, and route both LanguageService.SetLanguage and MainWindowViewModel.PersistSettings through it, deleting both hand-rolled per-field copies — the duplicated copy logic drifting out of sync is the actual root cause. Add a test that round-trips every AppSettings property through SetLanguage so a newly added setting can't be silently dropped again.


<sub>effort S · risk low · verified: confirmed (high confidence)</sub>

</details>

<details>
<summary><b>[medium]</b> Non-atomic settings write can silently reset all user settings — `src/Salvo.App/Services/SettingsStore.cs:31`</summary>


**Problem.** Save writes settings.json with a direct File.WriteAllText (line 31). If the process is killed or power is lost mid-write, the file is left truncated/corrupt. Load (lines 42-50) wraps deserialization in catch { return new AppSettings(); }, so a corrupt file silently discards ALL persisted settings and reverts to defaults with no warning. The sibling store in the same solution, Core JsonConfigStore, already does this correctly with an atomic temp-file swap (JsonConfigStore.cs lines 113-115: write to ConfigPath + ".tmp", then File.Move(tempPath, ConfigPath, overwrite: true)) — so this is both a data-loss risk and an inconsistency with the established pattern.


**Fix.** Mirror JsonConfigStore: serialize to a "settings.json.tmp" and File.Move(..., overwrite: true) to atomically replace, so a crash never leaves a half-written primary file.


<sub>effort S · risk low · verified: confirmed (high confidence)</sub>

</details>

<details>
<summary><b>[low]</b> Two JSON config stores diverge on write-durability and failure logging — `src/Salvo.App/Services/SettingsStore.cs:31`</summary>


**Problem.** Salvo has two primary JSON persistence stores that handle the identical concern (persist user config to disk, recover on load) two different ways. JsonConfigStore writes atomically and logs parse failures: SaveInternal writes to `ConfigPath + ".tmp"` then `File.Move(tempPath, ConfigPath, overwrite:true)` (JsonConfigStore.cs:113-115), and Load catches `JsonException` and logs it via the injected ILogger (JsonConfigStore.cs:73-76). SettingsStore does neither: Save does a direct non-atomic `File.WriteAllText(_settingsPath, json)` (line 31), and Load uses a bare `catch { return new AppSettings(); }` (lines 47-50) with no injected logger at all — a crash or power loss mid-write corrupts settings.json, and on next launch every user setting (theme, channel, auto-start, AlwaysRunAsAdmin) silently resets to defaults with zero diagnostic trail. The channel-seed marker write (App.xaml.cs:235) and the GitHub cache write (CachelessGithubSource.cs:118) follow the same non-atomic direct-write shape, so JsonConfigStore is the lone store that got the durable pattern.


**Fix.** Recommendation is sound as written. Two refinements: (1) When adding the logger, make it optional/nullable defaulting to NullLogger (mirror JsonConfigStore's `ILogger<JsonConfigStore>? logger = null`) so the direct `new SettingsStore()` at App.xaml.cs:366 still compiles. (2) The shared AtomicJson.Write helper is a nice-to-have but not required to close this; the minimal fix is temp-file + File.Move in Save plus logging the swallowed exception in Load before discarding. Extending the same atomic write to the marker (App.xaml.cs:235) and GitHub cache (CachelessGithubSource.cs:118) is optional follow-up, lower value since those are recreatable.


<sub>effort S · risk low · verified: confirmed (high confidence)</sub>

</details>


---

### 02. [P0] Launch pipeline correctness (readiness outcomes)  `[ ]`

- **Priority:** P0 · **Effort:** M · **Change risk:** low · **Category:** correctness

**Why:** The shell-launch readiness path can falsely declare ExitedEarly at the 1s grace mark before the PID has even resolved, aborting readiness detection for any slow-to-appear app — masked only by a test stub that resolves at ~600ms. The purpose-built PidNotFound outcome is never emitted, so genuine resolution failures are misclassified. These feed telemetry/benchmarking and user-visible status.

**Approach:** Gate WatchEarlyExitAsync's ExitedEarly return on session.RootPid != null: while the PID is unresolved, keep polling; only conclude early exit once a PID resolved and the tree is then dead (preserves the attached-process crash-in-grace case, lets never-resolving shell launches fall through to TimedOut honestly). In ObserveAsync, when the launch went through the inspector path AND RootPid is still null after DetectAsync, override the outcome to PidNotFound before BuildMetrics. Optionally reset ActivityQuietProbe.quietSince on ticks where observedAny is false. Lock in with a ReadinessDetector test exercising the >1s early-exit path with a null/late RootPid.

**Files:**
- `src/Salvo.Core/Launch/ReadinessDetector.cs`
- `src/Salvo.Core/Launch/LaunchTelemetryService.cs`
- `src/Salvo.Core/Launch/LaunchOutcome.cs`
- `src/Salvo.Core/Launch/Probes/ActivityQuietProbe.cs`
- `src/Salvo.App/ViewModels/AppEntryViewModel.cs`

**Rolled-up findings:**

<details>
<summary><b>[high]</b> Early-exit watcher reports ExitedEarly for shell-launched apps whose PID has not resolved yet — `src/Salvo.Core/Launch/ReadinessDetector.cs:109`</summary>


**Problem.** WatchEarlyExitAsync waits EarlyExitGrace (1s, Timeouts.ReadinessEarlyExitGrace) and then concludes ExitedEarly the moment IsTreeAlive() returns false (ReadinessDetector.cs:107-112). For the shell-launch path, no Process handle is attached, so RootPid is resolved asynchronously by ResolvePidAsync with a 5s deadline polling every 200ms (LaunchTelemetryService.cs:69-97, Timeouts.PidResolveDeadline=5s). Until the PID resolves, LaunchSession.EnumerateDescendantPids() returns Array.Empty (LaunchSession.cs:122-134, no job + null root), so IsTreeAlive() returns false (LaunchSession.cs:137-159). Result: any shell-launched app whose process takes longer than ~1s to appear in the inspector is falsely classified ExitedEarly at the 1s mark, aborting readiness detection, even though it is still starting. The LaunchTelemetryServicePidResolver test only passes because its stub resolves the PID at ~600ms (<1s), masking the bug; ReadinessDetectorTests never exercise the >1s early-exit path.


**Fix.** Gate the ExitedEarly return in WatchEarlyExitAsync on session.RootPid != null: while RootPid is still unresolved, keep polling instead of declaring ExitedEarly; only conclude early exit once a PID has been resolved and the tree is then dead. Prefer this RootPid-based gate over the alternative 'saw the tree alive at least once' flag suggested in the finding — the flag-only variant would regress the attached-process path, where a process that starts and crashes within the 1s grace is a legitimate ExitedEarly (RootPid is set synchronously in AttachRootProcess, so the RootPid gate preserves that detection while the flag would suppress it and mislabel it TimedOut). Accept that a shell launch which never produces a locatable process now falls through to TimedOut rather than ExitedEarly, which is honest since the two are indistinguishable without a resolved PID. Optionally also align ReadinessEarlyExitGrace with the PID-resolve budget for shell launches. Add a ReadinessDetectorTests case exercising the >1s early-exit path with a null/late RootPid to lock this in.


<sub>effort S · risk low · verified: confirmed (high confidence)</sub>

</details>

<details>
<summary><b>[low]</b> LaunchOutcome.PidNotFound is never produced; failed PID resolution misclassified — `src/Salvo.Core/Launch/LaunchOutcome.cs:9`</summary>


<sub>(detail cross-referenced in the full findings appendix)</sub>

</details>

<details>
<summary><b>[low]</b> ActivityQuietProbe does not reset quietSince on ticks where no PID could be sampled — `src/Salvo.Core/Launch/Probes/ActivityQuietProbe.cs:61`</summary>


**Problem.** quietSince is only cleared in the else branch (lines 82-84) inside `if (observedAny && lastTickAt is ...)`. If a poll tick observes no comparable PID between prior and current samples (observedAny stays false — e.g. transient PID churn or all descendants briefly unsampleable), the whole quiet-evaluation block is skipped and quietSince retains its prior value. A genuine burst of CPU/IO activity that happens to fall on such an unsampled tick will not reset the quiet timer, so the probe can accumulate the QuietWindow across a real activity spike and fire 'ActivityQuiet' prematurely.


**Fix.** If tightening is desired, treat a tick with observedAny == false as a break in quiet continuity and reset quietSince = null, so the quiet window only accrues across consecutive confirmed-quiet ticks. Drop the `wallDeltaMs <= 0` clause from the recommendation — it is effectively unreachable given a positive PollInterval. Note the tradeoff: for apps that periodically spawn/exit short-lived children this may occasionally delay readiness; because the main process normally persists (keeping observedAny true) this cost is small in practice. This is a defensiveness refinement, not a fix for a broadly-reproducible premature-firing bug.


<sub>effort S · risk low · verified: needs-nuance (medium confidence)</sub>

</details>


---

### 03. [P0] Orchestrator command execution correctness  `[ ]`

- **Priority:** P0 · **Effort:** S · **Change risk:** low · **Category:** correctness

**Why:** RunCommandNode does a synchronous WaitForExit(60_000) whose comment falsely claims it is cancel-aware — the token is only checked after the wait, so a StopGroup/cancel is unresponsive for up to 60s per running command node and blocks the dispatch loop. It also fails to expand env vars in WorkingDirectory and mis-parses commands containing quotes. Correctness + responsiveness with a misleading comment.

**Approach:** Make RunCommand async and await it in the RunCommandNode branch. Inside, use a linked CTS (caller token + CancelAfter(60s)) and await process.WaitForExitAsync(cts.Token): on caller-cancel kill the tree and rethrow OperationCanceledException; on timeout-only kill and return Failed("Timed out"). Correct the misleading comment. Expand+validate WorkingDirectory via Environment.ExpandEnvironmentVariables + Directory.Exists (mirror ProcessLauncher.ResolveWorkingDirectory); document the unescaped-double-quote constraint for the shell/powershell interpreters.

**Files:**
- `src/Salvo.Core/Services/AppOrchestrator.cs`
- `src/Salvo.Core/Services/ProcessLauncher.cs`

**Rolled-up findings:**

<details>
<summary><b>[medium]</b> RunCommand blocks up to 60s ignoring the CancellationToken; comment claims cancel-aware — `src/Salvo.Core/Services/AppOrchestrator.cs:482`</summary>


<sub>(detail cross-referenced in the full findings appendix)</sub>

</details>

<details>
<summary><b>[low]</b> RunCommand does not expand env vars in WorkingDirectory and breaks on quotes — `src/Salvo.Core/Services/AppOrchestrator.cs:468`</summary>


<sub>(detail cross-referenced in the full findings appendix)</sub>

</details>


---

### 04. [P0] Update-availability version-comparison fix  `[ ]`

- **Priority:** P0 · **Effort:** S · **Change risk:** low · **Category:** correctness

**Why:** NormaliseTo3Part does not actually normalise component count, so a 4-part release tag (which MSIX/AppInstaller versions natively are) compares as newer than the equal 3-part installed version — surfacing a perpetual, unclearable 'update available'. One-file, low-risk correctness fix worth doing independently of the broader (deferred) update-service dedup.

**Approach:** Compare only Major.Minor.Build: after parsing, rebuild both operands as new Version(v.Major, v.Minor, v.Build) (or pad/truncate both tag strings to a fixed component count before TryParse). Rename NormaliseTo3Part to reflect that it strips the prerelease suffix, and add the actual component-count normalisation. Add a test for the 4-part-tag-equals-installed case.

**Files:**
- `src/Salvo.App/Services/MsixUpdateService.cs`

**Rolled-up findings:**

<details>
<summary><b>[medium]</b> NormaliseTo3Part does not normalise part count, causing false 'update available' for 4-part tags — `src/Salvo.App/Services/MsixUpdateService.cs:163`</summary>


<sub>(detail cross-referenced in the full findings appendix)</sub>

</details>


---

### 05. [P1] Remove dead code  `[ ]`

- **Priority:** P1 · **Effort:** M · **Change risk:** low · **Category:** dead-code

**Why:** ~500 lines of confirmed-unreachable code across VMs, views, and Core: two parallel drifting add/remove-app implementations, an orphaned app-row drag/drop system wired to a no-op stub, never-activated drop-indicator scaffolding, and several unreferenced members. Pure clarity win, near-zero risk (everything is provably unreachable), and it de-risks later refactors by shrinking the surface.

**Approach:** Delete: legacy AddApp/RemoveAppAsync + AddInstalledApps/AddAppWithEditor; the orphaned Row_*/AppsContainer_* drag handlers + helpers/fields + the no-op MainWindowViewModel.ReorderApp; the static drop-bar scaffolding (GroupDropAbove/Below rects, SetDropVisible/SetGroupDropVisible, Clear*DropIndicator + call sites, _activeDropRow/_activeGroupDropItem); AppEntryEditorViewModel.RemoveFlag; AppIconCache.Set; IElevationClient.IsElevated + ElevationClient impl; AppIdentifiers.MainExecutableName; the unused LaunchApp(app, groupId) overload; and the always-true OnClosing window-count clause. Keep the live reorder-preview/ghost machinery. Verify FindVisualChild's RowRoot usage is only in deleted app code before removing.

**Files:**
- `src/Salvo.App/ViewModels/MainWindowViewModel.cs`
- `src/Salvo.App/Views/MainWindow.xaml.cs`
- `src/Salvo.App/Views/MainWindow.xaml`
- `src/Salvo.App/ViewModels/AppEntryEditorViewModel.cs`
- `src/Salvo.App/Services/AppIconCache.cs`
- `src/Salvo.Core/Elevation/IElevationClient.cs`
- `src/Salvo.Core/Elevation/ElevationClient.cs`
- `src/Salvo.Core/Branding/AppIdentifiers.cs`
- `src/Salvo.Core/Services/AppOrchestrator.cs`

**Rolled-up findings:**

<details>
<summary><b>[low]</b> Legacy apps-list commands (AddApp/RemoveApp + helpers) are unreferenced dead code — `src/Salvo.App/ViewModels/MainWindowViewModel.cs:801`</summary>


**Problem.** AddApp (801, generates AddAppCommand) and RemoveAppAsync (1016, generates RemoveAppCommand) have zero references anywhere — no XAML binding, no code-behind, no tests (grep for AddAppCommand/RemoveAppCommand across the whole repo finds only the generator sites). Their private helpers AddInstalledApps (909) and AddAppWithEditor (976) are called only from AddApp. They were superseded by the flow path AddNode("App") -> PickAppNodes/BuildAppNodeViaEditor and RemoveNodeAsync (which the live GroupFlowView.xaml binds). This is ~130 lines of parallel, drifting implementation of add/remove-app.


**Fix.** Delete AddApp, RemoveAppAsync, AddInstalledApps, and AddAppWithEditor. The flow editor's PickAppNodes/AppendAppNode/RemoveNodeAsync are the real code paths and already cover these cases.


<sub>effort S · risk low · verified: confirmed (high confidence)</sub>

</details>

<details>
<summary><b>[low]</b> Orphaned app-row drag/drop system wired to a no-op ReorderApp stub — `src/Salvo.App/Views/MainWindow.xaml.cs:275`</summary>


**Problem.** The app-row reorder handlers Row_PreviewMouseLeftButtonDown, Row_PreviewMouseMove, Row_DragOver, Row_DragLeave, Row_Drop, AppsContainer_Drop, AppsContainer_DragOver plus their helpers (CommitAppReorder, ResolveAppDropTarget, ClearAppDropIndicator, SetDropVisible, IsInsideButton) and fields (AppRowDragFormat, _dragStart, _dragSource, _dragSourceRow, _activeDropRow, _activeAppInsertAt) — roughly 200 lines — are referenced by no XAML. A grep for Row_*, AppsContainer_*, RowRoot, and the app DropAbove/DropBelow names across the whole repo finds them only in this code-behind; the apps list moved into GroupFlowView, which has its own independent drag system. The terminal call CommitAppReorder -> MainWindowViewModel.ReorderApp is itself a no-op stub (MainWindowViewModel.cs:1055: body is `_ = source; _ = targetIndex;`), so even if it were wired it would do nothing — contrast ReorderGroup (MainWindowViewModel.cs:1037) which is fully implemented. This is dead, misleading code and a public API that pretends to reorder but doesn't.


**Fix.** Recommendation is sound as written: delete the orphaned app-row drag handlers, their helpers (CommitAppReorder, ResolveAppDropTarget, ClearAppDropIndicator, SetDropVisible, IsInsideButton), and fields (AppRowDragFormat, _dragStart, _dragSource, _dragSourceRow, _activeDropRow, _activeAppInsertAt) from MainWindow.xaml.cs, and remove the no-op ReorderApp from MainWindowViewModel; keep the shared BeginReorderPreview/UpdateReorderPreview/EndReorderPreview, ShowDragGhost, FindVisualChild, FindAncestor used by the live group drag. Two clarifications: (1) risk is lower than "medium" — everything being removed is unreachable, ReorderApp has no live callers, and SetDropVisible/ClearAppDropIndicator are provably no-ops (_activeDropRow is never set non-null), so this is a mechanical, near-zero-risk deletion; (2) verify FindVisualChild's "RowRoot" lookup usage is only in the deleted app code (group drag uses "Bd") before removing any group-name references, which it is. Severity is better characterized as low: it is confirmed dead code with no runtime or correctness impact, its value is purely maintainability/clarity.


<sub>effort M · risk medium · verified: confirmed (high confidence)</sub>

</details>

<details>
<summary><b>[low]</b> Group drop-indicator rectangles and their show/clear plumbing are never activated — `src/Salvo.App/Views/MainWindow.xaml.cs:757`</summary>


**Problem.** _activeGroupDropItem (field, line 37) and _activeDropRow (line 32) are only ever read and set to null — grep confirms no code path assigns them a non-null value. Consequently SetGroupDropVisible (757) and SetDropVisible (593) are only ever invoked with visible:false, so the GroupDropAbove/GroupDropBelow rectangles declared in MainWindow.xaml (lines 409-422, plus the app DropAbove/DropBelow) can never become visible. The static drop-bar approach was superseded by the sliding reorder-preview, but the indicator markup and methods were left behind as no-ops, adding confusion to an already dense drag file.


**Fix.** Delete the dead static-drop-bar scaffolding, now fully superseded by the sliding reorder-preview: (1) remove the `GroupDropAbove`/`GroupDropBelow` rectangles from the GroupsList ControlTemplate (MainWindow.xaml 408-422); (2) delete `SetDropVisible` (593), `SetGroupDropVisible` (757), and the `_activeDropRow` (32) and `_activeGroupDropItem` (37) fields; (3) since their bodies become permanently unreachable, delete `ClearAppDropIndicator` (460) and `ClearGroupDropIndicator` (749) entirely and drop their call sites (329, 350, 388, 402, 658, 679, 729) rather than keeping empty methods. Note: there are NO app-level `DropAbove`/`DropBelow` rectangles in the XAML to remove — the finding's mention of them is inaccurate; `SetDropVisible` already references non-existent named elements, so removing it loses nothing. Leave the reorder-preview machinery (`UpdateReorderPreview`, `_activeAppInsertAt`, `_activeGroupInsertAt`, ghost adorner) untouched — that is the live path.


<sub>effort S · risk low · verified: confirmed (high confidence)</sub>

</details>

<details>
<summary><b>[low]</b> OnClosing guard 'Application.Current.Windows.Count > 0' is always true — `src/Salvo.App/Views/MainWindow.xaml.cs:265`</summary>


**Problem.** In OnClosing, the condition `_viewModel.MinimizeToTrayOnClose && Application.Current.Windows.Count > 0` gates hiding-to-tray. While MainWindow is closing it is still a member of Application.Current.Windows, so Count is always >= 1 and the second clause can never be false. The guard therefore has no effect and misleads a reader into thinking there is a window-count edge case being handled.


**Fix.** Drop the always-true `&& Application.Current.Windows.Count > 0` clause so the guard reads `if (_viewModel.MinimizeToTrayOnClose)`. If a real "no other windows remain" behavior was ever intended, that is not what MinimizeToTrayOnClose means here, so do not resurrect it speculatively — just remove the inert clause.


<sub>effort S · risk low · verified: confirmed (high confidence)</sub>

</details>

<details>
<summary><b>[low]</b> Unused private RemoveFlag method — `src/Salvo.App/ViewModels/AppEntryEditorViewModel.cs:351`</summary>


**Problem.** The static RemoveFlag(string args, string flag) at line 351 is never called (grep across src finds only its definition). It duplicates most of the token/flag-matching logic in ContainsFlag/NormalizeToken and is dead weight the compiler doesn't flag because it's referenced by neither test nor caller.


**Fix.** Delete RemoveFlag (lines 351-382). The shared helpers SplitTokens/NormalizeToken/StripQuotes stay because ContainsFlag still uses them; no other change needed.


<sub>effort S · risk low · verified: confirmed (high confidence)</sub>

</details>

<details>
<summary><b>[low]</b> AppIconCache.Set is never called — `src/Salvo.App/Services/AppIconCache.cs:22`</summary>


**Problem.** The public method AppIconCache.Set(string, BitmapSource?) (lines 22-26) has no callers — a repo-wide search for AppIconCache.Set returns nothing; every consumer (AppIconLoader, WindowsStartupIconLoader, BenchmarksViewModel, GroupIconView) uses only AppIconCache.Get. It is unused API surface on an internal static helper.


**Fix.** Delete Set (and its null/whitespace guard). Get already handles cache population on miss, so no caller behavior changes. If a future feature needs to pre-seed or invalidate cache entries, reintroduce it then.


<sub>effort S · risk low · verified: confirmed (high confidence)</sub>

</details>

<details>
<summary><b>[low]</b> IElevationClient.IsElevated is never consumed and duplicates ElevationDetector.IsElevated — `src/Salvo.Core/Elevation/IElevationClient.cs:5`</summary>


**Problem.** IElevationClient declares bool IsElevated (src/Salvo.Core/Elevation/IElevationClient.cs:5), implemented in ElevationClient (src/Salvo.Core/Elevation/ElevationClient.cs:24-32) with a WindowsIdentity/WindowsPrincipal admin-role check. Every actual elevation-state read in the codebase goes through the static Salvo.Core.Launch.ElevationDetector.IsElevated (App.xaml.cs:361, MainWindowViewModel.cs:112, RegistryRunValueEditorViewModel.cs:128, WindowsStartupEntryViewModel.cs:33, EtwResourceMonitor.cs:59); a grep for '.IsElevated' shows no call site uses the injected IElevationClient.IsElevated. The member is dead and its implementation re-derives (without ElevationDetector's Lazy caching and try/catch fallback) logic that already exists in one canonical place.


**Fix.** Delete IsElevated from IElevationClient and ElevationClient; keep ElevationDetector.IsElevated as the single source of truth.


<sub>effort S · risk low · verified: confirmed (high confidence)</sub>

</details>

<details>
<summary><b>[low]</b> AppIdentifiers.MainExecutableName is defined but never referenced — `src/Salvo.Core/Branding/AppIdentifiers.cs:9`</summary>


**Problem.** AppIdentifiers.MainExecutableName = "Salvo.exe" (line 9) is a public const, but a repo-wide grep finds no usage anywhere in src — the sibling ElevatorExecutableName is used by ElevationPaths, but the main-exe constant is dead. A hardcoded executable name that exists only as an unused constant is a latent trap: future code is likely to hardcode "Salvo.exe" again rather than discover this, and the constant can silently rot if the exe is ever renamed.


**Fix.** Remove the unused MainExecutableName const. Do NOT adopt the alternative of wiring existing exe-path resolution to it — App.xaml.cs, MainWindowViewModel.cs, and TaskSchedulerAutoStartService.cs correctly resolve the running executable via Environment.ProcessPath / MainModule.FileName, which is more robust than a hardcoded "Salvo.exe" and should be left as-is.


<sub>effort S · risk low · verified: confirmed (high confidence)</sub>

</details>

<details>
<summary><b>[low]</b> Public LaunchApp(AppEntry, string?) overload is never called — `src/Salvo.Core/Services/AppOrchestrator.cs:67`</summary>


**Problem.** AppOrchestrator exposes two public launch overloads: LaunchApp(app) (line 61) and LaunchApp(app, groupId) (line 67). Only the single-arg form is on IAppOrchestrator and is the only one used in production (MainWindowViewModel.cs:784) and tests. The 2-arg overload — the only place groupId would flow in for a single-app launch — has no callers; group launches go through LaunchGroupAsync which calls LaunchAppCore directly. It is unused public surface that implies a capability (telemetry attribution by group for ad-hoc launches) that isn't wired up.


**Fix.** Either remove the 2-arg overload, or wire it up (have MainWindowViewModel pass the owning group id so single-app launches are attributed) and add it to IAppOrchestrator if it is meant to be part of the contract.


<sub>effort S · risk low · verified: confirmed (high confidence)</sub>

</details>


---

### 06. [P1] Retire Velopack/Burn installer (delete StartupGroups.Installer.UI)  `[ ]`

- **Priority:** P1 · **Effort:** M · **Change risk:** low · **Category:** dead-code

**Why:** The entire StartupGroups.Installer.UI Burn BA is superseded by the shipped MSIX path, is already non-functional against the current app (probes StartupGroups.exe/AppData while the app builds Salvo.exe and uses Salvo folders), and only survives via slnx + one test's ProjectReference — yet it forces a self-contained win-x64 WPF publish + WiX restore into every CI build. This is the biggest single dead-weight removal and the MSIX plan already tracks it for Phase 3.

**Approach:** After confirming no workflow/product project consumes the Burn payloads, delete src/StartupGroups.Installer.UI/ (and the two installer/StartupGroups.* dirs), remove the Salvo.slnx entry, drop the ProjectReference from Salvo.App.Tests and delete MsiMessageFilterTests.cs (do not port MsiMessageFilter — it has no MSIX caller). All the branding/staleness/channel-picker/RID-SelfContained sub-findings resolve automatically. This shrinks CI restore/build and eliminates the WiX + self-contained-runtime dependency. Do NOT invest in rebranding or fixing this project first.

**Files:**
- `src/StartupGroups.Installer.UI/`
- `installer/StartupGroups.Bundle/`
- `installer/StartupGroups.Installer/`
- `Salvo.slnx`
- `tests/Salvo.App.Tests/Salvo.App.Tests.csproj`
- `tests/Salvo.App.Tests/MsiMessageFilterTests.cs`

**Rolled-up findings:**

<details>
<summary><b>[low]</b> Entire StartupGroups.Installer.UI project is superseded and safe to delete — `src/StartupGroups.Installer.UI/StartupGroups.Installer.UI.csproj`</summary>


**Problem.** This project is the WiX/Burn managed Bootstrapper Application (InstallerBootstrapperApplication + 6 WPF views) for the Velopack/Burn installer that the MSIX migration retires in Phase 3. MSIX has already landed as the primary path: installer/Msix/build.ps1 runs in ci.yml:63 and release.yml, MsixUpdateService.cs and installer/Msix/Package.appxmanifest exist, and Windows PackageManager now owns download/verify/install/uninstall. The ci.yml comment (lines 128-132) states the Burn-bundle wrapper that packaged this BA 'was removed during the StartupGroups -> Salvo rename; canary now ships the lean Velopack Setup.exe directly.' No GitHub workflow builds or ships the BA exe, and no product project (Salvo.App/Salvo.Core/Salvo.Elevator) references it. It survives only via Salvo.slnx and one unit test. This is orphaned dead code.


**Fix.** As part of Phase 3 MSIX cleanup, retire the entire retired Burn installer chain together — not just Installer.UI. Delete src/StartupGroups.Installer.UI/, installer/StartupGroups.Bundle/, and installer/StartupGroups.Installer/, remove the Salvo.slnx entry, remove the ProjectReference from tests/Salvo.App.Tests, and also delete tests/Salvo.App.Tests/MsiMessageFilterTests.cs (otherwise the test project fails to compile). Confirm nothing else consumes the Burn payloads first. Treat this as a low-severity maintenance cleanup, not a high-severity issue.


<sub>effort M · risk low · verified: needs-nuance (high confidence)</sub>

</details>

<details>
<summary><b>[low]</b> Retired installer project still in the solution; every CI build compiles dead WiX/WPF code — `Salvo.slnx:6`</summary>


<sub>(detail cross-referenced in the full findings appendix)</sub>

</details>

<details>
<summary><b>[low]</b> Installer.UI hardcodes RuntimeIdentifier + SelfContained, pulling full runtime into every build — `src/StartupGroups.Installer.UI/StartupGroups.Installer.UI.csproj:22`</summary>


<sub>(detail cross-referenced in the full findings appendix)</sub>

</details>

<details>
<summary><b>[low]</b> Test project pins the retired installer into the build graph for a single pure helper — `tests/Salvo.App.Tests/Salvo.App.Tests.csproj:17`</summary>


**Problem.** Salvo.App.Tests.csproj:17 holds a ProjectReference to the entire StartupGroups.Installer.UI project solely so MsiMessageFilterTests.cs can call the static MsiMessageFilter.LooksLikeRawGuid. This is the only consumer of the whole project besides the solution listing, and it is the thing that will break the build the moment the project is deleted. The filter itself is also dead once the BA goes: its only caller is OnExecuteMsiMessage (InstallerBootstrapperApplication.cs:756), and MSIX installs raise no MSI ExecuteMsiMessage/ActionStart events, so there is nothing to filter under the new pipeline.


**Fix.** Keep the recommendation as-is but scope it as a cleanup checklist item for the installer retirement, not a standing defect: during Phase 3 of the MSIX migration (which docs/MIGRATION_PLAN.md already lists), delete tests/Salvo.App.Tests/MsiMessageFilterTests.cs and remove the ProjectReference on csproj line 17 alongside deleting src/StartupGroups.Installer.UI/. Do not port MsiMessageFilter to the app — it has no caller under MSIX. No action is needed while the Burn BA still ships.


<sub>effort S · risk low · verified: needs-nuance (high confidence)</sub>

</details>

<details>
<summary><b>[low]</b> Installer launches/kills StartupGroups.exe, but the app now builds Salvo.exe — `src/StartupGroups.Installer.UI/InstallerBootstrapperApplication.cs:345`</summary>


**Problem.** ResolveInstalledAppPath (lines 345-348) probes only for 'StartupGroups.exe', and StopRunningInstances (line 565) calls Process.GetProcessesByName("StartupGroups"). After the Salvo rename, Salvo.App.csproj sets <AssemblyName>Salvo</AssemblyName>, so the shipped executable is Salvo.exe. Even if this BA still ran, it would never find, stop, or relaunch the running app. This confirms the project is already non-functional against the current app, not merely deprecated — reinforcing delete-rather-than-fix.


**Fix.** Keep the observation but fold it into the primary "remove StartupGroups.Installer.UI project" finding as supporting evidence rather than a separate medium item. If for some reason the Burn/Velopack installer must remain live during the MSIX transition, then the correct fix is to update both the exe candidate paths and GetProcessesByName to "Salvo" (and the %LocalAppData% folder name), since a name mismatch there would silently skip closing/relaunching the running app during updates. Otherwise, delete the project and no rewiring is warranted.


<sub>effort S · risk low · verified: confirmed (high confidence)</sub>

</details>

<details>
<summary><b>[low]</b> First-run seed / cleanup target pre-rename StartupGroups AppData folders — `src/StartupGroups.Installer.UI/InstallerBootstrapperApplication.cs:700`</summary>


<sub>(detail cross-referenced in the full findings appendix)</sub>

</details>

<details>
<summary><b>[low]</b> Channel-picker UI contradicts the locked 'drop the canary channel' decision — `src/StartupGroups.Installer.UI/ViewModels/CustomizeViewModel.cs:9`</summary>


**Problem.** CustomizeViewModel exposes an InstallerUpdateChannel enum (lines 9-13) and a Stable/Canary picker (lines 32-38); InstallerBootstrapperApplication.ApplyDefaultChannelFromBundle (lines 490-511) pre-selects it from a Burn bundle variable and TrySeedFirstRunSettings (lines 715-722) writes updateChannel into settings.json. The MSIX migration decision explicitly drops the canary channel concept and MSIX builds are single-track (MsixUpdateService docstring: 'MSIX builds are single-track'), and the recent commit 36461be 'retire update-channel picker' removed the equivalent picker from the app. The installer's channel UI, bundle-variable plumbing, and channel seeding are dead surface.


**Fix.** Do not raise or act on this as a standalone dead-code finding. It is not dead today: the installer feeds settings.json updateChannel into the still-active VelopackUpdateService path used by all non-MSIX installs (App.xaml.cs CreateUpdateService keeps Velopack "alive while the MSIX migration phases through"). Removing the picker piecemeal would break channel selection for Velopack installs, especially now that the in-app settings dropdown is already gone. The correct disposition is to retire it as a unit with the entire StartupGroups.Installer.UI Burn project during the migration's already-planned Phase 3 legacy cleanup — which the MSIX plan already tracks. Not worth a code-review flag.


<sub>effort S · risk low · verified: needs-nuance (high confidence)</sub>

</details>

<details>
<summary><b>[low]</b> Installer chrome still branded 'Startup Groups' post-rename (delete, not rebrand) — `src/StartupGroups.Installer.UI/ViewModels/LicenseViewModel.cs:17`</summary>


<sub>(detail cross-referenced in the full findings appendix)</sub>

</details>

<details>
<summary><b>[low]</b> Installer.UI still carries pre-rename name/namespace/assembly — `src/StartupGroups.Installer.UI/StartupGroups.Installer.UI.csproj:7`</summary>


<sub>(detail cross-referenced in the full findings appendix)</sub>

</details>


---

### 07. [P1] Centralize magic numbers, timeouts & hardcoded constants  `[ ]`

- **Priority:** P1 · **Effort:** M · **Change risk:** low · **Category:** magic-number

**Why:** The codebase deliberately centralizes tuning values (Timeouts, BenchmarkPolicy, UiMetrics, AppBranding, AppPaths), but a handful of policy knobs and defaults leaked out as inline literals — some duplicated across files where they can silently desync (regression thresholds vs sibling VM, default group icon in 6 places, the cross-process --payload flag). Mechanical, low-risk, and it restores the established convention before the dedup passes touch the same sites.

**Approach:** Add Timeouts.MainWindowResponsivenessProbe (500ms), RunCommandExecution (60s), ProcessKillGrace (5s) and consume via the ms overloads. Replace AppBenchmarkSummaryViewModel's 2.0/3 with BenchmarkPolicy.RegressionRatio/RegressionMinSampleSize. Introduce AppBranding.DefaultGroupIcon for the 6 'Apps24' string-default sites (leave the SymbolRegular.Apps24 render fallbacks). Add AppPaths.SettingsFileName/SettingsFilePath and route SettingsStore through it. Add UiMetrics.ListIconSize (32) for the two duplicated call sites only. Define a single '--payload' constant in Core referenced by both ElevationClient and Salvo.Elevator (the genuine cross-process pair). Name only the flow drag merge-bands as local consts. Skip single-use literals (48/16 icon sizes, 0.35 ghost opacity, already-centralized flags).

**Files:**
- `src/Salvo.Core/Services/Timeouts.cs`
- `src/Salvo.Core/Launch/Probes/MainWindowProbe.cs`
- `src/Salvo.Core/Services/AppOrchestrator.cs`
- `src/Salvo.Core/Services/ProcessInspector.cs`
- `src/Salvo.App/ViewModels/AppBenchmarkSummaryViewModel.cs`
- `src/Salvo.App/ViewModels/GroupViewModel.cs`
- `src/Salvo.Core/Branding/AppBranding.cs`
- `src/Salvo.Core/Services/AppPaths.cs`
- `src/Salvo.App/Services/AppIconCache.cs`
- `src/Salvo.App/Elevation/ElevationClient.cs`
- `src/Salvo.Elevator/Program.cs`

**Rolled-up findings:**

<details>
<summary><b>[low]</b> Hardcoded 500ms SendMessageTimeout bypasses the centralized Timeouts class — `src/Salvo.Core/Launch/Probes/MainWindowProbe.cs:72`</summary>


<sub>(detail cross-referenced in the full findings appendix)</sub>

</details>

<details>
<summary><b>[low]</b> Hardcoded process-wait timeouts (60000ms/5000ms) despite a centralized Timeouts class — `src/Salvo.Core/Services/AppOrchestrator.cs:482`</summary>


<sub>(detail cross-referenced in the full findings appendix)</sub>

</details>

<details>
<summary><b>[low]</b> Regression thresholds hardcoded while sibling VM uses BenchmarkPolicy constants — `src/Salvo.App/ViewModels/AppBenchmarkSummaryViewModel.cs:47`</summary>


<sub>(detail cross-referenced in the full findings appendix)</sub>

</details>

<details>
<summary><b>[low]</b> Default group icon 'Apps24' is a magic string duplicated across many files — `src/Salvo.App/ViewModels/GroupViewModel.cs:26`</summary>


<sub>(detail cross-referenced in the full findings appendix)</sub>

</details>

<details>
<summary><b>[low]</b> settings.json filename not centralized in AppPaths — `src/Salvo.App/Services/SettingsStore.cs:17`</summary>


<sub>(detail cross-referenced in the full findings appendix)</sub>

</details>

<details>
<summary><b>[low]</b> Icon pixel sizes hardcoded at call sites (32 duplicated) — `src/Salvo.App/Services/AppIconCache.cs:17`</summary>


<sub>(detail cross-referenced in the full findings appendix)</sub>

</details>

<details>
<summary><b>[low]</b> Command-line flags scattered across four+ files; --payload duplicated cross-process — `src/Salvo.App/App.xaml.cs:159`</summary>


<sub>(detail cross-referenced in the full findings appendix)</sub>

</details>

<details>
<summary><b>[low]</b> Drag merge-band thresholds inlined instead of named local consts — `src/Salvo.App/Views/Flow/GroupFlowView.xaml.cs:411`</summary>


<sub>(detail cross-referenced in the full findings appendix)</sub>

</details>

<details>
<summary><b>[low]</b> 'shell:' URI-scheme prefix literal duplicated across PathResolver and ProcessLauncher — `src/Salvo.Core/Services/PathResolver.cs:12`</summary>


<sub>(detail cross-referenced in the full findings appendix)</sub>

</details>


---

### 08. [P1] Windows startup registry single-source-of-truth + HKCU 32-bit fix  `[ ]`

- **Priority:** P1 · **Effort:** M · **Change risk:** medium · **Category:** correctness

**Why:** The Run/StartupApproved key paths and the source->(hive,view,approved-key) mapping are copy-pasted across RegistryRunValueWriter, WindowsStartupService, and the elevator — directly contradicting the writer's own 'single source of truth' comment — and the delete path was never centralized. This drift already produced a real bug: HKCU 32-bit entries are read from a different physical key than they are edited/deleted from, so editing fails and deleting is a silent no-op. Elevator paths are post-UAC where mistakes are hardest to see.

**Approach:** Extract one internal static class holding RunPath/RunWow64Path/StartupApprovedRun/StartupApprovedRun32/StartupApprovedFolder and the shared source-resolution, and add RegistryRunValueWriter.Delete(source, name) called by both WindowsStartupService.TryRemove (registry-Run sources only; keep the StartupFolder branch local) and the elevator's delete. Delete the dead RunWow64Path duplicate. Fix HKCU 32-bit by aligning the WRITE side to the READ side: address all *32 sources via the literal WOW6432Node path with the default view (matching Enumerate) in ResolveLocation, GetSiblingValueNames, and the elevator — do NOT change Enumerate to the 32-bit view. Also add the TryAddUserRunEntry existence guard here (mirror RegistryRunValueWriter.Write) so it stops silently overwriting a colliding Run value, and surface a rename/confirm prompt in AddEntryAsync.

**Files:**
- `src/Salvo.Core/WindowsStartup/RegistryRunValueWriter.cs`
- `src/Salvo.Core/WindowsStartup/WindowsStartupService.cs`
- `src/Salvo.Elevator/Program.cs`

**Rolled-up findings:**

<details>
<summary><b>[medium]</b> Run/StartupApproved paths and delete logic duplicated across 3 files — `src/Salvo.Core/WindowsStartup/RegistryRunValueWriter.cs:13`</summary>


<sub>(detail cross-referenced in the full findings appendix)</sub>

</details>

<details>
<summary><b>[low]</b> HKCU 32-bit Run entries read from a different physical key than edited/deleted — `src/Salvo.Core/WindowsStartup/WindowsStartupService.cs:20`</summary>


<sub>(detail cross-referenced in the full findings appendix)</sub>

</details>

<details>
<summary><b>[low]</b> TryAddUserRunEntry silently overwrites an existing Run value of the same name — `src/Salvo.Core/WindowsStartup/WindowsStartupService.cs:108`</summary>


**Problem.** TryAddUserRunEntry (lines 108-136) calls key.SetValue(name, command, ...) with no existence check, so it silently overwrites any existing HKCU Run value whose name collides. The sole caller, WindowsStartupViewModel.AddEntryAsync (lines 174-182), derives name from Path.GetFileNameWithoutExtension(path) with no collision guard. So adding, e.g., a second executable named 'updater.exe' silently replaces the command of an unrelated existing autostart entry — data loss the user never sees (the op reports 'Added'). Note RegistryRunValueWriter.Write already guards duplicates (lines 54-57), so the two write paths are inconsistent about this.


**Fix.** Keep the proposed fix: in TryAddUserRunEntry, after opening the Run key, check key.GetValueNames() for a case-insensitive match on name and return Failed (mirroring RegistryRunValueWriter.Write lines 54-57) before calling SetValue. This service-level guard is the load-bearing change. Secondarily, improve the VM (AddEntryAsync) to surface a rename-or-confirm prompt on collision rather than a dead-end error, since a bare failure leaves the user no way to add a legitimately distinct executable that happens to share a base name.


<sub>effort S · risk low · verified: confirmed (high confidence)</sub>

</details>

<details>
<summary><b>[low]</b> Registry-delete path logic hand-rolled in the elevator instead of centralized — `src/Salvo.Elevator/Program.cs:94`</summary>


<sub>(detail cross-referenced in the full findings appendix)</sub>

</details>


---

### 09. [P2] Core consistency & robustness cleanup  `[ ]`

- **Priority:** P2 · **Effort:** M · **Change risk:** low · **Category:** consistency

**Why:** A grab-bag of low-risk Core/App consistency items that align outliers with established conventions: reflection JSON vs the source-gen context, a redundant per-property enum converter, per-get command allocation vs the [RelayCommand] convention, a Process-handle-hygiene gap, SQLite pragmas, and a misleading store doc. None are active correctness bugs but each is a small trap or future-trim/AOT hazard.

**Approach:** Register KnownAppsFile on ConfigurationJsonContext and deserialize via the source-gen context (drop the ad-hoc reflection options). Remove the redundant [JsonConverter] on AppEntry.Kind (UseStringEnumConverter already covers it). Cache TrayViewModel's ShowMainWindow/Exit commands in readonly fields instead of allocating per get. Make ProcessInspector.IsRunning dispose the matched Process list in a finally (consistency with the other two callers). Add a private OpenConnection helper to SqliteLaunchBenchmarkStore that applies PRAGMA busy_timeout on every open, enable journal_mode=WAL once in init, drop Cache=Shared; and correct the InitializeAsync doc that falsely claims re-init support. Optionally align StartupOperationResult's Succeeded/NeedsAdmin naming with OperationResult (do NOT merge the two types).

**Files:**
- `src/Salvo.Core/Services/KnownAppsDatabase.cs`
- `src/Salvo.Core/Serialization/ConfigurationJsonContext.cs`
- `src/Salvo.Core/Models/AppEntry.cs`
- `src/Salvo.App/ViewModels/TrayViewModel.cs`
- `src/Salvo.Core/Services/ProcessInspector.cs`
- `src/Salvo.Core/Launch/SqliteLaunchBenchmarkStore.cs`
- `src/Salvo.Core/WindowsStartup/StartupOperationResult.cs`

**Rolled-up findings:**

<details>
<summary><b>[low]</b> KnownAppsDatabase uses reflection-based JSON while the rest of Core uses a source-gen context — `src/Salvo.Core/Services/KnownAppsDatabase.cs:86`</summary>


**Problem.** Load() calls `JsonSerializer.Deserialize<KnownAppsFile>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })` (lines 86-89) — reflection-based serialization with ad-hoc options. Everywhere else the codebase deliberately uses the source-generated ConfigurationJsonContext (JsonConfigStore.cs:47/112) with camelCase + polymorphic type registration. KnownAppsFile/KnownApp/KnownAppMatch/KnownArgument are not registered in ConfigurationJsonContext. This is inconsistent, pulls the reflection-based serializer into an app that is otherwise on a cold-start optimization branch (perf/cold-start, R2R), and forecloses any future PublishTrimmed/AOT because the reflection path would silently drop members.


**Fix.** Register the four DTOs (KnownAppsFile, KnownApp, KnownAppMatch, KnownArgument) on the existing ConfigurationJsonContext — adding [JsonSerializable(typeof(KnownAppsFile))] is sufficient since the source generator will pull in the nested types — and deserialize via JsonSerializer.Deserialize(stream, ConfigurationJsonContext.Default.KnownAppsFile), dropping the hand-built JsonSerializerOptions. The context's CamelCase policy matches the JSON keys, so no [JsonPropertyName] attributes are needed beyond what exists. This is a straightforward, low-risk consistency change; frame it as hygiene/future-trim-readiness rather than a cold-start win, since the load path is lazy and editor-only.


<sub>effort M · risk low · verified: confirmed (high confidence)</sub>

</details>

<details>
<summary><b>[low]</b> Redundant per-property enum converter duplicates the context-wide UseStringEnumConverter — `src/Salvo.Core/Models/AppEntry.cs:9`</summary>


<sub>(detail cross-referenced in the full findings appendix)</sub>

</details>

<details>
<summary><b>[low]</b> Tray command properties allocate a new RelayCommand on every getter access — `src/Salvo.App/ViewModels/TrayViewModel.cs:48`</summary>


**Problem.** ShowMainWindowCommand (line 48) and ExitCommand (line 49) are expression-bodied getters that `new RelayCommand(...)` on every read. They are consumed by TrayMenuFactory.Build (TrayMenuFactory.cs:48,53), which is rebuilt on every config change, and ShowMainWindowCommand is also invoked from App.OnStartup:130 — each read creates a distinct command instance, so command identity/CanExecute caching is lost and each menu rebuild churns fresh allocations.


**Fix.** Cache each command in an init-once readonly field (e.g. `private readonly RelayCommand _showMainWindowCommand;` assigned in the constructor, exposed via `public ICommand ShowMainWindowCommand => _showMainWindowCommand;`), matching how TrayGroupItem already stores its Launch/Stop commands. Frame this as a code-consistency cleanup rather than a performance fix — the allocation savings are negligible and there is no CanExecute behavior to preserve.


<sub>effort S · risk low · verified: needs-nuance (high confidence)</sub>

</details>

<details>
<summary><b>[low]</b> IsRunning does not dispose matched Process objects on the AUMID path — `src/Salvo.Core/Services/ProcessInspector.cs:34`</summary>


<sub>(detail cross-referenced in the full findings appendix)</sub>

</details>

<details>
<summary><b>[low]</b> SQLite store uses Shared cache with no WAL and no busy timeout — `src/Salvo.Core/Launch/SqliteLaunchBenchmarkStore.cs:58`</summary>


<sub>(detail cross-referenced in the full findings appendix)</sub>

</details>

<details>
<summary><b>[low]</b> Faulted schema-init task cached forever; doc falsely claims re-init support — `src/Salvo.Core/Launch/SqliteLaunchBenchmarkStore.cs:73`</summary>


<sub>(detail cross-referenced in the full findings appendix)</sub>

</details>

<details>
<summary><b>[low]</b> Two parallel operation-result types with divergent status vocabularies — `src/Salvo.Core/WindowsStartup/StartupOperationResult.cs:10`</summary>


**Problem.** The codebase models 'operation outcome (success / needs-admin / failed) + human message' with two independent types. OperationResult (Models/OperationResult.cs:14) is a `sealed record` class with a 5-value `OperationStatus` enum (Succeeded, AlreadyInState, NotFound, NeedsElevation, Failed), an `IsSuccess` property, and a `NeedsElevation()` factory; it is the return type of IAppOrchestrator launch/stop. StartupOperationResult (WindowsStartup/StartupOperationResult.cs:10) is a `readonly record struct` with a 3-value `StartupOperationStatus` enum (Ok, NeedsAdmin, Failed), a `Succeeded` property, and a `NeedsAdmin()` factory; it is the return type of every IWindowsStartupService method (IWindowsStartupService.cs:7-15) and RegistryRunValueWriter. So the same 'needs elevation' concept is spelled `NeedsElevation`/`NeedsAdmin`, the same 'did it work' predicate is `IsSuccess`/`Succeeded`, and one is a class while the other is a struct — callers that touch both subsystems must keep two mental models.


**Fix.** Do not merge the two types — that would force launch-domain fields (Source, Metrics) and states (AlreadyInState, NotFound) onto the registry layer and lose the deliberate lightweight-struct choice. If any change is wanted, limit it to aligning the shared vocabulary for readability: rename StartupOperationResult's Succeeded to IsSuccess and NeedsAdmin to NeedsElevation so the two domains read consistently. This is a minor, optional cosmetic tidy, not a medium-severity duplication defect, and could equally be left as-is since the two subsystems have no shared caller.


<sub>effort M · risk medium · verified: needs-nuance (high confidence)</sub>

</details>


---

### 10. [P1] Cold-start & runtime perf deferral  `[ ]`

- **Priority:** P1 · **Effort:** L · **Change risk:** medium · **Category:** performance

**Why:** On the perf/cold-start branch, several startup and steady-state paths do redundant heavy work on the UI thread: config.json parsed+migrated 3x before first paint, settings.json parsed twice, the whole telemetry/ETW/SQLite stack eagerly constructed even in tray-only mode, and RefreshRunningStates taking O(apps x all_processes) process-table snapshots on the UI thread every 3s. Providers also run sequentially. Medium-risk items are isolated and independently landable.

**Approach:** Load Configuration once in OnStartup and hand the same instance to both view-models; delete the discarded Load() at App.xaml.cs:110 (keep BeginWatching); change TrayViewModel's Changed handler to consume the event payload instead of re-reading disk. Load AppSettings once pre-host and feed it into the DI SettingsStore registration. Make RefreshRunningStates take ONE Process.GetProcesses() snapshot per tick (name->processes map + single AUMID pass) and run the sweep on a background thread, marshaling only bool[] back. Fan the composite provider's three providers out with Task.WhenAll. Make the benchmark/telemetry/ETW stack lazy (Lazy<>/factory) so TraceEvent+SQLite stay off first-paint. Memoize LaunchSession.EnumerateDescendantPids behind a short thread-safe TTL. LoadHistoricalBenchmarks single-query is optional tidy.

**Files:**
- `src/Salvo.App/ViewModels/MainWindowViewModel.cs`
- `src/Salvo.App/ViewModels/TrayViewModel.cs`
- `src/Salvo.App/App.xaml.cs`
- `src/Salvo.Core/Services/JsonConfigStore.cs`
- `src/Salvo.Core/Services/ProcessInspector.cs`
- `src/Salvo.Core/Services/CompositeInstalledAppsProvider.cs`
- `src/Salvo.Core/Launch/LaunchSession.cs`

**Rolled-up findings:**

<details>
<summary><b>[medium]</b> RefreshRunningStates snapshots the full process table once per app every 3s on the UI thread — `src/Salvo.App/ViewModels/MainWindowViewModel.cs:726`</summary>


<sub>(detail cross-referenced in the full findings appendix)</sub>

</details>

<details>
<summary><b>[medium]</b> Composite provider enumerates independent providers sequentially and double-sorts — `src/Salvo.Core/Services/CompositeInstalledAppsProvider.cs:17`</summary>


**Problem.** EnumerateAsync awaits each provider one at a time in a foreach (lines 17-22). The three wired providers (App.xaml.cs:315-319: ShellInstalledAppsProvider doing STA COM enumeration of every installed app, WindowsServicesProvider enumerating all services with a per-service registry ImagePath lookup, ScoopInstalledAppsProvider scanning the filesystem) are fully independent, so their latencies add up on the app-picker path (AddAppPickerViewModel.cs:164). Separately, each provider already sorts its own results (ScoopInstalledAppsProvider:80, ShellInstalledAppsProvider:116, WindowsServicesProvider:73) and the composite re-sorts the merged list (line 24), so the per-provider sorts are wasted work whenever they run through the composite.


**Fix.** Fan the three providers out with Task.WhenAll, passing the same CancellationToken to each, then concatenate and apply the single final sort. This is the substantive win. Preserve cooperative cancellation (the loop's ThrowIfCancellationRequested is replaced by token propagation into each provider). The double-sort cleanup is optional and low-value on its own — dropping per-provider sorts saves only microseconds and the providers may still be used standalone, so leaving them (or documenting that the composite always re-sorts) is fine.


<sub>effort S · risk low · verified: confirmed (high confidence)</sub>

</details>

<details>
<summary><b>[medium]</b> Every probe independently re-enumerates the descendant PID set each poll — `src/Salvo.Core/Launch/LaunchSession.cs:103`</summary>


<sub>(detail cross-referenced in the full findings appendix)</sub>

</details>

<details>
<summary><b>[low]</b> JsonConfigStore.Load() has no caching; config.json read+parsed 3x on the startup UI thread — `src/Salvo.Core/Services/JsonConfigStore.cs:26`</summary>


<sub>(detail cross-referenced in the full findings appendix)</sub>

</details>

<details>
<summary><b>[low]</b> Whole telemetry/benchmark/ETW subsystem eagerly constructed at startup, even tray-only — `src/Salvo.App/ViewModels/MainWindowViewModel.cs:67`</summary>


<sub>(detail cross-referenced in the full findings appendix)</sub>

</details>

<details>
<summary><b>[low]</b> settings.json read and deserialized twice during startup — `src/Salvo.App/App.xaml.cs:366`</summary>


<sub>(detail cross-referenced in the full findings appendix)</sub>

</details>

<details>
<summary><b>[low]</b> LoadHistoricalBenchmarksAsync issues N sequential SQLite queries at startup — `src/Salvo.App/ViewModels/MainWindowViewModel.cs:686`</summary>


<sub>(detail cross-referenced in the full findings appendix)</sub>

</details>


---

### 11. [P1] Close localization gaps  `[ ]`

- **Priority:** P1 · **Effort:** L · **Change risk:** low · **Category:** consistency

**Why:** The flow editor — a primary, central authoring view — is essentially all hardcoded English while the app ships 10 cultures including RTL, and a scatter of VM dialog strings and one admin-pill x:Static path are unlocalized or go stale on live language switch. Non-Latin users see an English flow editor. The flow view is still under active development (Phases B-E), so this can batch with that work but is a real, tracked gap.

**Approach:** Route GroupFlowView.xaml text through {loc:Translate}, localizing whole/format strings for composed sentences (e.g. the Wait and parallel-header phrases) so word order works in RTL; for ComboBoxItem/MenuItem set Content/Header via loc:Translate while keeping Tag for code binding. Add Strings entries for the RestartAsAdmin dialogs and the structural fragments in RemoveNodeAsync (leave interpolated user data untranslated). For the admin pill, render two loc:Translate TextBlocks toggled by the existing DataTrigger rather than swapping x:Static (Binding is unsupported in Setter.Value inside ControlTemplate triggers).

**Files:**
- `src/Salvo.App/Views/Flow/GroupFlowView.xaml`
- `src/Salvo.App/ViewModels/MainWindowViewModel.cs`
- `src/Salvo.App/Views/MainWindow.xaml`
- `src/Salvo.App/Localization/Strings.resx`

**Rolled-up findings:**

<details>
<summary><b>[medium]</b> Flow editor is entirely hardcoded English while the rest of the app is localized (10 langs incl. RTL) — `src/Salvo.App/Views/Flow/GroupFlowView.xaml`</summary>


<sub>(detail cross-referenced in the full findings appendix)</sub>

</details>

<details>
<summary><b>[low]</b> Unlocalized English UI strings in an otherwise fully-localized VM — `src/Salvo.App/ViewModels/MainWindowViewModel.cs:484`</summary>


**Problem.** RestartAsAdmin passes literal English to the dialog service: "Restart as administrator" / "Could not resolve executable path." (484) and "Restart as administrator" / ex.Message (508), while every other dialog in this file uses Strings.* resources (e.g. Strings.Dialog_AutoStart_Title, Strings.Dialog_RemoveGroup_Title). RemoveNodeAsync (1355-1365) similarly builds English node labels inline ("If / Else", "Group call", $"Wait {..}s", "node") that are then shown in a localized confirm dialog. These strings never translate and are inconsistent with the codebase's localization discipline.


**Fix.** Add Strings.resx entries for the RestartAsAdmin dialog title and the "Could not resolve executable path." message, matching the pattern at lines 452/471. For RemoveNodeAsync, extract only the literal/structural fragments into resources — the fixed words ("If / Else", "Group call", the "Wait {n}s" format string, "Start "/"Stop "/"Run: " prefixes, and the "node" fallback) — while leaving the interpolated user data (App.Name, ServiceName, Command) untranslated; do not blanket-localize the whole switch.


<sub>effort S · risk low · verified: confirmed (high confidence)</sub>

</details>

<details>
<summary><b>[low]</b> Admin pill 'Active' text uses x:Static and won't update on live language switch — `src/Salvo.App/Views/MainWindow.xaml:169`</summary>


**Problem.** The admin pill's default text binds via {loc:Translate Admin_Pill_Recommended} (line 154), which refreshes when the user changes language at runtime (LocalizationManager raises Item[] change). But the DataTrigger that swaps to the 'Active' state sets the text with {x:Static res:Strings.Admin_Pill_Active} (line 169), which is resolved once at template load against the then-current culture and never updates. So after switching language with the app running, an elevated session shows the pill's 'Active' label in the previous language while its sibling text is correctly re-localized — an inconsistent, partially-stale UI.


**Fix.** Fix is real but keep it structural. Prefer rendering two TextBlocks in the pill template — one bound to {loc:Translate Admin_Pill_Recommended}, one to {loc:Translate Admin_Pill_Active} — and toggle their Visibility/Foreground off IsRunningAsAdmin via the existing DataTrigger; both then stay live-localized without VM plumbing. If instead exposing a VM string property, note it must also subscribe to LocalizationManager culture changes and raise OnPropertyChanged, otherwise it will be equally stale (the existing AdminStatusText at MainWindowViewModel.cs:199 is a sibling instance of the same latent staleness worth fixing together). Do NOT simply swap x:Static for loc:Translate: WPF does not support Binding in a Setter.Value inside ControlTemplate.Triggers, which is precisely why x:Static was used.


<sub>effort S · risk low · verified: confirmed (high confidence)</sub>

</details>


---

### 12. [P1] Expand test coverage on correctness-critical paths  `[ ]`

- **Priority:** P1 · **Effort:** L · **Change risk:** low · **Category:** testing

**Why:** Several substantial, security-sensitive or subtle code paths are entirely unpinned: the elevator payload-parse/dispatch/registry-delete mapping, the startup writer's approval-byte bit layout, readiness early-exit and probe-fault verdicts, graph action nodes (which a docstring falsely claims are locked), the IfElse else-branch and group cancellation, and the pure command-line parsers. These pin the exact behaviors the P0/P1 correctness fixes above depend on, so they should follow those fixes to lock them in.

**Approach:** Add ReadinessDetector tests for ExitedEarly (process-less session + timeout>grace) and the probe-throws->TimedOut branch (sub-grace timeout). Add graph tests for ServiceStart/ServiceStop (extend FakeServices with call tracking) and a direct RunCommandNode exit-code mapping; add an IfElse else-branch test and a pre-cancelled-token LaunchGroupAsync test (assert OCE + CallCount==0). Hoist IsApproved/BuildApprovedValue encode/decode into a pure ParseApprovedEnabled and round-trip test it; test RegistryRunValueWriter.Write's pre-registry validation gates via InternalsVisibleTo. Test elevator ParsePayload edge cases (make it internal) and pin the source->location mapping via FormatKeyPath. Promote ParseProcessStartArg/ParseDirect to internal and cover quoted/unbalanced/no-arg inputs. Add ServiceRunningProbe tests via a stub IServiceController.

**Files:**
- `tests/Salvo.Core.Tests/ReadinessDetectorTests.cs`
- `tests/Salvo.Core.Tests/GraphOrchestratorTests.cs`
- `src/Salvo.Core/WindowsStartup/RegistryRunValueWriter.cs`
- `src/Salvo.Core/WindowsStartup/WindowsStartupService.cs`
- `src/Salvo.Elevator/Program.cs`
- `src/Salvo.Core/Services/AppOrchestrator.cs`
- `src/Salvo.Core/Services/ProcessMatcherResolver.cs`
- `src/Salvo.Core/Launch/Probes/ServiceRunningProbe.cs`

**Rolled-up findings:**

<details>
<summary><b>[medium]</b> ReadinessDetector early-exit and probe-fault paths are untested — `tests/Salvo.Core.Tests/ReadinessDetectorTests.cs:7`</summary>


**Problem.** ReadinessDetectorTests covers fastest-probe-wins, timeout, no-applicable-probes, and cancel-losers, but never exercises two real verdicts. (1) The early-exit watcher WatchEarlyExitAsync -> LaunchOutcome.ExitedEarly (ReadinessDetector.cs lines 100-120, consumed at 60-65) is a distinct outcome that feeds telemetry/benchmarking and is completely uncovered. (2) The probe-throws branch in WrapProbeAsync (lines 93-96, which logs a warning and returns Unknown rather than propagating) is also uncovered. A regression that stopped detecting early process-tree death, or that let a probe exception escape, would pass the current suite.


**Fix.** Two tests. Early-exit: reuse MakeContext's process-less session (IsTreeAlive() is already false), supply a non-firing or slow probe, and call DetectAsync with a timeout > EarlyExitGrace (e.g. TimeSpan.FromSeconds(3)); assert Outcome == ExitedEarly and Signal == EarlyExit (test runs ~1s due to the grace period). Probe-throws: add a FakeProbe variant whose RunAsync throws a general exception, and use a sub-grace timeout (e.g. 250ms, matching the existing timeout test) so the early-exit watcher is cancelled inside its grace delay; assert Outcome == TimedOut and that DetectAsync completes without surfacing the exception. Note: do NOT use a long timeout for the throw test — the dead-tree watcher would then win and return ExitedEarly rather than TimedOut.


<sub>effort M · risk low · verified: confirmed (high confidence)</sub>

</details>

<details>
<summary><b>[medium]</b> IfElse else-branch, non-FileExists conditions, and group cancellation untested — `tests/Salvo.Core.Tests/GraphOrchestratorTests.cs:49`</summary>


<sub>(detail cross-referenced in the full findings appendix)</sub>

</details>

<details>
<summary><b>[medium]</b> Startup writer (RegistryRunValueWriter + approval bit logic) has zero test coverage — `src/Salvo.Core/WindowsStartup/RegistryRunValueWriter.cs:18`</summary>


<sub>(detail cross-referenced in the full findings appendix)</sub>

</details>

<details>
<summary><b>[medium]</b> No test coverage for payload parsing, action dispatch, or the privileged registry-delete mapping — `src/Salvo.Elevator/Program.cs:120`</summary>


**Problem.** A search of tests/ (Salvo.Core.Tests, Salvo.App.Tests) finds no references to the elevator, RegistryRunValueWriter, ElevationRequest round-tripping, or WindowsServiceController. This is privileged, security-sensitive code that mutates HKLM/HKCU Run keys and controls services, and the delete-path source->hive/approved-key mapping (Program.cs:100-107) is hand-maintained and duplicated (see the duplication finding), which is exactly the kind of table that regresses silently. ParsePayload (Program.cs:120-144) also has non-obvious edge behavior (only the first --payload honored; missing value, non-base64, and malformed JSON must map to a null request) that is untested.


**Fix.** Keep the proposed ParsePayload cases (valid, missing --payload, --payload with no value, non-base64, invalid JSON, JSON 'null', unknown Action) but note ParsePayload must be made internal with [InternalsVisibleTo] to be unit-testable. For the source->location mapping, a cheap first step needs no refactor: the existing public RegistryRunValueWriter.FormatKeyPath(source) already exercises ResolveLocation and can pin the hive/view wiring for all four StartupEntrySource values today; the approved-key path and the duplicated elevator delete mapping should be pinned after centralizing them (the separate duplication finding) so a single table drives both write and delete. Do not attempt real registry mutation in tests — restrict to the pure parse/dispatch/mapping logic.


<sub>effort M · risk low · verified: confirmed (high confidence)</sub>

</details>

<details>
<summary><b>[low]</b> Graph action-node execution (ServiceStart/Stop/RunCommand/Wait) claimed but never tested — `tests/Salvo.Core.Tests/GraphOrchestratorTests.cs:8`</summary>


<sub>(detail cross-referenced in the full findings appendix)</sub>

</details>

<details>
<summary><b>[low]</b> Pure parsing/matching logic (ParseProcessStartArg/ParseDirect) is untested — `src/Salvo.Core/Services/ProcessMatcherResolver.cs:144`</summary>


<sub>(detail cross-referenced in the full findings appendix)</sub>

</details>

<details>
<summary><b>[low]</b> Readiness probes have no unit tests, including the fully mockable ServiceRunningProbe — `src/Salvo.Core/Launch/Probes/ServiceRunningProbe.cs:25`</summary>


**Problem.** None of the four probes (ActivityQuietProbe, MainWindowProbe, WaitForInputIdleProbe, ServiceRunningProbe) have tests — the test suite covers ReadinessDetector, LaunchSession, the SQLite store, the PID resolver and the analyzer, but not the probes themselves. ServiceRunningProbe in particular takes its only dependency through the IServiceController abstraction (constructor line 15) and has clear branch logic — fires on Running, aborts on NotFound, polls otherwise (lines 30-40) — making it trivially unit-testable with a stub controller, yet it is untested. This leaves the Running/NotFound/timeout transitions unguarded against regressions.


**Fix.** Add unit tests for ServiceRunningProbe using a stub IServiceController covering the Running (fires), NotFound (aborts, returns false), and pending-then-running transitions; this is the lowest-cost probe to cover and pins the readiness contract.


<sub>effort M · risk low · verified: confirmed (high confidence)</sub>

</details>


---

### 13. [P2] Test-suite infrastructure & assertion hygiene  `[ ]`

- **Priority:** P2 · **Effort:** M · **Change risk:** low · **Category:** testing

**Why:** Test-only maintainability: the three orchestrator fakes + BuildOrchestrator are duplicated (and already drifting) across two files, temp-directory scaffolding is copy-pasted across 8+ classes (three of which must remember SqliteConnection.ClearAllPools or leak handles on Windows CI), and several tests assert almost nothing (conditional/over-broad assertions, misleading names). No production impact, but it removes ~100 lines and a real CI foot-gun.

**Approach:** Extract the three fakes + BuildOrchestrator into one OrchestratorTestFakes.cs adopting the superset (keep LastCall + StartResult/StopResult, unify the status-map name). Add a shared TempDirectory (and SqliteTempDirectory whose Dispose calls ClearAllPools before delete) under TestSupport and migrate all 8 classes. Replace DependencyHintsAnalyzerTests' conditional block with unconditional single-hint/empty-edges asserts (and rename). Narrow the PID-resolver BeOneOf to (ExitedEarly, TimedOut, Unknown). Rename LaunchGroupAsync_RunsEachApp_InOrder to reflect the single parallel wave and add a real ordering test with a WaitNode barrier.

**Files:**
- `tests/Salvo.Core.Tests/GraphOrchestratorTests.cs`
- `tests/Salvo.Core.Tests/AppOrchestratorTests.cs`
- `tests/Salvo.Core.Tests/DependencyHintsAnalyzerTests.cs`
- `tests/Salvo.Core.Tests/LaunchTelemetryServicePidResolverTests.cs`

**Rolled-up findings:**

<details>
<summary><b>[medium]</b> Temp-directory scaffolding copy-pasted across 7+ test classes (ClearAllPools foot-gun) — `tests/Salvo.Core.Tests/AppOrchestratorTests.cs:221`</summary>


<sub>(detail cross-referenced in the full findings appendix)</sub>

</details>

<details>
<summary><b>[low]</b> FakeServices/FakeInspector/FakeLauncher duplicated verbatim across two test files — `tests/Salvo.Core.Tests/GraphOrchestratorTests.cs:173`</summary>


<sub>(detail cross-referenced in the full findings appendix)</sub>

</details>

<details>
<summary><b>[low]</b> No-shared-resources test asserts nothing when the analyzer drops the group — `tests/Salvo.Core.Tests/DependencyHintsAnalyzerTests.cs:114`</summary>


**Problem.** Analyze_ProducesNoHint_WhenNoSharedResources wraps its assertions in `if (hints.Count > 0) { ... }` (lines 114-118). For the seeded input the analyzer deterministically returns exactly one hint with empty Edges (AnalyzeAsync adds a hint whenever latestOrder.Count > 0, regardless of edges — see DependencyHintsAnalyzer.cs lines 40-49), so the guard is not needed; worse, it means that if a future regression made the analyzer silently drop the group entirely (return 0 hints), this test would still pass green without asserting the intended 'no inferred edge' contract. The test cannot fail in the branch that matters.


**Fix.** Recommendation is correct as written. Replace the conditional with unconditional assertions matching the deterministic outcome: hints.Should().ContainSingle(); hints[0].Edges.Should().BeEmpty(); hints[0].IsReorderSuggested.Should().BeFalse(). Consider also renaming the test (e.g. Analyze_ProducesHintWithNoEdges_WhenNoSharedResources) since the analyzer does emit a hint, just without inferred edges.


<sub>effort S · risk low · verified: confirmed (high confidence)</sub>

</details>

<details>
<summary><b>[low]</b> Near-vacuous outcome assertion in BeginObservation no-inspector test — `tests/Salvo.Core.Tests/LaunchTelemetryServicePidResolverTests.cs:67`</summary>


<sub>(detail cross-referenced in the full findings appendix)</sub>

</details>

<details>
<summary><b>[low]</b> LaunchGroupAsync_RunsEachApp_InOrder tests no ordering and the graph runs them in parallel — `tests/Salvo.Core.Tests/AppOrchestratorTests.cs:99`</summary>


**Problem.** The test name promises ordering, but the group has two apps with default DelayAfterSeconds, which FlowMigration.Migrate turns into a single wave that fans both out from Start in parallel (confirmed by FlowMigrationTests.Migrate_TwoAppsInOneWave_FanOutFromStart). The body only asserts HaveCount(2), OnlyContain(Succeeded), and CallCount==2 (lines 121-123) — nothing about order, and order isn't even guaranteed for that topology. The name misleads readers into thinking sequential wave ordering is covered when it is not.


**Fix.** Recommendation stands. Preferably do both: (1) rename the existing test to reflect what it actually verifies (e.g. LaunchGroupAsync_RunsAllApps_InSingleParallelWave), and (2) add a distinct ordering test that sets DelayAfterSeconds>0 on the first app so Migrate inserts a WaitNode barrier, then capture launch order in FakeLauncher and assert the second wave's app is launched strictly after the first. Note the fix is test-only with no production impact, hence low severity rather than medium.


<sub>effort S · risk low · verified: confirmed (high confidence)</sub>

</details>


---

### 14. [P2] Consolidate duplicated helpers  `[ ]`

- **Priority:** P2 · **Effort:** L · **Change risk:** medium · **Category:** duplication

**Why:** A dozen small, copy-pasted blocks that each force N-place edits and have already drifted: the background-STA icon-load loop (3 copies), Windows command-line parsing (4 divergent copies), duration/median formatting (5 VMs, with an F1/F2 output inconsistency), self-relaunch-as-admin (2 diverged copies), chip focus handlers, argument tokenizer, Shell COM plumbing, matcher projection, source-label switch, sidebar nav templates, and native interop structs. Pure maintainability; do after dead-code + installer removal so fewer copies survive to merge.

**Approach:** Extract IconLoad.LoadAsync<T>(items, resolveSource, assign, threadName) taking the Dispatcher explicitly; route all 3 loaders through it. Extract CommandLine.SplitExecutable(command) (+ argv tokenizer) in Core unifying the unbalanced-quote policy and \??\ stripping, and route all 4 sites through it while KEEPING each call site's wrapper behavior (cmd.exe fallback, env-expand+File.Exists, null-on-ambiguity); add tests. Add DurationFormat.Human/Median in Core.Launch (pick one F1/F2) and collapse the 5 formatters + triplicated median. Extract ProcessElevation.RelaunchSelfAsAdmin(args) preserving each caller's distinct catch behavior. Extract the chip focus/commit logic into a WPF attached behavior; a static StripQuotes+SplitTokens helper for the tokenizers; a ShellAppsFolder helper (prefix/namespace consts + ReleaseCom); ProcessMatcher.ExeNames()/Aumids() extensions; an InstalledAppSourceLabels.For helper (in App); IO_COUNTERS+CloseHandle into a shared NativeMethods; and fold the sidebar nav active-state DataTrigger into the single SidebarNavButton style. Also collapse GroupGraphViewModel append + AppEntryEditor's duplicated known-app lookup while here.

**Files:**
- `src/Salvo.App/Services/AppIconLoader.cs`
- `src/Salvo.App/Services/WindowsStartupIconLoader.cs`
- `src/Salvo.App/ViewModels/BenchmarksViewModel.cs`
- `src/Salvo.App/ViewModels/AppEntryViewModel.cs`
- `src/Salvo.Core/Services/AppOrchestrator.cs`
- `src/Salvo.Core/Services/WindowsServicesProvider.cs`
- `src/Salvo.App/ViewModels/RegistryRunValueEditorViewModel.cs`
- `src/Salvo.App/ViewModels/AppEntryEditorViewModel.cs`
- `src/Salvo.App/App.xaml.cs`
- `src/Salvo.Core/Services/ProcessMatcherResolver.cs`
- `src/Salvo.Core/Native/ProcessIoInterop.cs`
- `src/Salvo.App/Views/MainWindow.xaml`

**Rolled-up findings:**

<details>
<summary><b>[medium]</b> 'Append node after current leaves' graph logic duplicated inline (add GroupGraphViewModel.AppendToEnd) — `src/Salvo.App/ViewModels/MainWindowViewModel.cs:945`</summary>


<sub>(detail cross-referenced in the full findings appendix)</sub>

</details>

<details>
<summary><b>[low]</b> Icon-loading STA background-thread pattern copy-pasted in three places — `src/Salvo.App/Services/AppIconLoader.cs:26`</summary>


**Problem.** The exact same block — spawn a named IsBackground STA Thread at BelowNormal priority, iterate a List<(vm, source)>, call AppIconCache.Get(source), and dispatcher.BeginInvoke(() => vm.Icon = icon, DispatcherPriority.Background) inside an empty try/catch — is duplicated verbatim in AppIconLoader.LoadFor (lines 26-51), WindowsStartupIconLoader.LoadFor (WindowsStartupIconLoader.cs lines 22-47), and BenchmarksViewModel.LoadPerAppIcons (BenchmarksViewModel.cs lines 253-278). Only the target-projection and the icon-assignment lambda differ. Three copies means any fix (e.g. bounding concurrent threads, adding cancellation, or serialising work) must be made in three files.


**Fix.** Recommendation stands as written. Minor refinement: have the helper accept the Dispatcher explicitly (as proposed) so BenchmarksViewModel can keep passing its injected _dispatcher while the two static loaders pass Dispatcher.CurrentDispatcher — preserving each call site's existing dispatcher-acquisition semantics. Downgrade to low: this is maintainability-only duplication with no current runtime defect; the risk it guards against (an un-synced concurrency fix) is latent.


<sub>effort M · risk low · verified: confirmed (high confidence)</sub>

</details>

<details>
<summary><b>[low]</b> Windows command-line parsing reimplemented 4 times with divergent edge-case handling — `src/Salvo.Core/Services/AppOrchestrator.cs:509`</summary>


<sub>(detail cross-referenced in the full findings appendix)</sub>

</details>

<details>
<summary><b>[low]</b> Duration formatting copy-pasted across 5 VMs with an F1/F2 output inconsistency — `src/Salvo.App/ViewModels/AppEntryViewModel.cs:56`</summary>


<sub>(detail cross-referenced in the full findings appendix)</sub>

</details>

<details>
<summary><b>[low]</b> Self-relaunch-as-administrator routine duplicated in App and MainWindowViewModel — `src/Salvo.App/ViewModels/MainWindowViewModel.cs:476`</summary>


**Problem.** The 'resolve Environment.ProcessPath, start a new ProcessStartInfo with UseShellExecute=true + Verb="runas", then Shutdown, catching the UAC-cancel Win32Exception' sequence exists twice: App.TryRelaunchAsAdminIfConfigured (src/Salvo.App/App.xaml.cs:352-401) and MainWindowViewModel.RestartAsAdmin (src/Salvo.App/ViewModels/MainWindowViewModel.cs:476-509). The two copies have already diverged: the App copy forwards the original args plus a '--no-elevate-relaunch' guard flag, while RestartAsAdmin forwards no args at all and never passes the guard flag, so the elevation entry points are inconsistent and any change to how the app re-launches itself (args, working dir, guard flag) must be mirrored by hand.


**Fix.** Extract only the shared core — build the runas ProcessStartInfo (UseShellExecute=true, Verb=\"runas\", WorkingDirectory), Process.Start, Shutdown, and catch the UAC-cancel Win32Exception (code 1223) — into a helper such as ProcessElevation.RelaunchSelfAsAdmin(string arguments), taking the forwarded-args string as a parameter. App keeps passing args + SkipElevateFlag; RestartAsAdmin passes string.Empty. Leave the gating logic (SkipElevateFlag arg check, ElevationDetector.IsElevated, AlwaysRunAsAdmin settings lookup) in App, since it has no VM analog. Also reconcile the intentionally different catch behavior (App swallows all Win32Exception and returns false to continue non-elevated; VM narrows to 1223 and surfaces other errors via dialog) — the helper should either expose an error callback or the callers should keep their own outer catch so this behavioral difference is preserved deliberately rather than by accident.


<sub>effort S · risk medium · verified: confirmed (high confidence)</sub>

</details>

<details>
<summary><b>[low]</b> Argument-chip focus handlers duplicated verbatim across two editor windows — `src/Salvo.App/Views/AppEntryEditorWindow.xaml.cs:33`</summary>


**Problem.** ChipEditBox_IsVisibleChanged, ChipEditBox_Loaded, FocusChipEditor, and ChipEditBox_LostFocus (AppEntryEditorWindow.xaml.cs:33-70) are byte-for-byte identical to the same four methods in RegistryRunValueEditorWindow.xaml.cs:37-74, and both XAMLs wire the same handler names. This ~30-line block is copy-pasted; a change to chip focus/commit behavior must be made in two places and can drift.


**Fix.** Extract the chip-editing focus/commit logic into a WPF attached behavior (a static class exposing attached properties that internally subscribe to Loaded, IsVisibleChanged, and LostFocus on the TextBox), reference it from both editor XAMLs, and delete all four duplicated code-behind handlers plus their XAML handler wiring. Prefer this over the "shared static helper" alternative: a static helper would only deduplicate FocusChipEditor while leaving the three event handlers and their per-XAML wiring duplicated in both windows.


<sub>effort M · risk low · verified: confirmed (high confidence)</sub>

</details>

<details>
<summary><b>[low]</b> Argument tokenizer and chip logic duplicated in RegistryRunValueEditorViewModel — `src/Salvo.App/ViewModels/AppEntryEditorViewModel.cs:391`</summary>


**Problem.** AppEntryEditorViewModel and RegistryRunValueEditorViewModel independently reimplement the same command-line handling: StripQuotes is identical (AppEntryEditor 391 vs RegistryEditor 406), the quote-aware token splitter is duplicated (AppEntryEditor.SplitTokens 397 vs RegistryEditor.TryParseCommand 415), the "combine `--flag value` into one chip" heuristic is duplicated (RefreshArgumentChips 161 vs ReplaceChips 379), and both wire the ArgumentChips CollectionChanged subscription + OnChipPropertyChanged empty-chip removal the same way. ArgumentChipViewModel is already shared; the parsing/heuristics are not.


**Fix.** Extract the truly-shared, deterministic pieces into a small static helper in Salvo.App.Services: (1) StripQuotes and (2) the quote-aware token splitter (SplitTokens), which the Registry editor's TryParseCommand can call for the argument-remainder portion. Optionally factor the identical ArgumentChips.CollectionChanged subscribe/unsubscribe + empty-chip-removal boilerplate into a shared chip-collection helper or base class. Do NOT extract the 'combine --flag value into one chip' heuristic as a single shared function: the two editors intentionally differ — AppEntryEditor gates combining on per-app known-argument data (needsValueFlags), while RegistryRunValueEditor deliberately uses a generic fallback because it has no per-app knowledge (documented in its comment at lines 374-378). If shared at all, that heuristic must be parameterized by a 'should-combine' predicate rather than copied.


<sub>effort M · risk low · verified: needs-nuance (high confidence)</sub>

</details>

<details>
<summary><b>[low]</b> Shell.Application COM plumbing and shell:AppsFolder prefix duplicated across three files — `src/Salvo.Core/Services/ProcessMatcherResolver.cs:167`</summary>


<sub>(detail cross-referenced in the full findings appendix)</sub>

</details>

<details>
<summary><b>[low]</b> Matcher-to-exeNames/aumids projection duplicated between ProcessInspector and KnownAppsDatabase — `src/Salvo.Core/Services/KnownAppsDatabase.cs:29`</summary>


**Problem.** The exact LINQ that projects an IReadOnlyList<ProcessMatcher> into a case-insensitive HashSet of exe names and a HashSet of AUMIDs appears twice: KnownAppsDatabase.FindMatch (lines 29-37) and ProcessInspector.CollectExeNames/CollectAumids (lines 122-132). Same Where/Select/ToHashSet(OrdinalIgnoreCase) in both.


**Fix.** Add extension methods (e.g. ProcessMatcherExtensions.ExeNames()/Aumids() on IReadOnlyList<ProcessMatcher>) in Salvo.Core.Models and call them from both places.


<sub>effort S · risk low · verified: confirmed (high confidence)</sub>

</details>

<details>
<summary><b>[low]</b> InstalledAppSource-to-label switch duplicated in two VMs — `src/Salvo.App/ViewModels/AddAppPickerViewModel.cs:132`</summary>


**Problem.** AddAppPickerViewModel.SourceLabel (132-139) and InstalledAppViewModel.SourceBadge (24-31) are identical switch expressions mapping InstalledAppSource to the same Strings.AddAppPicker_Source* resources. Adding a new source requires editing both, and they can silently diverge.


**Fix.** Extract the mapping into a single static helper and call it from both VMs. Place it in the Salvo.App project (e.g. static string InstalledAppSourceLabels.For(InstalledAppSource)), not in Salvo.Core where the enum is defined, so the dependency on App-layer Strings resources stays out of Core.


<sub>effort S · risk low · verified: confirmed (high confidence)</sub>

</details>

<details>
<summary><b>[low]</b> Three sidebar nav buttons repeat near-identical control templates — `src/Salvo.App/Views/MainWindow.xaml:260`</summary>


**Problem.** A reusable SidebarNavButton style is defined (lines 63-83), but WindowsStartupButton overrides its Template inline anyway (226-240) to add an active-state DataTrigger, and BenchmarksButton (267-288) and SettingsButton (315-336) each declare their own full inline Button.Style with a copy of the same Border/ContentPresenter template. The three templates are identical except for the active-view DataTrigger binding (IsStartupView / IsBenchmarksView / IsSettingsView). This is ~80 lines of duplicated markup that must be edited in three places to change nav-item chrome.


**Fix.** Fold the active-state DataTrigger into the single SidebarNavButton style: add <DataTrigger Binding="{Binding Tag, RelativeSource={RelativeSource Self}}" Value="True"><Setter TargetName="Bd" Property="Background" Value="{DynamicResource SubtleFillColorTertiaryBrush}"/></DataTrigger> to its ControlTemplate.Triggers, then on each button apply Style="{StaticResource SidebarNavButton}" and set Tag="{Binding IsStartupView}" / Tag="{Binding IsBenchmarksView}" / Tag="{Binding IsSettingsView}" respectively. Delete the WindowsStartupButton inline Template override and the two inline Button.Style blocks. Do a quick visual check that the active-indicator highlight still tracks the selected view.


<sub>effort M · risk medium · verified: confirmed (high confidence)</sub>

</details>

<details>
<summary><b>[low]</b> IO_COUNTERS struct and CloseHandle P/Invoke duplicated across the Native interop files — `src/Salvo.Core/Native/ProcessIoInterop.cs:12`</summary>


**Problem.** The IO_COUNTERS struct is declared identically (same 6 ulong fields) in ProcessIoInterop.cs lines 12-20 and JobObjectInterop.cs lines 17-25. Separately, the kernel32 CloseHandle P/Invoke is declared three times: ProcessIoInterop.cs line 27, JobObjectInterop.cs line 85, and Toolhelp32Interop.cs line 41. This is the kind of interop duplication that invites subtle marshalling drift (e.g. a CharSet/SetLastError tweak applied to one copy but not another).


**Fix.** Consolidate the shared native declarations into a single internal Kernel32/NativeMethods class in Salvo.Core.Native and reference it from the specialized interop files. Lead with IO_COUNTERS (the real substance — a 6-field struct duplicated identically and embedded in JOBOBJECT_EXTENDED_LIMIT_INFORMATION, so drift there would break marshalling). Folding CloseHandle in is a reasonable secondary cleanup, but drop the "CharSet/SetLastError drift" justification for it — CloseHandle has no CharSet and a trivially stable signature, so that copy carries no real drift risk.


<sub>effort S · risk low · verified: confirmed (high confidence)</sub>

</details>

<details>
<summary><b>[low]</b> Identical AppEntry-from-fields construction plus known-app lookup duplicated — `src/Salvo.App/ViewModels/AppEntryEditorViewModel.cs:196`</summary>


<sub>(detail cross-referenced in the full findings appendix)</sub>

</details>

<details>
<summary><b>[low]</b> Hardcoded, theme-unaware hex colors for service Start/Stop icons — `src/Salvo.App/Views/Flow/GroupFlowView.xaml:398`</summary>


**Problem.** The ServiceStart icon uses Foreground="#16A34A" (line 398) and ServiceStop uses Foreground="#DC2626" (line 424). These are literal colors that do not adapt to light/dark theme, and they directly violate the codebase's own documented convention in SemanticBrushes.xaml: 'App code should bind to these keys, NOT directly to LimeGreen / Gray etc.' The repo already defines StatusRunningBrush (#FF37C05A) and StatusStoppedBrush (#FF8A8A8A) for exactly this running/stopped semantics; these two hardcoded hexes are the only literal colors in the entire views/styles tree (per a repo-wide hex scan) and are close-but-inconsistent duplicates of the semantic ones.


**Fix.** Replace both literals with theme-aware brushes that PRESERVE the green(start)/red(stop) action semantics: bind Foreground on the Play24 icon to {DynamicResource SystemFillColorSuccessBrush} and on the Stop24 icon to {DynamicResource SystemFillColorCriticalBrush} (WPF-UI Fluent fill brushes; note the actual key suffix is ...Brush). Do NOT use StatusStoppedBrush for the Stop icon — it is gray (#FF8A8A8A) and would drop the red destructive affordance. StatusRunningBrush is acceptable for the Start icon (both green) but for consistency prefer the Success/Critical pairing, or add two dedicated semantic keys (e.g. ActionStartBrush/ActionStopBrush) to SemanticBrushes.xaml if a specific hue must be pinned.


<sub>effort S · risk low · verified: needs-nuance (high confidence)</sub>

</details>


---

### 15. [P1] Decompose MainWindowViewModel — extract FlowEditorViewModel  `[ ]`

- **Priority:** P1 · **Effort:** L · **Change risk:** medium · **Category:** architecture

**Why:** At ~1465 lines MainWindowViewModel owns a dozen unrelated responsibilities; the self-contained ~430-line flow-graph editing cluster is the clean primary decomposition target (no dependency on settings/update/navigation). This is the highest-value maintainability refactor but carries medium risk (view bindings + command CanExecute/PropertyChanged retargeting), so it lands after dead-code removal and the duplication passes have already trimmed the file.

**Approach:** Extract a FlowEditorViewModel owning all node/branch authoring commands, constructed with IServiceProvider + two host callbacks (PersistConfig and RefreshRunningStates). Expose it as MainVM.FlowEditor, retargeted (with SelectedGroup) on SelectedGroup change, re-raising command CanExecute/PropertyChanged on retarget; repoint the views to MainVM.FlowEditor.X. While in here, make CreateNode's unrecognized-kind default throw and centralize the node-kind vocabulary as const strings on NodeViewModel (referenced by the switch and XAML Tags via x:Static). Optionally lift the back/forward navigation block into a small NavigationHistory helper as a lower-risk follow-up. Do NOT pursue the AppOrchestrator God-class split (already covered by tests; not worth the seams).

**Files:**
- `src/Salvo.App/ViewModels/MainWindowViewModel.cs`
- `src/Salvo.App/ViewModels/GroupGraphViewModel.cs`
- `src/Salvo.App/Views/Flow/GroupFlowView.xaml`
- `src/Salvo.App/ViewModels/NodeViewModel.cs`

**Rolled-up findings:**

<details>
<summary><b>[medium]</b> MainWindowViewModel is a God class; extract the flow-graph editor surface — `src/Salvo.App/ViewModels/MainWindowViewModel.cs:36`</summary>


**Problem.** At ~1465 lines this VM owns at least a dozen unrelated responsibilities: navigation back/forward history (272-353), settings persistence + theme/language (394-528), update checking (251-618), elevation/admin restart (197-510), group CRUD (802-880), config persistence + running-state polling (663-753), benchmark history loading (686-724), AND the entire flow-graph editing surface. The flow-graph cluster alone is a self-contained ~430 lines that operates only on SelectedGroup.Graph plus the service provider: CreateNode (1075), AddNode (1087), BuildAppNodeViaEditor (1144), PickAppNodes (1169), ToAppNode (1191), InsertNodeAfterStage (1214), AddNodeToBranch (1257), MoveNodeIntoBranch (1290), ExtractBranchNodeToOuter (1319), RemoveFromAnyBranch (1327), MakeStageSequential (1341), RemoveNodeAsync (1350), AppendAppNode (945). This is the primary decomposition target — it has no dependency on settings/update/navigation state and would move cleanly.


**Fix.** Extract a FlowEditorViewModel that owns all node/branch authoring commands (CreateNode/AddNode/PickAppNodes/InsertNodeAfterStage/AddNodeToBranch/MoveNodeIntoBranch/ExtractBranchNodeToOuter/MakeStageSequential/RemoveNodeAsync/AppendAppNode). Construct it with IServiceProvider and give it TWO host callbacks — a persist callback (PersistConfig) and a refresh callback (RefreshRunningStates) — since the mutators trigger both, not just persistence; AppIconLoader is static so needs no injection. MainWindowViewModel exposes a FlowEditor property retargeted (with its SelectedGroup) whenever SelectedGroup changes; note the public flow methods are bound from views, so views must be repointed to MainVM.FlowEditor.X and RelayCommand CanExecute / PropertyChanged must be re-raised on retarget — this is the source of the 'medium' risk. Treat this as a medium-priority maintainability refactor, not a high-severity issue. Optionally also lift the back/forward navigation block (272-353) into a small NavigationHistory helper as an independent, lower-risk follow-up.


<sub>effort L · risk medium · verified: needs-nuance (high confidence)</sub>

</details>

<details>
<summary><b>[low]</b> Node-kind discriminators are stringly-typed and duplicated across four call sites — `src/Salvo.App/ViewModels/MainWindowViewModel.cs:1075`</summary>


**Problem.** Node creation dispatches on raw string literals: CreateNode's switch (1075) plus `kind == "App"` special-cases in AddNode (1094), InsertNodeAfterStage (1220), and AddNodeToBranch (1264), and MoveNodeIntoBranch/branch selection compares `"else"` (1261, 1308). These literals must stay in lockstep with NodeViewModel.Kind ("App"/"Wait"/"IfElse"/... in NodeViewModel.cs) with no compile-time guarantee, so a typo like "Ifelse" fails silently by falling into the default null branch.


**Fix.** Keep this as a low-priority cleanup and reframe it. The highest-value, lowest-risk fix is to make an unrecognized kind fail loudly instead of silently: change CreateNode's `_ => null` default to throw (or assert), and centralize the kind vocabulary as shared const strings on NodeViewModel (referenced by the switch and, where practical, the XAML Tags via x:Static) so the C# side has a single source of truth. Drop the framing that an enum yields compile-time dispatch safety — the authoritative literals are XAML Tag strings, so any type-checking at that boundary is inherently runtime; a full NodeKind enum is optional polish, not the win the finding implies, and the medium effort/risk isn't justified for the marginal benefit.


<sub>effort M · risk medium · verified: needs-nuance (high confidence)</sub>

</details>

<details>
<summary><b>[low]</b> AppOrchestrator mixes four responsibilities (extract only ParseDirect for testability) — `src/Salvo.Core/Services/AppOrchestrator.cs:11`</summary>


<sub>(detail cross-referenced in the full findings appendix)</sub>

</details>


---

### 16. [P2] Elevator diagnostics & robustness  `[ ]`

- **Priority:** P2 · **Effort:** M · **Change risk:** low · **Category:** error-handling

**Why:** The elevated helper runs hidden with zero diagnostics — every failure is swallowed and invisible, and the parent's only log message miscategorizes all elevation failures as 'user cancelled'. Its per-service 25s timeout also means worst-case elevated runtime scales unbounded with service count. Supportability and robustness for privileged code; low-risk incremental improvements.

**Approach:** In ElevationClient.WaitAsync log the non-zero ExitCode; in InvokeAsync inspect the caught exception and log Win32Exception 1223 as an informational 'user cancelled', everything else as Error 'helper failed to start'. In Program.cs capture the per-service message (replace out _) and append failures to a %LOCALAPPDATA% text log (prefer a file over registering an EventLog source). Surface the discarded InvokeAsync bool at the MainWindowViewModel call site to inform the user. Bound the whole elevated request with one overall CancellationToken deadline plumbed from the caller rather than a fresh 25s per service. Document the 1/2/3 exit codes (a local-to-Elevator ExitCode enum is optional and only worth it if the client branches on the codes).

**Files:**
- `src/Salvo.Elevator/Program.cs`
- `src/Salvo.App/Elevation/ElevationClient.cs`
- `src/Salvo.App/ViewModels/MainWindowViewModel.cs`

**Rolled-up findings:**

<details>
<summary><b>[low]</b> Elevated helper has zero diagnostics; every failure is swallowed and invisible — `src/Salvo.Elevator/Program.cs:34`</summary>


**Problem.** The elevator runs elevated in a hidden, no-window process (launched with WindowStyle.Hidden + CreateNoWindow, ElevationClient.cs:66-68) yet emits no log, event-log entry, or stderr on any failure. Main catches all exceptions and returns 3 with no detail (Program.cs:34-37); RunRegistryDelete swallows all exceptions returning 1 (88-91); RunServiceAction discards the per-service failure message via `out _` (53-54) even though WindowsServiceController produces useful text like 'Needs admin' / 'Start timed out' (WindowsServiceController.cs:47,57). The parent doesn't recover any of this either: WaitAsync only returns `process.ExitCode == 0` (ElevationClient.cs:93) without logging the code, and the group call site ignores even that boolean (MainWindowViewModel.cs:1451-1455). Net result: when an elevated service start or registry write fails, there is no artifact anywhere explaining why. For privileged, security-sensitive code this is a real supportability gap.


**Fix.** Prioritize the two lowest-risk, highest-value changes: (1) in ElevationClient.WaitAsync, log the non-zero ExitCode via the existing _logger so failures are traceable from the parent without touching the elevated process; (2) in Program.cs, capture the per-service message (replace `out _`) and in the Main catch, and append failures to a plain text log file under %LOCALAPPDATA% (e.g. via File.AppendAllText). Prefer a log file over EventLog — registering an EventLog source is itself a privileged registry op that adds failure surface. Optionally surface the returned bool at the MainWindowViewModel call site to inform the user the elevated action failed.


<sub>effort M · risk low · verified: confirmed (high confidence)</sub>

</details>

<details>
<summary><b>[low]</b> All elevation start failures are logged as 'User cancelled elevation' — `src/Salvo.App/Elevation/ElevationClient.cs:81`</summary>


<sub>(detail cross-referenced in the full findings appendix)</sub>

</details>

<details>
<summary><b>[low]</b> Service timeout applied per-service; worst-case elevated runtime scales with service count — `src/Salvo.Elevator/Program.cs:50`</summary>


<sub>(detail cross-referenced in the full findings appendix)</sub>

</details>

<details>
<summary><b>[low]</b> Differentiated exit codes (1/2/3) are undocumented magic literals no consumer distinguishes — `src/Salvo.Elevator/Program.cs:26`</summary>


<sub>(detail cross-referenced in the full findings appendix)</sub>

</details>


---

### 17. [P2] Build & config hygiene  `[ ]`

- **Priority:** P2 · **Effort:** M · **Change risk:** low · **Category:** build-config

**Why:** Small build-graph tidiness: TargetFramework strings are copy-pasted across projects with an inconsistent Windows OS-version pin (Core implicitly platform 7.0 vs the app's 19041, a latent CA1416-as-error trap), and the Copyright embeds build-time UtcNow.Year, undermining the Deterministic=true build. The .editorconfig gap is a preference decision, not a bug — enforcement of compiler/CA warnings is already real via TreatWarningsAsErrors.

**Approach:** Move a shared default TargetFramework (net10.0-windows) into Directory.Build.props for Core/Elevator/Core.Tests (WPF projects keep 19041) and set one explicit Windows platform version + a single SupportedOSPlatformVersion floor matching the Win10 1809/MSIX minimum instead of letting Core default to 7.0. Replace the clock-derived Copyright year with a fixed year/range, and add ContinuousIntegrationBuild=true conditioned on GITHUB_ACTIONS in Directory.Build.props. Treat .editorconfig as optional: only add one if the team wants cosmetic IDExxxx enforcement, adopting promoted rules incrementally so TreatWarningsAsErrors doesn't break the build.

**Files:**
- `Directory.Build.props`
- `src/Salvo.Core/Salvo.Core.csproj`
- `src/Salvo.Elevator/Salvo.Elevator.csproj`
- `tests/Salvo.Core.Tests/Salvo.Core.Tests.csproj`

**Rolled-up findings:**

<details>
<summary><b>[low]</b> TargetFramework duplicated across 6 projects with two inconsistent Windows OS-version pins — `src/Salvo.Core/Salvo.Core.csproj:3`</summary>


<sub>(detail cross-referenced in the full findings appendix)</sub>

</details>

<details>
<summary><b>[low]</b> Copyright embeds build-time UtcNow.Year, undermining the Deterministic=true build — `Directory.Build.props:12`</summary>


**Problem.** Directory.Build.props sets <Deterministic>true</Deterministic> (line 9) — the intent being byte-reproducible output for identical source — but Copyright is defined as 'Copyright (c) $([System.DateTime]::UtcNow.Year)' (line 12). That expression is evaluated at build time, so the AssemblyCopyrightAttribute (and thus the produced binary) changes based on the wall-clock year: the same commit built in December 2026 vs January 2027 yields different assemblies. This is a (small, once-a-year) violation of the reproducibility the Deterministic flag advertises. Relatedly, the CI/Release workflows do not pass ContinuousIntegrationBuild=true, which is the companion flag that normalizes embedded source paths for reproducible/CI builds.


**Fix.** Replace the clock-derived year with an input-derived copyright string — a fixed start year or range, e.g. 'Copyright (c) 2025 Salvo' — so output is source-determined. Add ContinuousIntegrationBuild=true for CI builds; prefer a conditional in Directory.Build.props (<ContinuousIntegrationBuild Condition="'$(GITHUB_ACTIONS)'=='true'">true</ContinuousIntegrationBuild>) so both ci.yml and release.yml get it without editing each dotnet invocation. This is the higher-value half, as it normalizes embedded PDB source paths.


<sub>effort S · risk low · verified: confirmed (high confidence)</sub>

</details>

<details>
<summary><b>[low]</b> EnforceCodeStyleInBuild is on but there is no .editorconfig, so IDExxxx style rules are a no-op — `Directory.Build.props:8`</summary>


<sub>(detail cross-referenced in the full findings appendix)</sub>

</details>


---

### 18. [P2] Update-service HTTP/DTO consolidation (deferred with Velopack)  `[ ]`

- **Priority:** P2 · **Effort:** M · **Change risk:** low · **Category:** duplication

**Why:** The GitHub release HTTP client factory, repo-path/URL builder, Accept/User-Agent literals, timeouts, and release DTOs are duplicated across MsixUpdateService, VelopackUpdateService, and CachelessGithubSource, and a purpose-built Timeouts.UpdateCheckerHttp is dead. But the duplicated half lives in the Velopack path scheduled for deletion in MSIX Phase 3 — investing in a shared abstraction spanning dying code is largely wasted, so this is mostly a deferral.

**Approach:** Prefer to defer until VelopackUpdateService is deleted, at which point most duplication disappears on its own. If touched before then, do the minimum: extract only a tiny shared HttpClient factory + repos/{repoPath}/releases URL builder consumed by the two Salvo-owned HttpClient paths, and replace the literal 'Salvo' UA with AppBranding.AppName. Delete or adopt the dead Timeouts.UpdateCheckerHttp; if centralizing timeouts, keep the 15s release-body and 30s latest-lookup as two distinct named constants (do not merge). Do NOT fold in CachelessGithubSource (Velopack IFileDownloader, different UA/timeout) or unify DTOs with Velopack's library type.

**Files:**
- `src/Salvo.App/Services/MsixUpdateService.cs`
- `src/Salvo.App/Services/UpdateService.cs`
- `src/Salvo.App/Services/CachelessGithubSource.cs`
- `src/Salvo.Core/Services/Timeouts.cs`

**Rolled-up findings:**

<details>
<summary><b>[low]</b> GitHub API host, Accept header, and User-Agent duplicated across the three update-service files — `src/Salvo.App/Services/MsixUpdateService.cs:175`</summary>


**Problem.** The GitHub REST access details are copy-pasted across MsixUpdateService, VelopackUpdateService (UpdateService.cs) and CachelessGithubSource. The base URL "https://api.github.com/repos/{repoPath}/releases/..." appears three times (MsixUpdateService.cs:175, UpdateService.cs:233, CachelessGithubSource.cs:70); the Accept media type "application/vnd.github.v3+json" appears three times (MsixUpdateService.cs:200, UpdateService.cs:98, CachelessGithubSource.cs:74); and the User-Agent product literal "Salvo" is hardcoded twice (MsixUpdateService.cs:199, UpdateService.cs:97) instead of using the existing AppBranding.AppName (which already resolves to "Salvo"). This is exactly the duplicated-value case the audit prioritizes, and the hardcoded "Salvo" UA bypasses an existing branding constant.


**Fix.** Add a small GitHubApi constants class in Salvo.Core exposing the host (\"https://api.github.com\"), the vnd.github.v3+json Accept value, and a helper to build repos/{repoPath}/releases endpoints, and reference it from all three sites. Replace the literal \"Salvo\" UA with AppBranding.AppName in the two HttpClient factories (MsixUpdateService.CreateReleaseClient and VelopackUpdateService.CreateReleaseBodyClient). Do NOT try to unify the HttpClient creation with CachelessGithubSource — it is a Velopack GithubSource subclass using Downloader.DownloadString with a header dictionary and an intentionally different \"Velopack\" User-Agent; only share the host/media-type string constants there.


<sub>effort M · risk low · verified: confirmed (high confidence)</sub>

</details>

<details>
<summary><b>[low]</b> GitHub-release HTTP client and fetch logic duplicated between the two IUpdateService implementations — `src/Salvo.App/Services/MsixUpdateService.cs:187`</summary>


**Problem.** MsixUpdateService and VelopackUpdateService each carry near-identical infrastructure: CreateReleaseClient (src/Salvo.App/Services/MsixUpdateService.cs:187) and CreateReleaseBodyClient (src/Salvo.App/Services/UpdateService.cs:85) build the exact same proxy-bypassing HttpClientHandler (UseProxy/UseDefaultCredentials/UseCookies=false, Credentials=null) with the same 'Salvo' user-agent, github.v3 Accept header, and 15s timeout (a third copy of the handler config lives in CleanHttpClientFileDownloader at UpdateService.cs:24). Both services also duplicate the GitHub repo-path derivation (`new Uri(AppBranding.SupportUrl); AbsolutePath.Trim('/'); https://api.github.com/repos/{repoPath}/releases/...`) in FetchLatestReleaseAsync (MsixUpdateService.cs:171) vs TryFetchGithubReleaseBodyAsync (UpdateService.cs:227), and each declares its own private GitHub-release DTO with a `body` property. This will drift as one path is maintained and the other is not.


**Fix.** Given VelopackUpdateService is scheduled for retirement (Phase 3 of the MSIX migration, per MsixUpdateService's own docstring), the lowest-risk option is to leave the duplication until that class is deleted, after which it disappears on its own. If a fix is wanted before then, keep it minimal: extract only a tiny static helper for the two shared pieces that actually match — a plain-HttpClient factory (the proxy-bypass handler + UA/Accept/timeout) and a repo-path builder off AppBranding.SupportUrl — and have both services call it. Do NOT try to unify CleanHttpClientFileDownloader's handler into it (it needs redirect/decompression config and subclasses Velopack's downloader), and do NOT introduce a single shared release DTO/GitHubReleaseClient abstraction — the two DTOs differ in shape (superset vs body-only) and building a shared client abstraction adds coupling that will be unwound at Velopack retirement.


<sub>effort M · risk medium · verified: needs-nuance (high confidence)</sub>

</details>

<details>
<summary><b>[low]</b> Update HTTP timeouts hardcoded (15s x2, 30s x1) while Timeouts.UpdateCheckerHttp is dead — `src/Salvo.App/Services/UpdateService.cs:99`</summary>


<sub>(detail cross-referenced in the full findings appendix)</sub>

</details>


---

## Appendix — all confirmed findings (full detail)

Grouped by audit area. This is the complete verified record; the workstreams above are a rollup of these.

### App/Services+Converters+Controls  (6)

<details>
<summary><b>[medium]</b> <code>correctness</code> · NormaliseTo3Part does not normalise part count, causing false "update available" for 4-part tags — `src/Salvo.App/Services/MsixUpdateService.cs:163`</summary>


**Problem.** IsNewer (line 153) compares versions via System.Version.CompareTo after calling NormaliseTo3Part on both sides. Despite its name, NormaliseTo3Part (line 163) only strips a prerelease dash suffix — it does NOT reduce the version to 3 components. Its own comment says tags may be "0.2.14" or "0.2.14.0". CurrentVersion (line 63) always yields a 3-part string like "0.2.14" (Version parses that with Revision = -1/undefined). If a release tag carries a 4th component ("v0.2.14.0"), latest parses to Version(0,2,14,0) whose Revision=0. Version.CompareTo treats an undefined component (-1) as less than a defined one, so Version(0,2,14,0) > Version(0,2,14). IsNewer therefore returns true even when the installed build is the same version, surfacing a perpetual "update available" the user can never clear. MSIX/AppInstaller versions are natively 4-part, so 4-part tags are a realistic input the code explicitly anticipates.


**Fix.** Recommendation is sound. Simplest correct fix: since CurrentVersion intentionally drops the Revision component (Z=0 by Store convention), compare only Major.Minor.Build — e.g. build both operands as `new Version(v.Major, v.Minor, v.Build)` after parsing, or truncate/pad both strings to the same fixed component count before Version.TryParse. Rename NormaliseTo3Part to reflect what it does (strip prerelease suffix) and add the actual component-count normalisation.


<sub>effort S · risk low · confirmed (high)</sub>

</details>

<details>
<summary><b>[medium]</b> <code>error-handling</code> · Non-atomic settings write can silently reset all user settings — `src/Salvo.App/Services/SettingsStore.cs:31`</summary>


**Problem.** Save writes settings.json with a direct File.WriteAllText (line 31). If the process is killed or power is lost mid-write, the file is left truncated/corrupt. Load (lines 42-50) wraps deserialization in catch { return new AppSettings(); }, so a corrupt file silently discards ALL persisted settings and reverts to defaults with no warning. The sibling store in the same solution, Core JsonConfigStore, already does this correctly with an atomic temp-file swap (JsonConfigStore.cs lines 113-115: write to ConfigPath + ".tmp", then File.Move(tempPath, ConfigPath, overwrite: true)) — so this is both a data-loss risk and an inconsistency with the established pattern.


**Fix.** Mirror JsonConfigStore: serialize to a "settings.json.tmp" and File.Move(..., overwrite: true) to atomically replace, so a crash never leaves a half-written primary file.


<sub>effort S · risk low · confirmed (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>duplication</code> · Icon-loading STA background-thread pattern copy-pasted in three places — `src/Salvo.App/Services/AppIconLoader.cs:26`</summary>


**Problem.** The exact same block — spawn a named IsBackground STA Thread at BelowNormal priority, iterate a List<(vm, source)>, call AppIconCache.Get(source), and dispatcher.BeginInvoke(() => vm.Icon = icon, DispatcherPriority.Background) inside an empty try/catch — is duplicated verbatim in AppIconLoader.LoadFor (lines 26-51), WindowsStartupIconLoader.LoadFor (WindowsStartupIconLoader.cs lines 22-47), and BenchmarksViewModel.LoadPerAppIcons (BenchmarksViewModel.cs lines 253-278). Only the target-projection and the icon-assignment lambda differ. Three copies means any fix (e.g. bounding concurrent threads, adding cancellation, or serialising work) must be made in three files.


**Fix.** Recommendation stands as written. Minor refinement: have the helper accept the Dispatcher explicitly (as proposed) so BenchmarksViewModel can keep passing its injected _dispatcher while the two static loaders pass Dispatcher.CurrentDispatcher — preserving each call site's existing dispatcher-acquisition semantics. Downgrade to low: this is maintainability-only duplication with no current runtime defect; the risk it guards against (an un-synced concurrency fix) is latent.


<sub>effort M · risk low · confirmed (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>duplication</code> · GitHub release-API HTTP plumbing duplicated across the two update services — `src/Salvo.App/Services/MsixUpdateService.cs:187`</summary>


**Problem.** MsixUpdateService and VelopackUpdateService each independently reimplement the same GitHub REST plumbing: CreateReleaseClient (MsixUpdateService.cs 187-203) is byte-for-byte identical to CreateReleaseBodyClient (UpdateService.cs 85-101) — same proxy-bypass HttpClientHandler, same Salvo/AppBranding.Version user-agent, same application/vnd.github.v3+json accept, same 15s timeout. The repo-path derivation (new Uri(AppBranding.SupportUrl); AbsolutePath.Trim('/')) and the literal host "https://api.github.com/repos/{repoPath}/releases/..." are repeated in UpdateService.cs (231-233), MsixUpdateService.cs (173-175), and again in CachelessGithubSource.cs (69-70). The GitHub release DTO is likewise triplicated (GithubReleaseBody in UpdateService.cs 252-256, GithubRelease in MsixUpdateService.cs 205-218, plus Velopack's own). This is real drift risk (e.g. a UA or accept-header change must be made in multiple spots).


**Fix.** If touched at all, extract ONLY the shared HttpClient factory and the "https://api.github.com/repos/{repoPath}/releases/..." URL builder into a tiny static helper consumed by the two Salvo-owned HttpClient-based paths (MsixUpdateService, VelopackUpdateService.TryFetchGithubReleaseBodyAsync). Do NOT fold in CachelessGithubSource — it routes through Velopack's IFileDownloader with a "Velopack" UA and 30s timeout, so it shares at most the URL string, not the client. Do NOT attempt to unify DTOs with Velopack's library GithubRelease type. Given the locked plan to retire the Velopack update path in Phase 3, the most defensible option is to defer this entirely (the duplicated half is scheduled for deletion) rather than invest in a shared abstraction spanning dying code.


<sub>effort M · risk low · needs-nuance (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>consistency</code> · Centralized update-HTTP timeout constant is dead; actual timeouts are hardcoded and inconsistent — `src/Salvo.App/Services/UpdateService.cs:99`</summary>


**Problem.** Timeouts.UpdateCheckerHttp = FromSeconds(8) is defined in Salvo.Core (Timeouts.cs line 35) under a "Network calls" heading but is never referenced anywhere in the repo (repo-wide search finds only the definition). Meanwhile the code that actually performs update HTTP work hardcodes its own timeouts inconsistently: UpdateService.cs line 99 and MsixUpdateService.cs line 201 both set client.Timeout = FromSeconds(15), and CachelessGithubSource.cs line 82 passes timeout: 30. The codebase deliberately centralizes such values (TaskSchedulerAutoStartService uses Timeouts.AutoStartDelay), so having a dead 8s constant plus three divergent hardcoded literals defeats that intent.


**Fix.** Treat this as two independent cleanups rather than one value-reconciliation. (1) The dead Timeouts.UpdateCheckerHttp should either be adopted or deleted. (2) The two duplicated GitHub release-body HttpClient factories (UpdateService.cs:99 and MsixUpdateService.cs:201) each hardcode 15s and are otherwise identical — extract a single Timeouts constant (e.g. GithubReleaseBodyHttp = 15s, or repurpose UpdateCheckerHttp to that value) and reference it from both. Do NOT fold CachelessGithubSource.cs:82's timeout: 30 into the same constant: it is a different Velopack Downloader API (int seconds) for a different operation, so leaving it distinct — or giving it its own named constant — is correct. This is a low-severity dead-code/consistency tidy, not a behavioral fix.


<sub>effort S · risk low · needs-nuance (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>dead-code</code> · AppIconCache.Set is never called — `src/Salvo.App/Services/AppIconCache.cs:22`</summary>


**Problem.** The public method AppIconCache.Set(string, BitmapSource?) (lines 22-26) has no callers — a repo-wide search for AppIconCache.Set returns nothing; every consumer (AppIconLoader, WindowsStartupIconLoader, BenchmarksViewModel, GroupIconView) uses only AppIconCache.Get. It is unused API surface on an internal static helper.


**Fix.** Delete Set (and its null/whitespace guard). Get already handles cache population on miss, so no caller behavior changes. If a future feature needs to pre-seed or invalidate cache entries, reintroduce it then.


<sub>effort S · risk low · confirmed (high)</sub>

</details>


### App/ViewModels  (11)

<details>
<summary><b>[medium]</b> <code>architecture</code> · MainWindowViewModel is a God class; extract the flow-graph editor surface — `src/Salvo.App/ViewModels/MainWindowViewModel.cs:36`</summary>


**Problem.** At ~1465 lines this VM owns at least a dozen unrelated responsibilities: navigation back/forward history (272-353), settings persistence + theme/language (394-528), update checking (251-618), elevation/admin restart (197-510), group CRUD (802-880), config persistence + running-state polling (663-753), benchmark history loading (686-724), AND the entire flow-graph editing surface. The flow-graph cluster alone is a self-contained ~430 lines that operates only on SelectedGroup.Graph plus the service provider: CreateNode (1075), AddNode (1087), BuildAppNodeViaEditor (1144), PickAppNodes (1169), ToAppNode (1191), InsertNodeAfterStage (1214), AddNodeToBranch (1257), MoveNodeIntoBranch (1290), ExtractBranchNodeToOuter (1319), RemoveFromAnyBranch (1327), MakeStageSequential (1341), RemoveNodeAsync (1350), AppendAppNode (945). This is the primary decomposition target — it has no dependency on settings/update/navigation state and would move cleanly.


**Fix.** Extract a FlowEditorViewModel that owns all node/branch authoring commands (CreateNode/AddNode/PickAppNodes/InsertNodeAfterStage/AddNodeToBranch/MoveNodeIntoBranch/ExtractBranchNodeToOuter/MakeStageSequential/RemoveNodeAsync/AppendAppNode). Construct it with IServiceProvider and give it TWO host callbacks — a persist callback (PersistConfig) and a refresh callback (RefreshRunningStates) — since the mutators trigger both, not just persistence; AppIconLoader is static so needs no injection. MainWindowViewModel exposes a FlowEditor property retargeted (with its SelectedGroup) whenever SelectedGroup changes; note the public flow methods are bound from views, so views must be repointed to MainVM.FlowEditor.X and RelayCommand CanExecute / PropertyChanged must be re-raised on retarget — this is the source of the 'medium' risk. Treat this as a medium-priority maintainability refactor, not a high-severity issue. Optionally also lift the back/forward navigation block (272-353) into a small NavigationHistory helper as an independent, lower-risk follow-up.


<sub>effort L · risk medium · needs-nuance (high)</sub>

</details>

<details>
<summary><b>[medium]</b> <code>duplication</code> · "Append node after current leaves" graph logic is duplicated inline — `src/Salvo.App/ViewModels/MainWindowViewModel.cs:945`</summary>


**Problem.** AppendAppNode (945-974) and the non-App branch of AddNode (1110-1136) contain byte-for-byte the same topology algorithm: find leaves via Nodes.Where(n => !Edges.Any(e => e.From == n.Id)), fall back to Start if empty, add the node, add a leaf->newNode edge per leaf, then RebuildStages. This graph-mutation primitive also does not belong in MainWindowViewModel at all — GroupGraphViewModel already owns every other topology op (AddNodeAfter, InsertAfterStage, RemoveNode, MakeStageSequential) but is missing an "append to end" method, which is why the logic leaked into the caller and got duplicated.


**Fix.** Add a single GroupGraphViewModel.AppendToEnd(NodeViewModel) that encapsulates the leaf-find + wire + RebuildStages, and have both AppendAppNode and AddNode call it.


<sub>effort M · risk low · confirmed (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>dead-code</code> · Legacy apps-list commands (AddApp/RemoveApp + helpers) are unreferenced dead code — `src/Salvo.App/ViewModels/MainWindowViewModel.cs:801`</summary>


**Problem.** AddApp (801, generates AddAppCommand) and RemoveAppAsync (1016, generates RemoveAppCommand) have zero references anywhere — no XAML binding, no code-behind, no tests (grep for AddAppCommand/RemoveAppCommand across the whole repo finds only the generator sites). Their private helpers AddInstalledApps (909) and AddAppWithEditor (976) are called only from AddApp. They were superseded by the flow path AddNode("App") -> PickAppNodes/BuildAppNodeViaEditor and RemoveNodeAsync (which the live GroupFlowView.xaml binds). This is ~130 lines of parallel, drifting implementation of add/remove-app.


**Fix.** Delete AddApp, RemoveAppAsync, AddInstalledApps, and AddAppWithEditor. The flow editor's PickAppNodes/AppendAppNode/RemoveNodeAsync are the real code paths and already cover these cases.


<sub>effort S · risk low · confirmed (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>duplication</code> · Duration formatting copy-pasted across 5 VMs, with an F1/F2 output inconsistency — `src/Salvo.App/ViewModels/AppEntryViewModel.cs:56`</summary>


**Problem.** The identical formatter `d.TotalMilliseconds < 1000 ? "{...:F0}ms" : "{...:F#}s"` is duplicated in AppEntryViewModel (56), AppBenchmarkSummaryViewModel (96), GroupRunSummaryViewModel (77), BenchmarkRowViewModel (41), and BenchmarksViewModel.FormatMedian (326). Worse, AppEntryViewModel uses F1 for the seconds branch while all four others use F2, so the same launch renders as "1.5s" on the app card but "1.50s" in the benchmark views. The median computation (sort, mid, even/odd average) is also duplicated verbatim between AppBenchmarkSummaryViewModel.Median (75) and BenchmarksViewModel.FormatMedian (318).


**Fix.** Recommendation stands. Add DurationFormat.Human(TimeSpan) and Median(IReadOnlyList<TimeSpan>) helpers in Salvo.Core.Launch and route all call sites through them, picking a single F1/F2 decision. Note the median logic is triplicated (not just duplicated): also collapse AppBenchmarkSummaryViewModel.Median:75, its FormatMedian:85, and BenchmarksViewModel.FormatMedian:318 into the one Median helper.


<sub>effort S · risk low · confirmed (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>hardcoded-value</code> · Regression thresholds hardcoded here while the sibling VM uses BenchmarkPolicy constants — `src/Salvo.App/ViewModels/AppBenchmarkSummaryViewModel.cs:47`</summary>


**Problem.** Line 47 computes regressions with `ratio >= 2.0 && ready.Count >= 3`. Those literals are exactly BenchmarkPolicy.RegressionRatio (2.0) and BenchmarkPolicy.RegressionMinSampleSize (3), which BenchmarksViewModel.RefreshAsync (line 157) already references by name for the very same calculation. So two views compute "is this a regression" against the same intended policy, but one hardcodes it — changing BenchmarkPolicy silently desyncs the per-app summary from the recent-launches table.


**Fix.** Replace `2.0` with `BenchmarkPolicy.RegressionRatio` and `3` with `BenchmarkPolicy.RegressionMinSampleSize` on line 47. No new using directive is required since the file already imports Salvo.Core.Launch.


<sub>effort S · risk low · confirmed (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>consistency</code> · Unlocalized English UI strings in an otherwise fully-localized VM — `src/Salvo.App/ViewModels/MainWindowViewModel.cs:484`</summary>


**Problem.** RestartAsAdmin passes literal English to the dialog service: "Restart as administrator" / "Could not resolve executable path." (484) and "Restart as administrator" / ex.Message (508), while every other dialog in this file uses Strings.* resources (e.g. Strings.Dialog_AutoStart_Title, Strings.Dialog_RemoveGroup_Title). RemoveNodeAsync (1355-1365) similarly builds English node labels inline ("If / Else", "Group call", $"Wait {..}s", "node") that are then shown in a localized confirm dialog. These strings never translate and are inconsistent with the codebase's localization discipline.


**Fix.** Add Strings.resx entries for the RestartAsAdmin dialog title and the "Could not resolve executable path." message, matching the pattern at lines 452/471. For RemoveNodeAsync, extract only the literal/structural fragments into resources — the fixed words ("If / Else", "Group call", the "Wait {n}s" format string, "Start "/"Stop "/"Run: " prefixes, and the "node" fallback) — while leaving the interpolated user data (App.Name, ServiceName, Command) untranslated; do not blanket-localize the whole switch.


<sub>effort S · risk low · confirmed (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>duplication</code> · Argument tokenizer and chip logic duplicated in RegistryRunValueEditorViewModel — `src/Salvo.App/ViewModels/AppEntryEditorViewModel.cs:391`</summary>


**Problem.** AppEntryEditorViewModel and RegistryRunValueEditorViewModel independently reimplement the same command-line handling: StripQuotes is identical (AppEntryEditor 391 vs RegistryEditor 406), the quote-aware token splitter is duplicated (AppEntryEditor.SplitTokens 397 vs RegistryEditor.TryParseCommand 415), the "combine `--flag value` into one chip" heuristic is duplicated (RefreshArgumentChips 161 vs ReplaceChips 379), and both wire the ArgumentChips CollectionChanged subscription + OnChipPropertyChanged empty-chip removal the same way. ArgumentChipViewModel is already shared; the parsing/heuristics are not.


**Fix.** Extract the truly-shared, deterministic pieces into a small static helper in Salvo.App.Services: (1) StripQuotes and (2) the quote-aware token splitter (SplitTokens), which the Registry editor's TryParseCommand can call for the argument-remainder portion. Optionally factor the identical ArgumentChips.CollectionChanged subscribe/unsubscribe + empty-chip-removal boilerplate into a shared chip-collection helper or base class. Do NOT extract the 'combine --flag value into one chip' heuristic as a single shared function: the two editors intentionally differ — AppEntryEditor gates combining on per-app known-argument data (needsValueFlags), while RegistryRunValueEditor deliberately uses a generic fallback because it has no per-app knowledge (documented in its comment at lines 374-378). If shared at all, that heuristic must be parameterized by a 'should-combine' predicate rather than copied.


<sub>effort M · risk low · needs-nuance (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>dead-code</code> · Unused private RemoveFlag method — `src/Salvo.App/ViewModels/AppEntryEditorViewModel.cs:351`</summary>


**Problem.** The static RemoveFlag(string args, string flag) at line 351 is never called (grep across src finds only its definition). It duplicates most of the token/flag-matching logic in ContainsFlag/NormalizeToken and is dead weight the compiler doesn't flag because it's referenced by neither test nor caller.


**Fix.** Delete RemoveFlag (lines 351-382). The shared helpers SplitTokens/NormalizeToken/StripQuotes stay because ContainsFlag still uses them; no other change needed.


<sub>effort S · risk low · confirmed (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>magic-number</code> · Default group icon "Apps24" is a magic string duplicated across many files — `src/Salvo.App/ViewModels/GroupViewModel.cs:26`</summary>


**Problem.** The default-icon literal "Apps24" is repeated in GroupViewModel (26 and 131), GroupEditorViewModel (73), GroupRunSummaryViewModel (12 and 47), MainWindowViewModel.AddGroup (808), and the Group model (Group.cs:11). The codebase already centralizes such constants (AppBranding, AppIdentifiers, AppPaths), so this default having no named home is an inconsistency and a change-in-N-places hazard.


**Fix.** Introduce one named constant (e.g. AppBranding.DefaultGroupIcon) and reference it from all string-default/fallback sites — including the one the finding missed, Controls/GroupIconView.xaml.cs:17. Scope the change strictly to the string-literal default sites; do NOT replace the `SymbolRegular.Apps24` enum fallbacks in GroupIconSpec.Parse/StringToSymbolConverter (those are the icon-rendering fallback, a separate concern). Optionally have GroupIconSpec.Parse treat the new constant as its default input so the two stay conceptually linked.


<sub>effort S · risk low · confirmed (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>duplication</code> · InstalledAppSource-to-label switch duplicated in two VMs — `src/Salvo.App/ViewModels/AddAppPickerViewModel.cs:132`</summary>


**Problem.** AddAppPickerViewModel.SourceLabel (132-139) and InstalledAppViewModel.SourceBadge (24-31) are identical switch expressions mapping InstalledAppSource to the same Strings.AddAppPicker_Source* resources. Adding a new source requires editing both, and they can silently diverge.


**Fix.** Extract the mapping into a single static helper and call it from both VMs. Place it in the Salvo.App project (e.g. static string InstalledAppSourceLabels.For(InstalledAppSource)), not in Salvo.Core where the enum is defined, so the dependency on App-layer Strings resources stays out of Core.


<sub>effort S · risk low · confirmed (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>refactor</code> · Node-kind discriminators are stringly-typed and duplicated across four call sites — `src/Salvo.App/ViewModels/MainWindowViewModel.cs:1075`</summary>


**Problem.** Node creation dispatches on raw string literals: CreateNode's switch (1075) plus `kind == "App"` special-cases in AddNode (1094), InsertNodeAfterStage (1220), and AddNodeToBranch (1264), and MoveNodeIntoBranch/branch selection compares `"else"` (1261, 1308). These literals must stay in lockstep with NodeViewModel.Kind ("App"/"Wait"/"IfElse"/... in NodeViewModel.cs) with no compile-time guarantee, so a typo like "Ifelse" fails silently by falling into the default null branch.


**Fix.** Keep this as a low-priority cleanup and reframe it. The highest-value, lowest-risk fix is to make an unrecognized kind fail loudly instead of silently: change CreateNode's `_ => null` default to throw (or assert), and centralize the kind vocabulary as shared const strings on NodeViewModel (referenced by the switch and, where practical, the XAML Tags via x:Static) so the C# side has a single source of truth. Drop the framing that an enum yields compile-time dispatch safety — the authoritative literals are XAML Tag strings, so any type-checking at that boundary is inherently runtime; a full NodeKind enum is optional polish, not the win the finding implies, and the medium effort/risk isn't justified for the marginal benefit.


<sub>effort M · risk medium · needs-nuance (high)</sub>

</details>


### App/Views+XAML  (10)

<details>
<summary><b>[high]</b> <code>correctness</code> · Changing UI language silently resets 5 unrelated settings to defaults — `src/Salvo.App/Localization/LanguageService.cs:54`</summary>


**Problem.** CloneSettings copies only 4 of AppSettings' 9 properties (Theme, MinimizeToTrayOnClose, ShowNotifications, UiCulture). SetLanguage() builds this partial clone, sets UiCulture, and calls _settings.Save(clone). SettingsStore.Save (SettingsStore.cs:25-33) replaces _current wholesale and re-serializes the whole object — it does not merge. So every time the user picks a language in Settings, AppsViewMode, AlwaysRunAsAdmin, WarnWhenElevatedAppsPresent, UpdateChannel, and ShowMainWindowOnLaunch are all reset to their defaults and persisted to disk. Concretely: a user on the Canary update channel who changes language is silently moved back to Stable and stops receiving canary updates; a user with AlwaysRunAsAdmin=true loses auto-elevation. This is real, persisted data loss triggered by an unrelated action.


**Fix.** Add a single AppSettings.Clone() method (or copy constructor) that copies all 9 fields, and route both LanguageService.SetLanguage and MainWindowViewModel.PersistSettings through it, deleting both hand-rolled per-field copies — the duplicated copy logic drifting out of sync is the actual root cause. Add a test that round-trips every AppSettings property through SetLanguage so a newly added setting can't be silently dropped again.


<sub>effort S · risk low · confirmed (high)</sub>

</details>

<details>
<summary><b>[medium]</b> <code>consistency</code> · Flow editor is entirely hardcoded English while the rest of the app is localized (10 languages incl. RTL) — `src/Salvo.App/Views/Flow/GroupFlowView.xaml`</summary>


**Problem.** GroupFlowView.xaml — a primary, central view (group authoring) — uses literal English text for essentially all of its UI, whereas every other view (MainWindow.xaml, GroupEditorWindow.xaml, etc.) routes text through {loc:Translate ...} and the app ships SupportedLanguages for 10 cultures including RTL Arabic/Hebrew. Hardcoded strings include: 'START' (69); 'Wait' + 'seconds before the next item runs' (182,187); 'If' (285); the condition ComboBoxItems 'Service running'/'File exists'/'Process running' (291-293); 'Then'/'Else' (323,362); 'Nothing here yet — add a step.' (330,369); 'Add to Then'/'Add to Else' (340,379); 'Start service'/'Stop service' (401,427); 'Run' + 'Shell'/'PowerShell'/'Direct' (458,463-465); 'Run group' (494); 'Run in parallel' (600); 'Make sequential' (586); 'Right-click for options' (596); 'Insert below' (630); 'Add item' (715); and every MenuItem Header in the three add-menus (App/Wait/If-Else/Start service/Stop service/Run command/Call group). Non-Latin users see a fully English flow editor.


**Fix.** No correction needed — the recommendation is correct. One clarifying refinement: for the ComboBoxItem cases, prefer setting Content via {loc:Translate ...} while keeping the Tag (which SelectedValuePath="Tag" binds to code), and for MenuItems localize Header while keeping Tag; both are drop-in. The composed sentences ("Wait … seconds before the next item runs" and the parallel-header "Run in parallel" + "{0} items") should be localized as whole/format strings rather than concatenated fragments, so word order works in RTL and non-English grammars. Given this is a pre-1.0 view still under active development (Flow editor Phases B-E pending per project notes), it's reasonable to batch this with the remaining flow-editor work rather than treat it as urgent, but it is a real gap worth tracking.


<sub>effort L · risk low · confirmed (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>dead-code</code> · Orphaned app-row drag/drop system wired to a no-op ReorderApp stub — `src/Salvo.App/Views/MainWindow.xaml.cs:275`</summary>


**Problem.** The app-row reorder handlers Row_PreviewMouseLeftButtonDown, Row_PreviewMouseMove, Row_DragOver, Row_DragLeave, Row_Drop, AppsContainer_Drop, AppsContainer_DragOver plus their helpers (CommitAppReorder, ResolveAppDropTarget, ClearAppDropIndicator, SetDropVisible, IsInsideButton) and fields (AppRowDragFormat, _dragStart, _dragSource, _dragSourceRow, _activeDropRow, _activeAppInsertAt) — roughly 200 lines — are referenced by no XAML. A grep for Row_*, AppsContainer_*, RowRoot, and the app DropAbove/DropBelow names across the whole repo finds them only in this code-behind; the apps list moved into GroupFlowView, which has its own independent drag system. The terminal call CommitAppReorder -> MainWindowViewModel.ReorderApp is itself a no-op stub (MainWindowViewModel.cs:1055: body is `_ = source; _ = targetIndex;`), so even if it were wired it would do nothing — contrast ReorderGroup (MainWindowViewModel.cs:1037) which is fully implemented. This is dead, misleading code and a public API that pretends to reorder but doesn't.


**Fix.** Recommendation is sound as written: delete the orphaned app-row drag handlers, their helpers (CommitAppReorder, ResolveAppDropTarget, ClearAppDropIndicator, SetDropVisible, IsInsideButton), and fields (AppRowDragFormat, _dragStart, _dragSource, _dragSourceRow, _activeDropRow, _activeAppInsertAt) from MainWindow.xaml.cs, and remove the no-op ReorderApp from MainWindowViewModel; keep the shared BeginReorderPreview/UpdateReorderPreview/EndReorderPreview, ShowDragGhost, FindVisualChild, FindAncestor used by the live group drag. Two clarifications: (1) risk is lower than "medium" — everything being removed is unreachable, ReorderApp has no live callers, and SetDropVisible/ClearAppDropIndicator are provably no-ops (_activeDropRow is never set non-null), so this is a mechanical, near-zero-risk deletion; (2) verify FindVisualChild's "RowRoot" lookup usage is only in the deleted app code (group drag uses "Bd") before removing any group-name references, which it is. Severity is better characterized as low: it is confirmed dead code with no runtime or correctness impact, its value is purely maintainability/clarity.


<sub>effort M · risk medium · confirmed (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>hardcoded-value</code> · Hardcoded, theme-unaware hex colors for service Start/Stop icons — `src/Salvo.App/Views/Flow/GroupFlowView.xaml:398`</summary>


**Problem.** The ServiceStart icon uses Foreground="#16A34A" (line 398) and ServiceStop uses Foreground="#DC2626" (line 424). These are literal colors that do not adapt to light/dark theme, and they directly violate the codebase's own documented convention in SemanticBrushes.xaml: 'App code should bind to these keys, NOT directly to LimeGreen / Gray etc.' The repo already defines StatusRunningBrush (#FF37C05A) and StatusStoppedBrush (#FF8A8A8A) for exactly this running/stopped semantics; these two hardcoded hexes are the only literal colors in the entire views/styles tree (per a repo-wide hex scan) and are close-but-inconsistent duplicates of the semantic ones.


**Fix.** Replace both literals with theme-aware brushes that PRESERVE the green(start)/red(stop) action semantics: bind Foreground on the Play24 icon to {DynamicResource SystemFillColorSuccessBrush} and on the Stop24 icon to {DynamicResource SystemFillColorCriticalBrush} (WPF-UI Fluent fill brushes; note the actual key suffix is ...Brush). Do NOT use StatusStoppedBrush for the Stop icon — it is gray (#FF8A8A8A) and would drop the red destructive affordance. StatusRunningBrush is acceptable for the Start icon (both green) but for consistency prefer the Success/Critical pairing, or add two dedicated semantic keys (e.g. ActionStartBrush/ActionStopBrush) to SemanticBrushes.xaml if a specific hue must be pinned.


<sub>effort S · risk low · needs-nuance (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>dead-code</code> · Group drop-indicator rectangles and their show/clear plumbing are never activated — `src/Salvo.App/Views/MainWindow.xaml.cs:757`</summary>


**Problem.** _activeGroupDropItem (field, line 37) and _activeDropRow (line 32) are only ever read and set to null — grep confirms no code path assigns them a non-null value. Consequently SetGroupDropVisible (757) and SetDropVisible (593) are only ever invoked with visible:false, so the GroupDropAbove/GroupDropBelow rectangles declared in MainWindow.xaml (lines 409-422, plus the app DropAbove/DropBelow) can never become visible. The static drop-bar approach was superseded by the sliding reorder-preview, but the indicator markup and methods were left behind as no-ops, adding confusion to an already dense drag file.


**Fix.** Delete the dead static-drop-bar scaffolding, now fully superseded by the sliding reorder-preview: (1) remove the `GroupDropAbove`/`GroupDropBelow` rectangles from the GroupsList ControlTemplate (MainWindow.xaml 408-422); (2) delete `SetDropVisible` (593), `SetGroupDropVisible` (757), and the `_activeDropRow` (32) and `_activeGroupDropItem` (37) fields; (3) since their bodies become permanently unreachable, delete `ClearAppDropIndicator` (460) and `ClearGroupDropIndicator` (749) entirely and drop their call sites (329, 350, 388, 402, 658, 679, 729) rather than keeping empty methods. Note: there are NO app-level `DropAbove`/`DropBelow` rectangles in the XAML to remove — the finding's mention of them is inaccurate; `SetDropVisible` already references non-existent named elements, so removing it loses nothing. Leave the reorder-preview machinery (`UpdateReorderPreview`, `_activeAppInsertAt`, `_activeGroupInsertAt`, ghost adorner) untouched — that is the live path.


<sub>effort S · risk low · confirmed (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>duplication</code> · Argument-chip focus handlers duplicated verbatim across two editor windows — `src/Salvo.App/Views/AppEntryEditorWindow.xaml.cs:33`</summary>


**Problem.** ChipEditBox_IsVisibleChanged, ChipEditBox_Loaded, FocusChipEditor, and ChipEditBox_LostFocus (AppEntryEditorWindow.xaml.cs:33-70) are byte-for-byte identical to the same four methods in RegistryRunValueEditorWindow.xaml.cs:37-74, and both XAMLs wire the same handler names. This ~30-line block is copy-pasted; a change to chip focus/commit behavior must be made in two places and can drift.


**Fix.** Extract the chip-editing focus/commit logic into a WPF attached behavior (a static class exposing attached properties that internally subscribe to Loaded, IsVisibleChanged, and LostFocus on the TextBox), reference it from both editor XAMLs, and delete all four duplicated code-behind handlers plus their XAML handler wiring. Prefer this over the "shared static helper" alternative: a static helper would only deduplicate FocusChipEditor while leaving the three event handlers and their per-XAML wiring duplicated in both windows.


<sub>effort M · risk low · confirmed (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>duplication</code> · Three sidebar nav buttons repeat near-identical control templates — `src/Salvo.App/Views/MainWindow.xaml:260`</summary>


**Problem.** A reusable SidebarNavButton style is defined (lines 63-83), but WindowsStartupButton overrides its Template inline anyway (226-240) to add an active-state DataTrigger, and BenchmarksButton (267-288) and SettingsButton (315-336) each declare their own full inline Button.Style with a copy of the same Border/ContentPresenter template. The three templates are identical except for the active-view DataTrigger binding (IsStartupView / IsBenchmarksView / IsSettingsView). This is ~80 lines of duplicated markup that must be edited in three places to change nav-item chrome.


**Fix.** Fold the active-state DataTrigger into the single SidebarNavButton style: add <DataTrigger Binding="{Binding Tag, RelativeSource={RelativeSource Self}}" Value="True"><Setter TargetName="Bd" Property="Background" Value="{DynamicResource SubtleFillColorTertiaryBrush}"/></DataTrigger> to its ControlTemplate.Triggers, then on each button apply Style="{StaticResource SidebarNavButton}" and set Tag="{Binding IsStartupView}" / Tag="{Binding IsBenchmarksView}" / Tag="{Binding IsSettingsView}" respectively. Delete the WindowsStartupButton inline Template override and the two inline Button.Style blocks. Do a quick visual check that the active-indicator highlight still tracks the selected view.


<sub>effort M · risk medium · confirmed (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>magic-number</code> · Drag merge-band and opacity thresholds inlined instead of following the UiMetrics/Durations pattern — `src/Salvo.App/Views/Flow/GroupFlowView.xaml.cs:411`</summary>


**Problem.** The flow drag logic hardcodes tuning constants inline: the DragOver merge band relative>0.30 && relative<0.70 (411), the more-forgiving Drop merge band relative>0.10 && relative<0.90 (551), and the dragged-row opacity 0.35 (331). The codebase deliberately centralizes such UI tuning values in Animations/Durations.cs (Durations, UiMetrics — e.g. RowReorderSnapToleranceY), so these inline literals are inconsistent with the established pattern and hard to tune coherently. The two different bands (0.30/0.70 vs 0.10/0.90) also encode a hysteresis relationship that is easy to break when edited independently.


**Fix.** If addressed at all, name only the merge bands as local private const fields inside GroupFlowView (e.g. MergeBandDragOverInner/Outer = 0.30/0.70, MergeBandDropInner/Outer = 0.10/0.90), keeping them beside their explanatory comments rather than moving them to the shared UiMetrics class — they are single-use and only meaningful in this drag handler. Do NOT frame the two bands as a coupled hysteresis pair; they are independent per-phase thresholds and the actual hysteresis is the _activeMergeRow arming logic. Leave the 0.35 ghost opacity inline; extracting a single-use dim value is not worth it.


<sub>effort S · risk low · needs-nuance (medium)</sub>

</details>

<details>
<summary><b>[low]</b> <code>consistency</code> · Admin pill 'Active' text uses x:Static and won't update on live language switch — `src/Salvo.App/Views/MainWindow.xaml:169`</summary>


**Problem.** The admin pill's default text binds via {loc:Translate Admin_Pill_Recommended} (line 154), which refreshes when the user changes language at runtime (LocalizationManager raises Item[] change). But the DataTrigger that swaps to the 'Active' state sets the text with {x:Static res:Strings.Admin_Pill_Active} (line 169), which is resolved once at template load against the then-current culture and never updates. So after switching language with the app running, an elevated session shows the pill's 'Active' label in the previous language while its sibling text is correctly re-localized — an inconsistent, partially-stale UI.


**Fix.** Fix is real but keep it structural. Prefer rendering two TextBlocks in the pill template — one bound to {loc:Translate Admin_Pill_Recommended}, one to {loc:Translate Admin_Pill_Active} — and toggle their Visibility/Foreground off IsRunningAsAdmin via the existing DataTrigger; both then stay live-localized without VM plumbing. If instead exposing a VM string property, note it must also subscribe to LocalizationManager culture changes and raise OnPropertyChanged, otherwise it will be equally stale (the existing AdminStatusText at MainWindowViewModel.cs:199 is a sibling instance of the same latent staleness worth fixing together). Do NOT simply swap x:Static for loc:Translate: WPF does not support Binding in a Setter.Value inside ControlTemplate.Triggers, which is precisely why x:Static was used.


<sub>effort S · risk low · confirmed (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>dead-code</code> · OnClosing guard 'Application.Current.Windows.Count > 0' is always true — `src/Salvo.App/Views/MainWindow.xaml.cs:265`</summary>


**Problem.** In OnClosing, the condition `_viewModel.MinimizeToTrayOnClose && Application.Current.Windows.Count > 0` gates hiding-to-tray. While MainWindow is closing it is still a member of Application.Current.Windows, so Count is always >= 1 and the second clause can never be false. The guard therefore has no effect and misleads a reader into thinking there is a window-count edge case being handled.


**Fix.** Drop the always-true `&& Application.Current.Windows.Count > 0` clause so the guard reads `if (_viewModel.MinimizeToTrayOnClose)`. If a real "no other windows remain" behavior was ever intended, that is not what MinimizeToTrayOnClose means here, so do not resurrect it speculatively — just remove the inert clause.


<sub>effort S · risk low · confirmed (high)</sub>

</details>


### Core/Launch  (8)

<details>
<summary><b>[high]</b> <code>correctness</code> · Early-exit watcher reports ExitedEarly for shell-launched apps whose PID has not resolved yet — `src/Salvo.Core/Launch/ReadinessDetector.cs:109`</summary>


**Problem.** WatchEarlyExitAsync waits EarlyExitGrace (1s, Timeouts.ReadinessEarlyExitGrace) and then concludes ExitedEarly the moment IsTreeAlive() returns false (ReadinessDetector.cs:107-112). For the shell-launch path, no Process handle is attached, so RootPid is resolved asynchronously by ResolvePidAsync with a 5s deadline polling every 200ms (LaunchTelemetryService.cs:69-97, Timeouts.PidResolveDeadline=5s). Until the PID resolves, LaunchSession.EnumerateDescendantPids() returns Array.Empty (LaunchSession.cs:122-134, no job + null root), so IsTreeAlive() returns false (LaunchSession.cs:137-159). Result: any shell-launched app whose process takes longer than ~1s to appear in the inspector is falsely classified ExitedEarly at the 1s mark, aborting readiness detection, even though it is still starting. The LaunchTelemetryServicePidResolver test only passes because its stub resolves the PID at ~600ms (<1s), masking the bug; ReadinessDetectorTests never exercise the >1s early-exit path.


**Fix.** Gate the ExitedEarly return in WatchEarlyExitAsync on session.RootPid != null: while RootPid is still unresolved, keep polling instead of declaring ExitedEarly; only conclude early exit once a PID has been resolved and the tree is then dead. Prefer this RootPid-based gate over the alternative 'saw the tree alive at least once' flag suggested in the finding — the flag-only variant would regress the attached-process path, where a process that starts and crashes within the 1s grace is a legitimate ExitedEarly (RootPid is set synchronously in AttachRootProcess, so the RootPid gate preserves that detection while the flag would suppress it and mislabel it TimedOut). Accept that a shell launch which never produces a locatable process now falls through to TimedOut rather than ExitedEarly, which is honest since the two are indistinguishable without a resolved PID. Optionally also align ReadinessEarlyExitGrace with the PID-resolve budget for shell launches. Add a ReadinessDetectorTests case exercising the >1s early-exit path with a null/late RootPid to lock this in.


<sub>effort S · risk low · confirmed (high)</sub>

</details>

<details>
<summary><b>[medium]</b> <code>performance</code> · Every probe independently re-enumerates the descendant PID set each poll; shell-launched apps trigger repeated full process-table snapshots — `src/Salvo.Core/Launch/LaunchSession.cs:103`</summary>


**Problem.** EnumerateDescendantPids() is called on every poll by MainWindowProbe (MainWindowProbe.cs:45), WaitForInputIdleProbe (WaitForInputIdleProbe.cs:24), ActivityQuietProbe (ActivityQuietProbe.cs:33) and the early-exit watcher via IsTreeAlive (ReadinessDetector.cs:109) — all concurrently, at 250-500ms intervals, for up to the 30s readiness window. When no job object is assigned (the shell-launch case, where no Process handle was available), each call falls through to ProcessTreeSnapshot.GetDescendantPids (LaunchSession.cs:124), which runs a full CreateToolhelp32Snapshot over every process on the machine and rebuilds a parent->children dictionary (ProcessTreeSnapshot.cs:11-88). That is several whole-system process snapshots per second for the entire launch window. Even in the job-assigned case each call does AllocHGlobal + marshaling + a fresh int[] allocation per probe per poll (ChildProcessTracker.cs:50-104).


**Fix.** Memoize the descendant-PID result in LaunchSession behind a short TTL (~100-200ms, i.e. under one poll interval) so the concurrent probes share one snapshot per cycle instead of each recomputing it. Two caveats: (1) make the cache thread-safe — the probes run on concurrent tasks, so guard the cached list + timestamp with the existing _lock (or an atomic swap); (2) keep the TTL short enough that newly-spawned descendants are still discovered promptly during the launch window. ActivityQuietProbe's per-PID CPU/IO sampling is unaffected since it only consumes the PID list. This collapses the ~4 near-simultaneous callers per cycle into a single snapshot, cutting the full-process-table walks (and the per-call AllocHGlobal/marshal even in the job case) by roughly 4x.


<sub>effort M · risk medium · confirmed (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>error-handling</code> · Faulted schema-init task is cached forever; documented corruption re-init is impossible and the store stays permanently broken — `src/Salvo.Core/Launch/SqliteLaunchBenchmarkStore.cs:73`</summary>


**Problem.** The constructor starts _initTask = Task.Run(InitializeCoreAsync) once (line 65) and InitializeAsync() just returns that same cached field (line 73). If InitializeCoreAsync faults (disk full, locked/corrupt db, permissions), every subsequent SaveAsync/GetRecentAsync/etc. does `await _initTask` (e.g. lines 94, 144, 163, 180, 205, 228) and re-throws the same stored exception for the entire process lifetime — telemetry silently dies with no self-heal. This directly contradicts the XML doc on InitializeAsync (lines 68-72), which claims it is 'kept for explicit fire-and-await use cases (tests, re-init after a corruption recovery)': returning the immutable cached task means a re-init can never actually re-run.


**Fix.** Primary fix (low effort, real): correct the misleading XML doc on InitializeAsync — it should not claim support for "re-init after a corruption recovery," since returning the cached task cannot re-run and nothing in production attempts that. Optional secondary improvement: if resilience to a transient locked/busy DB is desired, make init re-runnable by guarding `_initTask` behind a lock/Interlocked and recreating it when the prior attempt faulted (or expose a TryReinitialize). This is low-risk but low-value given graceful degradation in LaunchTelemetryService and that the dominant failure modes are persistent. Do not frame this as a store that leaves the app broken — clarify that only benchmark telemetry is lost, and the launch path is unaffected.


<sub>effort M · risk low · needs-nuance (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>performance</code> · SQLite store uses Shared cache with no WAL and no busy timeout despite concurrent launch writes and analytics reads — `src/Salvo.Core/Launch/SqliteLaunchBenchmarkStore.cs:58`</summary>


**Problem.** The connection string sets Cache=SqliteCacheMode.Shared (line 62) but never enables WAL journal mode or a busy_timeout PRAGMA in InitializeCoreAsync (lines 75-89). Group launches observe many apps concurrently, each on its own Task calling SaveAsync with its own fresh connection (LaunchTelemetryService.cs:66,111), while DependencyHintsAnalyzer concurrently issues long read queries via GetAllSinceAsync/GetResourcesSinceAsync (DependencyHintsAnalyzer.cs:25-26). Under the default rollback journal, readers and writers block each other; shared-cache mode adds table-level SQLITE_LOCKED contention that the default busy handler does not resolve, so contention surfaces as thrown 'database is locked' errors (only mitigated by the implicit 30s command-timeout retry loop, i.e. long stalls).


**Fix.** Introduce a single private connection-factory helper (e.g. OpenConnectionAsync) that all six DB methods call: it opens the connection and then runs PRAGMA busy_timeout=<n> (busy_timeout is per-connection, so it MUST be applied on every open — not only in InitializeCoreAsync as originally proposed). Enable journal_mode=WAL once in InitializeCoreAsync (WAL persists in the DB file). Drop Cache=Shared to use the default private cache. Note that Microsoft.Data.Sqlite already retries on SQLITE_BUSY/SQLITE_LOCKED up to the 30s command timeout, so the practical win is avoiding brief stalls and aligning with documented concurrency guidance rather than fixing frequently-thrown errors; treat this as a low-severity robustness/best-practice cleanup.


<sub>effort S · risk medium · needs-nuance (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>dead-code</code> · LaunchOutcome.PidNotFound is never produced; failed PID resolution is misclassified as TimedOut/ExitedEarly — `src/Salvo.Core/Launch/LaunchOutcome.cs:9`</summary>


**Problem.** LaunchOutcome.PidNotFound (and to a lesser extent Failed) is defined and rendered in the UI (AppEntryViewModel.cs:51-52) but no code path in the launch pipeline ever assigns it. When ResolvePidAsync exhausts its 5s deadline without finding a PID (LaunchTelemetryService.cs:76-96) it just returns silently; ObserveAsync then records whatever the detector returned — TimedOut, or (per the finding above) a false ExitedEarly — never PidNotFound. So the purpose-built outcome that would tell a user 'we launched it but never found the process' is unreachable, and genuine PID-resolution failures are indistinguishable from real timeouts/early exits.


**Fix.** The original recommendation is sound but vague on placement. Cleanest fix: in ObserveAsync, after DetectAsync returns, detect the specific shell-launch-resolution-failure condition — i.e. this launch went through the inspector/matchers path (process was null at BeginObservation) AND session.RootPid is still null — and override result.Outcome to PidNotFound before BuildMetrics, rather than letting the detector's ExitedEarly/TimedOut stand. ObserveAsync is the right layer because only it (via BeginObservation) knows whether PID resolution was even expected; ReadinessDetector has no such context. If instead the taxonomy is intentionally collapsed, delete both PidNotFound and the never-produced Failed value from the enum and their arms in AppEntryViewModel (51-52) so the UI does not advertise states that cannot occur. Prefer the first (emit the outcome) since the resolution-failure vs. genuine-early-exit distinction has real diagnostic value for shell-launched apps.


<sub>effort S · risk low · confirmed (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>magic-number</code> · Hardcoded 500ms SendMessageTimeout in the readiness hot path bypasses the centralized Timeouts class — `src/Salvo.Core/Launch/Probes/MainWindowProbe.cs:72`</summary>


**Problem.** SendMessageTimeoutW(hwnd, WM_NULL, ..., SMTO_ABORTIFHUNG, 500, out _) hardcodes a 500ms responsiveness timeout inline (MainWindowProbe.cs:72). Every other timing constant in this subsystem is centralized (Timeouts.ProbePollDefault/ProbePollService/WaitForInputIdlePerAttempt/etc., ReadinessThresholds, BenchmarkPolicy), and this literal is on the per-window, per-poll readiness path where the value directly affects how long the probe can stall waiting on a hung window. The magic 500 is inconsistent with the established convention and is undocumented.


**Fix.** Add Timeouts.MainWindowResponsivenessProbe = TimeSpan.FromMilliseconds(500) in the "Readiness probes" region and reference it at the call site as (uint)Timeouts.MainWindowResponsivenessProbe.TotalMilliseconds, since SendMessageTimeoutW's timeout parameter is a uint in milliseconds, not a TimeSpan.


<sub>effort S · risk low · confirmed (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>correctness</code> · ActivityQuietProbe does not reset quietSince on ticks where no PID could be sampled — `src/Salvo.Core/Launch/Probes/ActivityQuietProbe.cs:61`</summary>


**Problem.** quietSince is only cleared in the else branch (lines 82-84) inside `if (observedAny && lastTickAt is ...)`. If a poll tick observes no comparable PID between prior and current samples (observedAny stays false — e.g. transient PID churn or all descendants briefly unsampleable), the whole quiet-evaluation block is skipped and quietSince retains its prior value. A genuine burst of CPU/IO activity that happens to fall on such an unsampled tick will not reset the quiet timer, so the probe can accumulate the QuietWindow across a real activity spike and fire 'ActivityQuiet' prematurely.


**Fix.** If tightening is desired, treat a tick with observedAny == false as a break in quiet continuity and reset quietSince = null, so the quiet window only accrues across consecutive confirmed-quiet ticks. Drop the `wallDeltaMs <= 0` clause from the recommendation — it is effectively unreachable given a positive PollInterval. Note the tradeoff: for apps that periodically spawn/exit short-lived children this may occasionally delay readiness; because the main process normally persists (keeping observedAny true) this cost is small in practice. This is a defensiveness refinement, not a fix for a broadly-reproducible premature-firing bug.


<sub>effort S · risk low · needs-nuance (medium)</sub>

</details>

<details>
<summary><b>[low]</b> <code>testing</code> · Readiness probes have no unit tests, including the fully mockable ServiceRunningProbe — `src/Salvo.Core/Launch/Probes/ServiceRunningProbe.cs:25`</summary>


**Problem.** None of the four probes (ActivityQuietProbe, MainWindowProbe, WaitForInputIdleProbe, ServiceRunningProbe) have tests — the test suite covers ReadinessDetector, LaunchSession, the SQLite store, the PID resolver and the analyzer, but not the probes themselves. ServiceRunningProbe in particular takes its only dependency through the IServiceController abstraction (constructor line 15) and has clear branch logic — fires on Running, aborts on NotFound, polls otherwise (lines 30-40) — making it trivially unit-testable with a stub controller, yet it is untested. This leaves the Running/NotFound/timeout transitions unguarded against regressions.


**Fix.** Add unit tests for ServiceRunningProbe using a stub IServiceController covering the Running (fires), NotFound (aborts, returns false), and pending-then-running transitions; this is the lowest-cost probe to cover and pins the readiness contract.


<sub>effort M · risk low · confirmed (high)</sub>

</details>


### Core/Platform  (6)

<details>
<summary><b>[medium]</b> <code>duplication</code> · Run/StartupApproved registry paths and delete logic duplicated across 3 files, contradicting the stated single-source-of-truth design — `src/Salvo.Core/WindowsStartup/RegistryRunValueWriter.cs:13`</summary>


**Problem.** RegistryRunValueWriter's own header comment (lines 6-9) says the write path was centralized here so 'the two paths' (in-process WindowsStartupService and the elevated Salvo.Elevator) 'don't drift apart'. But that centralization is only partial. (1) The four Run/StartupApproved path literals are copy-pasted in three places: RegistryRunValueWriter.cs lines 13-16, WindowsStartupService.cs lines 9-13, and Salvo.Elevator/Program.cs lines 96-98 (confirmed by grep: these are the only 3 files containing the literals, with no shared constant). (2) The DELETE path was never centralized: WindowsStartupService.TryRemove/RemoveRegistryValue/RemoveFromApproved (lines 62-106, 302-324) and Salvo.Elevator.Program.DeleteRunValue (lines 94-118) are two independent reimplementations of 'delete the Run value + delete its StartupApproved entry'. This is exactly the drift the writer comment warns against, and it already produced divergence (see the HKCU 32-bit addressing finding).


**Fix.** Recommendation is sound: extract one internal static class holding RunPath/RunWow64Path/StartupApprovedRun/StartupApprovedRun32/StartupApprovedFolder, and add RegistryRunValueWriter.Delete(source, name) that both WindowsStartupService.TryRemove and the elevator's RunRegistryDelete call, and delete the dead RunWow64Path const from the writer. Scoping note: the shared Delete helper can only cover the four registry-Run sources (the writer's ResolveLocation models only those); TryRemove's StartupFolder branch (file deletion + StartupApprovedFolder) must remain in WindowsStartupService. While consolidating, pick ONE addressing scheme for the 32-bit HKCU/HKLM case (OpenBaseKey+Registry32, as the writer/elevator already use) so the divergence with WindowsStartupService's WOW6432Node-literal approach is eliminated at the same time.


<sub>effort M · risk medium · confirmed (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>correctness</code> · HKCU 32-bit Run entries are read from a different physical key than they are edited/deleted from — `src/Salvo.Core/WindowsStartup/WindowsStartupService.cs:20`</summary>


**Problem.** Enumerate reads RegistryRunUser32 entries from the literal key HKCU\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run using the default (64-bit) view (line 20: root=Registry.CurrentUser, subKey=RunWow64Path). But every mutation path addresses HKCU 32-bit entries as the 32-bit registry VIEW of the plain Run path instead: RegistryRunValueWriter.ResolveLocation lines 163-165 (OpenBaseKey(CurrentUser, Registry32) + RunPath), WindowsStartupService.GetSiblingValueNames line 154, and Salvo.Elevator/Program.cs line 103. Unlike HKLM\Software, HKCU\Software is NOT subject to WOW64 registry redirection, so the 32-bit view of HKCU\...\Run resolves to the SAME key as the 64-bit view (i.e. the non-WOW6432Node Run) — a different physical key than the one Enumerate read. Consequence: editing an enumerated RegistryRunUser32 value fails with 'Original value no longer exists' (Write, lines 59-62) and deleting it is a silent no-op (the value lives under WOW6432Node, the delete targets plain Run). HKLM 32-bit entries are unaffected because HKLM\Software IS redirected, so the two conventions coincide there.


**Fix.** Align the WRITE side to the READ side, not vice versa. For all *32 sources, use the literal RunWow64Path (Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run) with the default registry view in RegistryRunValueWriter.ResolveLocation, WindowsStartupService.GetSiblingValueNames, and Salvo.Elevator.DeleteRunValue — matching Enumerate (lines 20/22) and the already-correct in-process TryRemove (lines 72/78), which use the literal WOW6432Node path. This is consistent for both hives: for HKLM the literal WOW6432Node key equals the 32-bit view of Run (redirected), and for HKCU read and write both target the same literal WOW6432Node key. Do NOT change Enumerate to read the 32-bit view of RunPath: because HKCU\Software is not WOW64-redirected, that view resolves to the plain HKCU Run key, which would duplicate every HKCU RegistryRunUser entry as a RegistryRunUser32 entry.


<sub>effort M · risk medium · needs-nuance (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>duplication</code> · IO_COUNTERS struct and CloseHandle P/Invoke duplicated across the Native interop files — `src/Salvo.Core/Native/ProcessIoInterop.cs:12`</summary>


**Problem.** The IO_COUNTERS struct is declared identically (same 6 ulong fields) in ProcessIoInterop.cs lines 12-20 and JobObjectInterop.cs lines 17-25. Separately, the kernel32 CloseHandle P/Invoke is declared three times: ProcessIoInterop.cs line 27, JobObjectInterop.cs line 85, and Toolhelp32Interop.cs line 41. This is the kind of interop duplication that invites subtle marshalling drift (e.g. a CharSet/SetLastError tweak applied to one copy but not another).


**Fix.** Consolidate the shared native declarations into a single internal Kernel32/NativeMethods class in Salvo.Core.Native and reference it from the specialized interop files. Lead with IO_COUNTERS (the real substance — a 6-field struct duplicated identically and embedded in JOBOBJECT_EXTENDED_LIMIT_INFORMATION, so drift there would break marshalling). Folding CloseHandle in is a reasonable secondary cleanup, but drop the "CharSet/SetLastError drift" justification for it — CloseHandle has no CharSet and a trivially stable signature, so that copy carries no real drift risk.


<sub>effort S · risk low · confirmed (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>correctness</code> · TryAddUserRunEntry silently overwrites an existing Run value of the same name — `src/Salvo.Core/WindowsStartup/WindowsStartupService.cs:108`</summary>


**Problem.** TryAddUserRunEntry (lines 108-136) calls key.SetValue(name, command, ...) with no existence check, so it silently overwrites any existing HKCU Run value whose name collides. The sole caller, WindowsStartupViewModel.AddEntryAsync (lines 174-182), derives name from Path.GetFileNameWithoutExtension(path) with no collision guard. So adding, e.g., a second executable named 'updater.exe' silently replaces the command of an unrelated existing autostart entry — data loss the user never sees (the op reports 'Added'). Note RegistryRunValueWriter.Write already guards duplicates (lines 54-57), so the two write paths are inconsistent about this.


**Fix.** Keep the proposed fix: in TryAddUserRunEntry, after opening the Run key, check key.GetValueNames() for a case-insensitive match on name and return Failed (mirroring RegistryRunValueWriter.Write lines 54-57) before calling SetValue. This service-level guard is the load-bearing change. Secondarily, improve the VM (AddEntryAsync) to surface a rename-or-confirm prompt on collision rather than a dead-end error, since a bare failure leaves the user no way to add a legitimately distinct executable that happens to share a base name.


<sub>effort S · risk low · confirmed (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>error-handling</code> · All elevation start failures are logged as 'User cancelled elevation' — `src/Salvo.Core/Elevation/ElevationClient.cs:81`</summary>


**Problem.** InvokeAsync wraps Process.Start in a single catch that logs every failure with the message 'User cancelled elevation or helper failed to start' at Warning (lines 81-85). A genuine defect — the helper failing to ShellExecute, a corrupt path, an antivirus block — is logged with wording that tells the operator the user cancelled, which will actively mislead diagnostics. UAC cancellation surfaces specifically as Win32Exception with NativeErrorCode 1223 (ERROR_CANCELLED).


**Fix.** Keep the exception attached (it already preserves diagnostics). Refine severity/wording: inspect the caught exception — when it is a Win32Exception with NativeErrorCode == 1223 (ERROR_CANCELLED), log at Debug/Information with a "user cancelled elevation" message; for any other exception, log at Error with a "helper failed to start" message. This separates benign cancels from real defects for filtering/alerting. Note the value is log-level hygiene, not recovering lost detail — the current LogWarning(ex, ...) already records the underlying exception.


<sub>effort S · risk low · needs-nuance (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>testing</code> · Deterministic startup-approval logic has no unit coverage — `src/Salvo.Core/WindowsStartup/RegistryRunValueWriter.cs:18`</summary>


**Problem.** No test file references RegistryRunValueWriter, WindowsStartupService, or ElevationClient (grep over tests/ returned nothing). Several branches here are pure, OS-independent logic that is cheap to test and easy to break in a refactor: RegistryRunValueWriter.Write's validation branches (empty original/new name, rename collision, missing original — lines 22-62), WindowsStartupService.IsApproved's byte-flag parsing (lines 275-291), and BuildApprovedValue's 12-byte layout (lines 293-300, where the enabled/disabled 0x02/0x03 flag and the FILETIME offset are format-critical).


**Fix.** Extract the approval-flag parse into a pure static bool ParseApprovedEnabled(byte[]) used by both IsApproved and tests; round-trip test it against BuildApprovedValue(true/false) and cover the empty/short-array fallbacks. Separately unit-test only the pre-registry validation branches of Write (empty OriginalName, empty NewName, empty Command, unsupported source — lines 22-42), which return before any RegistryKey access, after exposing them via InternalsVisibleTo. Do not attempt to unit-test the rename-collision, missing-original, or approval re-keying paths — they require a real sealed RegistryKey (I/O) and belong in integration tests if pursued at all.


<sub>effort M · risk low · needs-nuance (medium)</sub>

</details>


### Core/Services+Models+Data  (11)

<details>
<summary><b>[medium]</b> <code>error-handling</code> · RunCommand blocks up to 60s ignoring the CancellationToken; comment claims it is cancel-aware — `src/Salvo.Core/Services/AppOrchestrator.cs:482`</summary>


**Problem.** In ExecuteGraphAsync a RunCommandNode runs via RunCommand, which does a synchronous `process.WaitForExit(60_000)` (line 482). The comment on line 477-479 says "Wait synchronously up to a generous timeout ... Cancel-aware via the token," but the token is only checked AFTER the wait returns (`cancellationToken.ThrowIfCancellationRequested()` on line 489). So when a group launch is cancelled while a RunCommand node is executing, the whole graph walk stays blocked on that node for up to 60 seconds before cancellation is observed. Because ExecuteGraphAsync awaits each running node task (line 315 `Task.WhenAny`), one hung/slow command makes StopGroup/cancel unresponsive. The comment is actively misleading about the behavior.


**Fix.** Replace the blocking `process.WaitForExit(60_000)` with a genuinely cancellable async wait. Make RunCommand async (e.g. RunCommandAsync returning Task<OperationResult>) and await it in the RunCommandNode branch so a real yield point exists. Inside, create a linked CTS: `using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));` then `try { await process.WaitForExitAsync(timeoutCts.Token); } catch (OperationCanceledException)` — distinguish the two causes: if the caller's `cancellationToken` is cancelled, kill the process (`process.Kill(entireProcessTree: true)`) and rethrow OperationCanceledException; if only the timeout fired, kill the process and return OperationResult.Failed("Timed out"). Correct the comment at lines 477-479 to state the wait is bounded by a 60s timeout and is now truly cancel-aware. This also removes the current inline synchronous blocking of the dispatch loop (line 307), restoring parallel node execution while a command runs.


<sub>effort S · risk low · confirmed (high)</sub>

</details>

<details>
<summary><b>[medium]</b> <code>performance</code> · Composite provider enumerates independent providers sequentially and double-sorts — `src/Salvo.Core/Services/CompositeInstalledAppsProvider.cs:17`</summary>


**Problem.** EnumerateAsync awaits each provider one at a time in a foreach (lines 17-22). The three wired providers (App.xaml.cs:315-319: ShellInstalledAppsProvider doing STA COM enumeration of every installed app, WindowsServicesProvider enumerating all services with a per-service registry ImagePath lookup, ScoopInstalledAppsProvider scanning the filesystem) are fully independent, so their latencies add up on the app-picker path (AddAppPickerViewModel.cs:164). Separately, each provider already sorts its own results (ScoopInstalledAppsProvider:80, ShellInstalledAppsProvider:116, WindowsServicesProvider:73) and the composite re-sorts the merged list (line 24), so the per-provider sorts are wasted work whenever they run through the composite.


**Fix.** Fan the three providers out with Task.WhenAll, passing the same CancellationToken to each, then concatenate and apply the single final sort. This is the substantive win. Preserve cooperative cancellation (the loop's ThrowIfCancellationRequested is replaced by token propagation into each provider). The double-sort cleanup is optional and low-value on its own — dropping per-provider sorts saves only microseconds and the providers may still be used standalone, so leaving them (or documenting that the composite always re-sorts) is fine.


<sub>effort S · risk low · confirmed (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>error-handling</code> · IsRunning leaks matched Process objects on the AUMID path — `src/Salvo.Core/Services/ProcessInspector.cs:34`</summary>


**Problem.** IsRunning ends with `return FindProcessesByAumid(aumids).HasAny;` (line 34). FindProcessesByAumid (lines 134-162) disposes only the NON-matching Process objects (line 155); every matching Process is added to the returned `matches` list and left undisposed. IsRunning discards that list, so matched Process handles are never released. The other two callers handle this correctly — FindMatchingPids wraps the result in try/finally DisposeAll (lines 63-76) and TryKill disposes via KillProcesses (lines 102, 190-193). IsRunning is the hot path (status refresh runs on Timeouts.StatusRefreshInterval = 3s) so for any UWP/AUMID-matched app this leaks a handle on every poll until finalization.


**Fix.** Keep the recommended fix — capture the tuple and dispose in a finally: `var found = FindProcessesByAumid(aumids); try { return found.HasAny; } finally { DisposeAll(found.Processes); }` — for IDisposable correctness, consistency with the other two callers, and future-proofing. But frame it as hygiene/consistency (low severity), not an active OS-handle leak: in this path the matched Process objects only have .Id accessed and therefore hold no native handle. The "stop on first match" overload is an optional micro-optimization, not required.


<sub>effort S · risk low · needs-nuance (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>architecture</code> · AppOrchestrator is a God class mixing four unrelated responsibilities — `src/Salvo.Core/Services/AppOrchestrator.cs:11`</summary>


**Problem.** AppOrchestrator (~540 lines) bundles: (1) per-app/service lifecycle (LaunchAppCore/StopApp, lines 61-178), (2) a full topological flow-graph execution engine with ready-queue, skip propagation, cycle detection and cross-group recursion (ExecuteGraphAsync + local RunNodeAsync/FireOutgoing/PropagateSkip, lines 232-444), (3) a shell/PowerShell/direct command runner including an ad-hoc command-line parser (RunCommand + ParseDirect, lines 446-526), and (4) a runtime condition evaluator over FlowCondition subtypes (EvaluateCondition, lines 528-539). These have distinct reasons to change and distinct test needs; parsing and condition logic are pure and could be unit-tested in isolation but are currently buried in the orchestrator and reachable only through graph execution.


**Fix.** Do not pursue the full three-class God-class refactor — it fights the existing coupling (the executor invokes LaunchAppCore/StopApp directly) for largely aesthetic benefit and risks regressions in working, tested orchestration logic. If anything is worth doing, extract only ParseDirect (and optionally EvaluateCondition) into a small pure static helper so its command-line-parsing edge cases (quoted exe path, unterminated quote, no-argument, no-space) can be unit-tested directly; leave the graph executor and lifecycle methods in AppOrchestrator, since they are already covered by GraphOrchestratorTests/AppOrchestratorTests and splitting them adds interface seams without clear payoff.


<sub>effort L · risk medium · needs-nuance (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>consistency</code> · KnownAppsDatabase uses reflection-based JSON while the rest of Core uses a source-gen context — `src/Salvo.Core/Services/KnownAppsDatabase.cs:86`</summary>


**Problem.** Load() calls `JsonSerializer.Deserialize<KnownAppsFile>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })` (lines 86-89) — reflection-based serialization with ad-hoc options. Everywhere else the codebase deliberately uses the source-generated ConfigurationJsonContext (JsonConfigStore.cs:47/112) with camelCase + polymorphic type registration. KnownAppsFile/KnownApp/KnownAppMatch/KnownArgument are not registered in ConfigurationJsonContext. This is inconsistent, pulls the reflection-based serializer into an app that is otherwise on a cold-start optimization branch (perf/cold-start, R2R), and forecloses any future PublishTrimmed/AOT because the reflection path would silently drop members.


**Fix.** Register the four DTOs (KnownAppsFile, KnownApp, KnownAppMatch, KnownArgument) on the existing ConfigurationJsonContext — adding [JsonSerializable(typeof(KnownAppsFile))] is sufficient since the source generator will pull in the nested types — and deserialize via JsonSerializer.Deserialize(stream, ConfigurationJsonContext.Default.KnownAppsFile), dropping the hand-built JsonSerializerOptions. The context's CamelCase policy matches the JSON keys, so no [JsonPropertyName] attributes are needed beyond what exists. This is a straightforward, low-risk consistency change; frame it as hygiene/future-trim-readiness rather than a cold-start win, since the load path is lazy and editor-only.


<sub>effort M · risk low · confirmed (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>duplication</code> · Shell.Application COM plumbing and the shell:AppsFolder prefix are duplicated across three files — `src/Salvo.Core/Services/ProcessMatcherResolver.cs:167`</summary>


**Problem.** The COM-release helper is copy-pasted verbatim: ReleaseCom in ProcessMatcherResolver (lines 167-180) and in ShellInstalledAppsProvider (lines 157-170) are identical (Marshal.IsComObject + FinalReleaseComObject in a try/catch). The `Type.GetTypeFromProgID("Shell.Application")` + NameSpace("shell:AppsFolder") acquisition pattern is likewise duplicated (ProcessMatcherResolver:74-90, ShellInstalledAppsProvider:30-49). And the `shell:AppsFolder\` prefix literal is redeclared three times: ProcessMatcherResolver AppsFolderPrefix (line 11), KnownAppsDatabase prefix (line 68), and ShellInstalledAppsProvider AppsFolderPath/interpolations (lines 11, 86, 94). A change to how AppsFolder parse-names are built or how COM objects are released has to be made in several places.


**Fix.** Extract a small internal static helper (e.g. ShellAppsFolder in Salvo.Core) exposing: the prefix constant "shell:AppsFolder\\", the namespace name "shell:AppsFolder", and ReleaseCom(object?). Route KnownAppsDatabase, ProcessMatcherResolver, and ShellInstalledAppsProvider through the prefix/namespace constants and the shared ReleaseCom. Do NOT try to unify the full Shell-instance acquisition into one NameSpace/enumeration helper — the two consumers use it differently (single ParseName vs Items() loop), so a thin CreateShell() at most; the clear wins are the constants and ReleaseCom.


<sub>effort M · risk low · confirmed (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>magic-number</code> · Hardcoded process-wait timeouts despite a centralized Timeouts class — `src/Salvo.Core/Services/AppOrchestrator.cs:482`</summary>


**Problem.** The codebase centralizes durations in Timeouts (Services/Timeouts.cs), yet two process waits use bare literals: `process.WaitForExit(60_000)` in AppOrchestrator.RunCommand (line 482) and `process.WaitForExit(5_000)` in ProcessInspector.KillProcesses (line 178). These are exactly the policy knobs Timeouts exists to hold, and they are undiscoverable/untunable where they sit.


**Fix.** Add TimeSpan members to Timeouts (e.g. RunCommandExecution = TimeSpan.FromSeconds(60) and ProcessKillGrace = TimeSpan.FromSeconds(5)) to match the class's TimeSpan convention, then consume them at the call sites via the millisecond int overload: process.WaitForExit((int)Timeouts.RunCommandExecution.TotalMilliseconds) and process.WaitForExit((int)Timeouts.ProcessKillGrace.TotalMilliseconds).


<sub>effort S · risk low · confirmed (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>duplication</code> · Matcher-to-exeNames/aumids projection duplicated between ProcessInspector and KnownAppsDatabase — `src/Salvo.Core/Services/KnownAppsDatabase.cs:29`</summary>


**Problem.** The exact LINQ that projects an IReadOnlyList<ProcessMatcher> into a case-insensitive HashSet of exe names and a HashSet of AUMIDs appears twice: KnownAppsDatabase.FindMatch (lines 29-37) and ProcessInspector.CollectExeNames/CollectAumids (lines 122-132). Same Where/Select/ToHashSet(OrdinalIgnoreCase) in both.


**Fix.** Add extension methods (e.g. ProcessMatcherExtensions.ExeNames()/Aumids() on IReadOnlyList<ProcessMatcher>) in Salvo.Core.Models and call them from both places.


<sub>effort S · risk low · confirmed (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>testing</code> · Pure parsing/matching logic is untested — `src/Salvo.Core/Services/ProcessMatcherResolver.cs:144`</summary>


**Problem.** Several deterministic, edge-case-heavy helpers have no unit coverage (tests exist for Scoop provider, PathResolver, orchestrator graph, config store, but not these). ProcessMatcherResolver.ParseProcessStartArg (lines 144-165) parses --processStart out of a shortcut arg string with quote-stripping and whitespace scanning; AppOrchestrator.ParseDirect (lines 509-526) splits a command line honoring a leading quoted exe; KnownAppsDatabase.FindMatch (lines 27-64) does the exe/aumid/shellParseName matching. All are pure (no COM/FS/registry) and cheaply testable, and all are the kind of string logic where off-by-one/quote-handling regressions hide.


**Fix.** Prioritize unit tests for the two pure private parsers: change ParseProcessStartArg and ParseDirect from private to internal (InternalsVisibleTo=Salvo.Core.Tests is already set) and cover quoted/unquoted, empty, no-flag/no-match, trailing-args, and unbalanced-quote inputs. Drop or de-emphasize FindMatch: it is already public (no visibility change needed) but reads an embedded KnownApps.json via a private Lazy list, so tests would assert against the shipped DB rather than the matching algorithm in isolation — lower ROI unless the entry list is made injectable.


<sub>effort M · risk low · confirmed (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>dead-code</code> · Public LaunchApp(AppEntry, string?) overload is never called — `src/Salvo.Core/Services/AppOrchestrator.cs:67`</summary>


**Problem.** AppOrchestrator exposes two public launch overloads: LaunchApp(app) (line 61) and LaunchApp(app, groupId) (line 67). Only the single-arg form is on IAppOrchestrator and is the only one used in production (MainWindowViewModel.cs:784) and tests. The 2-arg overload — the only place groupId would flow in for a single-app launch — has no callers; group launches go through LaunchGroupAsync which calls LaunchAppCore directly. It is unused public surface that implies a capability (telemetry attribution by group for ad-hoc launches) that isn't wired up.


**Fix.** Either remove the 2-arg overload, or wire it up (have MainWindowViewModel pass the owning group id so single-app launches are attributed) and add it to IAppOrchestrator if it is meant to be part of the contract.


<sub>effort S · risk low · confirmed (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>consistency</code> · RunCommand does not expand env vars in WorkingDirectory and breaks on quotes in the command — `src/Salvo.Core/Services/AppOrchestrator.cs:468`</summary>


**Problem.** Two robustness gaps in RunCommand vs the rest of the code: (1) WorkingDirectory is passed through raw as `node.WorkingDirectory ?? string.Empty` (line 468) with no Environment.ExpandEnvironmentVariables and no existence check, whereas ProcessLauncher.ResolveWorkingDirectory (ProcessLauncher.cs:52-61) expands and validates — so a WaitNode/RunCommandNode with `%USERPROFILE%\...` silently fails to set the cwd. (2) For the shell/powershell interpreters the user command is string-interpolated into a quoted argument: `"-NoProfile -Command \"{node.Command}\""` and `"/c {node.Command}"` (lines 457-459); a command containing a double quote terminates the quoted region and mis-parses the rest.


**Fix.** Expand and validate WorkingDirectory to match ProcessLauncher.ResolveWorkingDirectory: if node.WorkingDirectory is non-empty, set expanded = Environment.ExpandEnvironmentVariables(node.WorkingDirectory) and only assign it when Directory.Exists(expanded); otherwise leave WorkingDirectory unset (empty) so the process inherits the parent cwd. Note that the current failure mode is a thrown Win32Exception (node fails), not a silent misconfiguration. For the quoting issue, since RunCommand is a documented power-user escape hatch, simply documenting the constraint (avoid unescaped double quotes in powershell commands) is proportionate; -EncodedCommand/temp-file is optional and not required at this severity.


<sub>effort S · risk low · confirmed (high)</sub>

</details>


### Elevator  (5)

<details>
<summary><b>[medium]</b> <code>testing</code> · No test coverage for payload parsing, action dispatch, or the privileged registry-delete mapping — `src/Salvo.Elevator/Program.cs:120`</summary>


**Problem.** A search of tests/ (Salvo.Core.Tests, Salvo.App.Tests) finds no references to the elevator, RegistryRunValueWriter, ElevationRequest round-tripping, or WindowsServiceController. This is privileged, security-sensitive code that mutates HKLM/HKCU Run keys and controls services, and the delete-path source->hive/approved-key mapping (Program.cs:100-107) is hand-maintained and duplicated (see the duplication finding), which is exactly the kind of table that regresses silently. ParsePayload (Program.cs:120-144) also has non-obvious edge behavior (only the first --payload honored; missing value, non-base64, and malformed JSON must map to a null request) that is untested.


**Fix.** Keep the proposed ParsePayload cases (valid, missing --payload, --payload with no value, non-base64, invalid JSON, JSON 'null', unknown Action) but note ParsePayload must be made internal with [InternalsVisibleTo] to be unit-testable. For the source->location mapping, a cheap first step needs no refactor: the existing public RegistryRunValueWriter.FormatKeyPath(source) already exercises ResolveLocation and can pin the hive/view wiring for all four StartupEntrySource values today; the approved-key path and the duplicated elevator delete mapping should be pinned after centralizing them (the separate duplication finding) so a single table drives both write and delete. Do not attempt real registry mutation in tests — restrict to the pure parse/dispatch/mapping logic.


<sub>effort M · risk low · confirmed (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>duplication</code> · Registry-delete path logic hand-rolled in the elevator instead of centralized like the write path — `src/Salvo.Elevator/Program.cs:94`</summary>


**Problem.** DeleteRunValue (Program.cs:94-118) re-declares the Run/StartupApproved path constants (lines 96-98) and re-implements the StartupEntrySource -> (root, path, approvedRoot, approvedPath) mapping (lines 100-107). This is a near-copy of RegistryRunValueWriter (constants at RegistryRunValueWriter.cs:13,15,16; mapping in ResolveLocation at 158-173). The write action is deliberately routed through the shared RegistryRunValueWriter.Write (RunRegistryWrite, Program.cs:72) and that class's own header comment states its purpose is 'Keeping the rename + approval-table re-keying in one place prevents the two paths from drifting apart.' The delete path violates exactly that intent: there is no RegistryRunValueWriter.Delete, so the elevator owns a second, independent copy of the hive/view/approved-key mapping. A future change to the approved-table scheme (e.g. HKLM 32-bit approval location) must now be made in three places (writer, this delete, and WindowsStartupService), and any miss silently leaves stale entries.


**Fix.** Add a public static RegistryRunValueWriter.Delete(StartupEntrySource source, string name) that calls the existing private ResolveLocation and, when non-null, deletes name from RunRoot/RunPath and ApprovedRoot/ApprovedPath (throwOnMissingValue: false), returning early when ResolveLocation is null — mirroring the current 'if (root is null) return;' guard. Call it from RunRegistryDelete, deleting the duplicated constants and switch in Program.cs. Optional but worth noting in the same pass: WindowsStartupService also keeps its own duplicated constants and ResolveApprovedLocation/RemoveFromApproved mapping, so if the goal is genuine single-source for the approved-table scheme, that class should route through the same helper too; otherwise the 'one place' intent remains only partially realized.


<sub>effort M · risk low · confirmed (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>error-handling</code> · Elevated helper has zero diagnostics; every failure is swallowed and invisible — `src/Salvo.Elevator/Program.cs:34`</summary>


**Problem.** The elevator runs elevated in a hidden, no-window process (launched with WindowStyle.Hidden + CreateNoWindow, ElevationClient.cs:66-68) yet emits no log, event-log entry, or stderr on any failure. Main catches all exceptions and returns 3 with no detail (Program.cs:34-37); RunRegistryDelete swallows all exceptions returning 1 (88-91); RunServiceAction discards the per-service failure message via `out _` (53-54) even though WindowsServiceController produces useful text like 'Needs admin' / 'Start timed out' (WindowsServiceController.cs:47,57). The parent doesn't recover any of this either: WaitAsync only returns `process.ExitCode == 0` (ElevationClient.cs:93) without logging the code, and the group call site ignores even that boolean (MainWindowViewModel.cs:1451-1455). Net result: when an elevated service start or registry write fails, there is no artifact anywhere explaining why. For privileged, security-sensitive code this is a real supportability gap.


**Fix.** Prioritize the two lowest-risk, highest-value changes: (1) in ElevationClient.WaitAsync, log the non-zero ExitCode via the existing _logger so failures are traceable from the parent without touching the elevated process; (2) in Program.cs, capture the per-service message (replace `out _`) and in the Main catch, and append failures to a plain text log file under %LOCALAPPDATA% (e.g. via File.AppendAllText). Prefer a log file over EventLog — registering an EventLog source is itself a privileged registry op that adds failure surface. Optionally surface the returned bool at the MainWindowViewModel call site to inform the user the elevated action failed.


<sub>effort M · risk low · confirmed (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>dead-code</code> · Differentiated exit codes (1/2/3) are magic literals that no consumer ever distinguishes — `src/Salvo.Elevator/Program.cs:26`</summary>


**Problem.** Program.cs returns four distinct bare-integer exit codes -- 0 success, 1 operation failed (62,73,90), 2 bad/rejected input (23,31,68,80), 3 unexpected exception (36) -- with no named constants and no documentation of their meaning. But the sole consumer, ElevationClient.WaitAsync, only evaluates `process.ExitCode == 0` (ElevationClient.cs:93), so 1, 2, and 3 all collapse to the same 'false'. The careful differentiation conveys nothing to anyone and misleads a maintainer into thinking these codes are a contract. Combined with the absence of logging (see the diagnostics finding), the distinction is pure dead differentiation.


**Fix.** Do NOT collapse to 0/non-zero — that discards useful diagnostic signal for manual runs and telemetry. Treat this as low-priority polish: add a short comment (or XML doc) in Program.cs documenting what 1/2/3 mean. Only introduce a shared ElevatorExitCode enum in Salvo.Core if you simultaneously make ElevationClient log/branch on the specific code (e.g. distinguish user-cancelled/bad-input from operation-failed in the warning); an enum used by only one side would be ceremony without payoff.


<sub>effort S · risk low · needs-nuance (medium)</sub>

</details>

<details>
<summary><b>[low]</b> <code>correctness</code> · Service timeout is applied per-service, so worst-case elevated runtime scales with service count and is otherwise unbounded — `src/Salvo.Elevator/Program.cs:50`</summary>


**Problem.** RunServiceAction loops over every service and passes the full 25s Timeouts.ElevatorServiceOperation (Program.cs:13, Timeouts.cs:7) to each TryStart/TryStop (Program.cs:50-60), each of which blocks on WaitForStatus for that full window (WindowsServiceController.cs:41,74). A request with N slow/hung services can therefore block the elevated process for up to N x 25s. There is no overall deadline: the group call site invokes InvokeAsync with no CancellationToken and ignores the result (MainWindowViewModel.cs:1451-1455), and InvokeAsync/WaitAsync impose no timeout of their own (ElevationClient.cs:79,88-93). So for a group with several unresponsive services the hidden elevated helper can run for minutes with the UI awaiting it, with no upper bound and no way to cancel.


**Fix.** Bound the whole elevated request with a single overall deadline enforced via a CancellationToken plumbed from the caller (MainWindowViewModel → InvokeAsync → the elevator process, e.g. as a payload field or by killing the helper on cancel), rather than granting a fresh 25s per service. Prefer an overall cancellation-based deadline that aborts the remaining batch over a per-service decremented budget, since dividing a shared time budget can starve later services and cause spurious WaitForStatus timeouts even for fast-starting services. Treating the discarded InvokeAsync bool result (report failure to the user) is a worthwhile adjacent fix. Only parallelize service start/stop if you have confirmed there are no start-ordering dependencies among the elevated services; otherwise keep it sequential and rely on the overall deadline. Given the impact requires multiple genuinely-hung services and the UI is not actually blocked, treat this as a low-severity robustness improvement.


<sub>effort M · risk medium · needs-nuance (high)</sub>

</details>


### Installer (RETIRING)  (7)

<details>
<summary><b>[low]</b> <code>dead-code</code> · Entire StartupGroups.Installer.UI project is superseded and safe to delete — `src/StartupGroups.Installer.UI/StartupGroups.Installer.UI.csproj`</summary>


**Problem.** This project is the WiX/Burn managed Bootstrapper Application (InstallerBootstrapperApplication + 6 WPF views) for the Velopack/Burn installer that the MSIX migration retires in Phase 3. MSIX has already landed as the primary path: installer/Msix/build.ps1 runs in ci.yml:63 and release.yml, MsixUpdateService.cs and installer/Msix/Package.appxmanifest exist, and Windows PackageManager now owns download/verify/install/uninstall. The ci.yml comment (lines 128-132) states the Burn-bundle wrapper that packaged this BA 'was removed during the StartupGroups -> Salvo rename; canary now ships the lean Velopack Setup.exe directly.' No GitHub workflow builds or ships the BA exe, and no product project (Salvo.App/Salvo.Core/Salvo.Elevator) references it. It survives only via Salvo.slnx and one unit test. This is orphaned dead code.


**Fix.** As part of Phase 3 MSIX cleanup, retire the entire retired Burn installer chain together — not just Installer.UI. Delete src/StartupGroups.Installer.UI/, installer/StartupGroups.Bundle/, and installer/StartupGroups.Installer/, remove the Salvo.slnx entry, remove the ProjectReference from tests/Salvo.App.Tests, and also delete tests/Salvo.App.Tests/MsiMessageFilterTests.cs (otherwise the test project fails to compile). Confirm nothing else consumes the Burn payloads first. Treat this as a low-severity maintenance cleanup, not a high-severity issue.


<sub>effort M · risk low · needs-nuance (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>build-config</code> · Retired installer project still in the solution, so every CI build compiles dead WiX/WPF code — `Salvo.slnx:5`</summary>


**Problem.** Salvo.slnx line 5 lists src/StartupGroups.Installer.UI. CI restores, builds, and tests the whole solution (ci.yml:42-48: dotnet restore/build/test Salvo.slnx). That drags WixToolset.BootstrapperApplicationApi and a self-contained win-x64 WPF publish (SelfContained=true, RuntimeIdentifier=win-x64 in the csproj) into every PR and main build, purely to compile a project nothing ships. This is wasted restore/build time and NuGet surface on a project slated for deletion.


**Fix.** Recommendation is sound: remove the <Project Path="src/StartupGroups.Installer.UI/StartupGroups.Installer.UI.csproj" /> entry from Salvo.slnx (line 6, not line 5 as cited), ideally alongside deleting the retired project directory, to drop the WiX dependency and self-contained win-x64 build from CI. No downstream references exist, so this is safe. Treat as low-priority build hygiene, not a high-severity issue.


<sub>effort S · risk low · confirmed (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>testing</code> · Test project pins the retired installer into the build graph for a single pure helper — `tests/Salvo.App.Tests/Salvo.App.Tests.csproj:17`</summary>


**Problem.** Salvo.App.Tests.csproj:17 holds a ProjectReference to the entire StartupGroups.Installer.UI project solely so MsiMessageFilterTests.cs can call the static MsiMessageFilter.LooksLikeRawGuid. This is the only consumer of the whole project besides the solution listing, and it is the thing that will break the build the moment the project is deleted. The filter itself is also dead once the BA goes: its only caller is OnExecuteMsiMessage (InstallerBootstrapperApplication.cs:756), and MSIX installs raise no MSI ExecuteMsiMessage/ActionStart events, so there is nothing to filter under the new pipeline.


**Fix.** Keep the recommendation as-is but scope it as a cleanup checklist item for the installer retirement, not a standing defect: during Phase 3 of the MSIX migration (which docs/MIGRATION_PLAN.md already lists), delete tests/Salvo.App.Tests/MsiMessageFilterTests.cs and remove the ProjectReference on csproj line 17 alongside deleting src/StartupGroups.Installer.UI/. Do not port MsiMessageFilter to the app — it has no caller under MSIX. No action is needed while the Burn BA still ships.


<sub>effort S · risk low · needs-nuance (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>dead-code</code> · Installer launches/kills StartupGroups.exe, but the app now builds Salvo.exe — `src/StartupGroups.Installer.UI/InstallerBootstrapperApplication.cs:345`</summary>


**Problem.** ResolveInstalledAppPath (lines 345-348) probes only for 'StartupGroups.exe', and StopRunningInstances (line 565) calls Process.GetProcessesByName("StartupGroups"). After the Salvo rename, Salvo.App.csproj sets <AssemblyName>Salvo</AssemblyName>, so the shipped executable is Salvo.exe. Even if this BA still ran, it would never find, stop, or relaunch the running app. This confirms the project is already non-functional against the current app, not merely deprecated — reinforcing delete-rather-than-fix.


**Fix.** Keep the observation but fold it into the primary "remove StartupGroups.Installer.UI project" finding as supporting evidence rather than a separate medium item. If for some reason the Burn/Velopack installer must remain live during the MSIX transition, then the correct fix is to update both the exe candidate paths and GetProcessesByName to "Salvo" (and the %LocalAppData% folder name), since a name mismatch there would silently skip closing/relaunching the running app during updates. Otherwise, delete the project and no rewiring is warranted.


<sub>effort S · risk low · confirmed (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>dead-code</code> · First-run settings seed and user-data cleanup target the pre-rename StartupGroups AppData folders — `src/StartupGroups.Installer.UI/InstallerBootstrapperApplication.cs:700`</summary>


**Problem.** TrySeedFirstRunSettings writes %AppData%\StartupGroups\settings.json (lines 700-701) and TryDeleteUserDataFolders targets %AppData%\StartupGroups plus %LocalAppData%\StartupGroups.UserData (lines 662-673). The renamed app now uses AppPaths.AppFolderName = "Salvo" and LocalDataFolderName = "Salvo.UserData" (src/Salvo.Core/Services/AppPaths.cs:5,7). So the seed writes a config the app never reads and the uninstall cleanup deletes folders that no longer exist. More stale, no-op logic confirming the project is superseded.


**Fix.** Recommendation is sound: remove this logic when the Burn BA project is retired. Worth noting for the record that it is triply stale — wrong folder (StartupGroups vs Salvo), wrong filename (settings.json vs AppPaths.ConfigFileName "config.json"), and a retired field (updateChannel, removed in 36461be) — so even a quick "just fix the paths" patch would still write an unread file. If, however, the Burn installer must ship in the interim before MSIX lands, the uninstall cleanup is a genuine (if low-impact) data-cleanup bug: fix the two folder paths to AppPaths.UserDataFolder / LocalDataFolder so the opt-in "delete my config" actually deletes Salvo data.


<sub>effort S · risk low · confirmed (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>dead-code</code> · Channel-picker UI contradicts the locked 'drop the canary channel' decision — `src/StartupGroups.Installer.UI/ViewModels/CustomizeViewModel.cs:9`</summary>


**Problem.** CustomizeViewModel exposes an InstallerUpdateChannel enum (lines 9-13) and a Stable/Canary picker (lines 32-38); InstallerBootstrapperApplication.ApplyDefaultChannelFromBundle (lines 490-511) pre-selects it from a Burn bundle variable and TrySeedFirstRunSettings (lines 715-722) writes updateChannel into settings.json. The MSIX migration decision explicitly drops the canary channel concept and MSIX builds are single-track (MsixUpdateService docstring: 'MSIX builds are single-track'), and the recent commit 36461be 'retire update-channel picker' removed the equivalent picker from the app. The installer's channel UI, bundle-variable plumbing, and channel seeding are dead surface.


**Fix.** Do not raise or act on this as a standalone dead-code finding. It is not dead today: the installer feeds settings.json updateChannel into the still-active VelopackUpdateService path used by all non-MSIX installs (App.xaml.cs CreateUpdateService keeps Velopack "alive while the MSIX migration phases through"). Removing the picker piecemeal would break channel selection for Velopack installs, especially now that the in-app settings dropdown is already gone. The correct disposition is to retire it as a unit with the entire StartupGroups.Installer.UI Burn project during the migration's already-planned Phase 3 legacy cleanup — which the MSIX plan already tracks. Not worth a code-review flag.


<sub>effort S · risk low · needs-nuance (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>consistency</code> · Installer chrome still branded 'Startup Groups' post-rename (do not rebrand — delete) — `src/StartupGroups.Installer.UI/ViewModels/LicenseViewModel.cs:17`</summary>


**Problem.** The installer surface still carries pre-rename branding: LicenseViewModel copyright '(c) 2026 Startup Groups' (line 17), InstallerWindow.xaml title 'Startup Groups Installer' (lines 7 and 43), and CustomizeView.xaml body copy 'Pick how Startup Groups updates...' (line 17). This is inconsistent with the Salvo rename but is not worth fixing given the project is being retired — it is additional evidence the whole surface is orphaned rather than maintained.


**Fix.** Do not invest in rebranding; fold into the project deletion. MSIX chrome/branding lives in installer/Msix/Package.appxmanifest instead.


<sub>effort S · risk low · confirmed (high)</sub>

</details>


### Tests  (9)

<details>
<summary><b>[medium]</b> <code>testing</code> · Startup writer (RegistryRunValueWriter + WindowsStartupService bit logic) has zero test coverage — `src/Salvo.Core/WindowsStartup/RegistryRunValueWriter.cs:18`</summary>


**Problem.** A grep across tests/ shows no reference to RegistryRunValueWriter or WindowsStartupService, yet this is user-facing startup-entry editing and the audit's named focus. Two parts are pure and unit-testable without touching the live registry but are untested: (1) WindowsStartupService.IsApproved (lines 275-291) and BuildApprovedValue (lines 293-300) encode a subtle Windows convention (byte[0]=0x02 enabled / 0x03 disabled, low-bit = disabled, 12-byte blob with a FILETIME at offset 4) — exactly the kind of bit-twiddling that silently breaks; (2) RegistryRunValueWriter.Write's validation gates (empty OriginalName/NewName/Command, rename collision detection at line 54, and the 'original value no longer exists' guard at line 59) are ordinary branch logic reachable with a redirected test hive or by refactoring the validation out. None of these edge cases are pinned.


**Fix.** Make IsApproved and BuildApprovedValue testable — either mark them internal on WindowsStartupService with InternalsVisibleTo(Salvo.Core.Tests), or (cleaner) hoist the approval-byte encode/decode into RegistryRunValueWriter, which already exists to centralize the shared write path. Add round-trip tests: 0x02->enabled, 0x03->disabled, low-bit masking, empty/zero-length blob defaults to enabled, and 12-byte blob length with FILETIME at offset 4. For RegistryRunValueWriter.Write, cover the pure input-validation gates directly (empty OriginalName/NewName/Command -> Failed) and, using a per-test throwaway HKCU subkey created under a GUID path and deleted in Dispose, cover the rename + approved re-key happy path, the rename-collision failure (line 54), and the missing-original failure (line 59). Treat this as a medium-priority coverage improvement, not a high — the code reads correct today; the value is pinning the subtle convention against future regressions.


<sub>effort M · risk low · confirmed (high)</sub>

</details>

<details>
<summary><b>[medium]</b> <code>duplication</code> · Temp-directory scaffolding copy-pasted across 7+ test classes — `tests/Salvo.Core.Tests/AppOrchestratorTests.cs:221`</summary>


**Problem.** The 'create Path.Combine(GetTempPath(), "sg-..."+Guid) in ctor / best-effort recursive Delete in Dispose / swallow exceptions' pattern is reimplemented in at least seven places: AppOrchestratorTests (TempDir nested class, 221-245), GraphOrchestratorTests (TempDir, 234-248), PathResolverTests (10-14, 79-92), JsonConfigStoreTests (10-14, 82-95), SqliteLaunchBenchmarkStoreTests (10-15, 172-186), DependencyHintsAnalyzerTests (10-16, 179-192), ScoopInstalledAppsProviderTests (10-15, 17-20), and LaunchTelemetryServicePidResolverTests (14-20, 74-82). Three of them additionally must remember to call SqliteConnection.ClearAllPools() in Dispose (SqliteLaunchBenchmarkStoreTests:176, DependencyHintsAnalyzerTests:183, LaunchTelemetryServicePidResolverTests:78) — a foot-gun that is easy to forget in a new Sqlite-touching test and would leak file handles / fail cleanup on Windows.


**Fix.** Recommendation is sound as written. Concretely: add a shared test-support file (e.g. tests/Salvo.Core.Tests/TestSupport/TempDirectory.cs) with a sealed IDisposable TempDirectory exposing Root/CreateFile and doing the guarded recursive delete, plus a SqliteTempDirectory (or a base fixture) whose Dispose calls SqliteConnection.ClearAllPools() before deleting. Migrate all 8 classes to it; delete the two nested TempDir types. This removes ~50 lines and makes the ClearAllPools ceremony impossible to forget for future Sqlite-touching tests. Severity sits at the low end of medium — test-only maintainability with no current failure — but the ClearAllPools omission hazard on Windows CI keeps it above 'low'.


<sub>effort M · risk low · confirmed (high)</sub>

</details>

<details>
<summary><b>[medium]</b> <code>testing</code> · ReadinessDetector early-exit and probe-fault paths are untested — `tests/Salvo.Core.Tests/ReadinessDetectorTests.cs:7`</summary>


**Problem.** ReadinessDetectorTests covers fastest-probe-wins, timeout, no-applicable-probes, and cancel-losers, but never exercises two real verdicts. (1) The early-exit watcher WatchEarlyExitAsync -> LaunchOutcome.ExitedEarly (ReadinessDetector.cs lines 100-120, consumed at 60-65) is a distinct outcome that feeds telemetry/benchmarking and is completely uncovered. (2) The probe-throws branch in WrapProbeAsync (lines 93-96, which logs a warning and returns Unknown rather than propagating) is also uncovered. A regression that stopped detecting early process-tree death, or that let a probe exception escape, would pass the current suite.


**Fix.** Two tests. Early-exit: reuse MakeContext's process-less session (IsTreeAlive() is already false), supply a non-firing or slow probe, and call DetectAsync with a timeout > EarlyExitGrace (e.g. TimeSpan.FromSeconds(3)); assert Outcome == ExitedEarly and Signal == EarlyExit (test runs ~1s due to the grace period). Probe-throws: add a FakeProbe variant whose RunAsync throws a general exception, and use a sub-grace timeout (e.g. 250ms, matching the existing timeout test) so the early-exit watcher is cancelled inside its grace delay; assert Outcome == TimedOut and that DetectAsync completes without surfacing the exception. Note: do NOT use a long timeout for the throw test — the dead-tree watcher would then win and return ExitedEarly rather than TimedOut.


<sub>effort M · risk low · confirmed (high)</sub>

</details>

<details>
<summary><b>[medium]</b> <code>testing</code> · IfElse else-branch, non-FileExists conditions, and group cancellation are untested — `tests/Salvo.Core.Tests/GraphOrchestratorTests.cs:49`</summary>


**Problem.** The only IfElse test drives the condition true via an existing file (FileExistsCondition), so the else-taken path — condition false, then-branch skipped, else fires, downstream merge still runs — is never exercised, and the symmetric skip propagation on the then side is untested. The other two condition types the orchestrator evaluates (ServiceRunningCondition and ProcessRunningCondition in AppOrchestrator.EvaluateCondition, lines 528-539) have no evaluation test at all (FlowBranchRoundTripTests only round-trips ServiceRunningCondition, never evaluates it). Separately, ExecuteGraphAsync threads a CancellationToken with ThrowIfCancellationRequested and AppNode obs.WaitAsync(token) (lines 287, 343), but no test cancels a LaunchGroupAsync mid-flight — cancellation of a readiness-gated group launch is entirely uncovered.


**Fix.** Keep the else-branch test as proposed (use a FileExistsCondition pointing at a non-existent path, assert the else app launches, the then app is absent from results, and the merge app still runs). For the cancellation gap, prefer a deterministic pre-cancelled-token test — LaunchGroupAsync(group, new CancellationToken(canceled: true)) — asserting OperationCanceledException propagates and launcher.CallCount == 0 (this reliably trips the guard at line 287). Only attempt the harder mid-flight/blocking-observation variant if the fakes are first extended to expose a controllable/blocking readiness observation, since the current FakeInspector/FakeLauncher return synchronously and won't reliably block obs.WaitAsync. Optionally add a direct EvaluateCondition-through-orchestrator test for ServiceRunningCondition using the existing FakeServices.States to toggle Running/Stopped.


<sub>effort M · risk low · confirmed (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>testing</code> · Graph action-node execution (ServiceStart/ServiceStop/RunCommand/Wait) is claimed but never tested — `tests/Salvo.Core.Tests/GraphOrchestratorTests.cs:8`</summary>


**Problem.** The class docstring (lines 7-11) states it "Locks the new code paths added in Phase A2: action nodes (ServiceStart/ServiceStop/RunCommand), IfElse skip propagation ... and GroupCall recursion." But the four test methods only exercise parallel fan-out, an IfElse *then*-branch, and GroupCall resolve/recursion. There is NO test that constructs a ServiceStartNode, ServiceStopNode, RunCommandNode, or even executes a WaitNode. Those are substantial, distinct production paths: AppOrchestrator.cs lines 370-394 (the three action-node cases) and lines 446-526 (RunCommand + the hand-rolled ParseDirect quoting parser, which has real edge cases around quoted exe paths). The docstring gives false confidence that these are locked when they are entirely uncovered. RunCommand's ParseDirect in particular is pure and trivially unit-testable yet has zero tests.


**Fix.** Either add the missing graph tests or trim the docstring to match reality. For tests: (1) execute a graph containing a ServiceStartNode and ServiceStopNode and assert the results are recorded with the expected OperationStatus — note the existing FakeServices.TryStart/TryStop always return true and do not track invocations, so extend it with call-tracking to assert the controller was actually hit; (2) execute a RunCommandNode with Interpreter="direct" running a benign command and assert the exit-code -> OperationStatus mapping (Exit 0 -> Success). For ParseDirect (currently private static): cover quoted-path, unterminated-quote, bare-exe, and no-arg forms either by promoting it to internal with InternalsVisibleTo, or by driving equivalent commands through RunCommand. A WaitNode fan-in test is optional and lower value since the barrier is the generic engine mechanism, not WaitNode-specific.


<sub>effort M · risk low · confirmed (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>duplication</code> · FakeServices/FakeInspector/FakeLauncher duplicated verbatim across two orchestrator test files — `tests/Salvo.Core.Tests/GraphOrchestratorTests.cs:173`</summary>


**Problem.** GraphOrchestratorTests (lines 175-232) and AppOrchestratorTests (lines 139-219) each define their own FakeServices, FakeInspector, and FakeLauncher with the same interfaces and near-identical bodies, plus an identical BuildOrchestrator helper. The GraphOrchestratorTests copy even carries a comment admitting it: "Reuse the fakes from AppOrchestratorTests — same shape, kept here as nested types to avoid coupling." The two copies have already drifted (GraphOrchestrator's FakeLauncher dropped the LastCall property; its FakeServices dropped the StartResult/StopResult dictionaries), so they are not really the same and will keep diverging. This is exactly the duplicated setup that wants a shared fixture.


**Fix.** Extract the three fakes + BuildOrchestrator into one internal file (e.g. OrchestratorTestFakes.cs) consumed by both classes. When merging, adopt the superset: keep FakeLauncher.LastCall and FakeServices.StartResult/StopResult, and unify the status-map property under a single name (App's 'Status' vs Graph's 'States'), updating the ~2 call sites accordingly. This reconciliation is trivial but must be done deliberately so neither test loses a capability it relies on.


<sub>effort S · risk low · confirmed (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>testing</code> · No-shared-resources test asserts nothing when the analyzer drops the group — `tests/Salvo.Core.Tests/DependencyHintsAnalyzerTests.cs:114`</summary>


**Problem.** Analyze_ProducesNoHint_WhenNoSharedResources wraps its assertions in `if (hints.Count > 0) { ... }` (lines 114-118). For the seeded input the analyzer deterministically returns exactly one hint with empty Edges (AnalyzeAsync adds a hint whenever latestOrder.Count > 0, regardless of edges — see DependencyHintsAnalyzer.cs lines 40-49), so the guard is not needed; worse, it means that if a future regression made the analyzer silently drop the group entirely (return 0 hints), this test would still pass green without asserting the intended 'no inferred edge' contract. The test cannot fail in the branch that matters.


**Fix.** Recommendation is correct as written. Replace the conditional with unconditional assertions matching the deterministic outcome: hints.Should().ContainSingle(); hints[0].Edges.Should().BeEmpty(); hints[0].IsReorderSuggested.Should().BeFalse(). Consider also renaming the test (e.g. Analyze_ProducesHintWithNoEdges_WhenNoSharedResources) since the analyzer does emit a hint, just without inferred edges.


<sub>effort S · risk low · confirmed (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>testing</code> · Near-vacuous outcome assertion in BeginObservation no-inspector test — `tests/Salvo.App.Tests/../Salvo.Core.Tests/LaunchTelemetryServicePidResolverTests.cs:67`</summary>


**Problem.** BeginObservation_WithNullProcess_NoInspector_StillObserves asserts metrics.Outcome.Should().BeOneOf(Ready, TimedOut, ExitedEarly, Unknown) (lines 67-71). LaunchOutcome has six members (Unknown, Ready, TimedOut, ExitedEarly, PidNotFound, Failed), so this accepts four of six and, given the shell-path/no-inspector/no-process setup, is effectively non-deterministic — the test can practically never fail on the property it claims to check, so it pins no behavior. It also spends a real 2-second wall-clock timeout (line 61) to reach that non-assertion.


**Fix.** Keep the timing-robust BeOneOf approach rather than pinning a single outcome (the author's hedge against CI load is sound: ExitedEarly at ~1s is normal, but TimedOut/Unknown are possible under starvation or store failure). The only defensible tightening is to drop LaunchOutcome.Ready from the accepted set — it is unreachable here because RootPid can never become non-null without an inspector, so ImmediateReadyProbe never fires Ready. Narrow to BeOneOf(ExitedEarly, TimedOut, Unknown). Do NOT introduce a short timeout or pin Be(ExitedEarly): the outcome is already deterministic in isolation but the assertion breadth is intentional flakiness insurance, and the ~1s cost is the fixed EarlyExitGrace, not a wasted 2s. Note the test's real value is already the RootPid==null and non-throwing-completion assertions, so this is low-priority polish, not a correctness gap.


<sub>effort S · risk low · needs-nuance (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>testing</code> · LaunchGroupAsync_RunsEachApp_InOrder tests no ordering and the graph runs them in parallel — `tests/Salvo.Core.Tests/AppOrchestratorTests.cs:99`</summary>


**Problem.** The test name promises ordering, but the group has two apps with default DelayAfterSeconds, which FlowMigration.Migrate turns into a single wave that fans both out from Start in parallel (confirmed by FlowMigrationTests.Migrate_TwoAppsInOneWave_FanOutFromStart). The body only asserts HaveCount(2), OnlyContain(Succeeded), and CallCount==2 (lines 121-123) — nothing about order, and order isn't even guaranteed for that topology. The name misleads readers into thinking sequential wave ordering is covered when it is not.


**Fix.** Recommendation stands. Preferably do both: (1) rename the existing test to reflect what it actually verifies (e.g. LaunchGroupAsync_RunsAllApps_InSingleParallelWave), and (2) add a distinct ordering test that sets DelayAfterSeconds>0 on the first app so Migrate inserts a WaitNode barrier, then capture launch order in FakeLauncher and assert the second wave's app is launched strictly after the first. Note the fix is test-only with no production impact, hence low severity rather than medium.


<sub>effort S · risk low · confirmed (high)</sub>

</details>


### xc:build+analyzers+editorconfig  (5)

<details>
<summary><b>[low]</b> <code>build-config</code> · EnforceCodeStyleInBuild + TreatWarningsAsErrors are on, but there is no .editorconfig — style enforcement is largely a no-op — `Directory.Build.props:8`</summary>


**Problem.** Directory.Build.props sets EnforceCodeStyleInBuild=true (line 8) and TreatWarningsAsErrors=true (line 6), which signals the team wants code style enforced at build time and treated as errors. But there is no .editorconfig anywhere in the repo (confirmed by glob + find). EnforceCodeStyleInBuild only runs the IDExxxx code-style analyzers; without an .editorconfig raising their severity, virtually all of those rules stay at their default of 'silent'/'suggestion' and never emit a warning, so they never fail the build. The net effect is that the two flags give a false sense of enforcement: naming conventions, using-directive placement, file-scoped-namespace preference, var usage, expression-body preferences, unused usings, etc. are effectively unenforced. Style is only whatever the C# compiler itself defaults to.


**Fix.** Downgrade to a low-priority, optional note. Accurate framing: TreatWarningsAsErrors + AnalysisLevel=latest already enforce compiler warnings and CA code-quality analyzers (build-failing), so enforcement is real; what EnforceCodeStyleInBuild=true does NOT currently do, absent an .editorconfig, is enforce cosmetic IDExxxx style rules. If — and only if — the team wants formatting/style enforced, add a root .editorconfig, and note that whichever rules they promote to warning should be adopted incrementally (start as suggestion, fix violations, then bump to warning) so TreatWarningsAsErrors doesn't break the build. Also note IDE0005 needs GenerateDocumentationFile=true to fire at build time. This is a preference decision, not a bug; it is fine to leave as-is.


<sub>effort M · risk medium · needs-nuance (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>build-config</code> · TargetFramework is duplicated across all 6 projects with two inconsistent Windows OS-version pins, and no central definition — `src/Salvo.Core/Salvo.Core.csproj:3`</summary>


**Problem.** Each project declares its own TargetFramework and they disagree on the Windows platform version. Salvo.Core (line 3), Salvo.Elevator (line 4), and Salvo.Core.Tests (line 3) target bare 'net10.0-windows', which implicitly resolves to Windows platform version 7.0. Salvo.App, StartupGroups.Installer.UI, and Salvo.App.Tests target 'net10.0-windows10.0.19041.0'. Only Salvo.App/Installer.UI declare a SupportedOSPlatformVersion floor (10.0.17763.0); Core/Elevator declare none, so there is no single source of truth for the app's minimum OS. Because Core is pinned (implicitly) to platform 7.0 while the app is 19041, a developer who later calls a modern Windows API from Core will hit a confusing CA1416 platform-compat error (a hard build error here due to TreatWarningsAsErrors) that would not fire in Salvo.App. The strings are also copy-pasted six times, so bumping the TFM or SDK version is a six-file edit.


**Fix.** Keep the UI projects on 19041 (deliberate WinRT surface). In Directory.Build.props add a shared default <TargetFramework>net10.0-windows</TargetFramework> for Core/Elevator/Core.Tests to inherit while the WPF projects override, and — the substantive part — explicitly set an intended Windows platform version plus a single SupportedOSPlatformVersion floor (matching the product's Win10 1809 / MSIX minimum) rather than letting Core silently default to 7.0. Treat as low-priority consistency cleanup, not a medium-severity issue, since the build is currently green and the CA1416 risk is latent.


<sub>effort S · risk low · needs-nuance (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>consistency</code> · StartupGroups.Installer.UI still carries the pre-rename name (project, folder, RootNamespace, AssemblyName) while everything else is Salvo.* — `src/StartupGroups.Installer.UI/StartupGroups.Installer.UI.csproj:7`</summary>


**Problem.** The StartupGroups → Salvo rename is complete for every project except this one: the folder is src/StartupGroups.Installer.UI, the project file is StartupGroups.Installer.UI.csproj, RootNamespace=StartupGroups.Installer.UI (line 7), AssemblyName=StartupGroups.Installer.UI (line 8), and StartupObject=StartupGroups.Installer.UI.Program (line 15). It is listed under /src/ in Salvo.slnx (line 6) and referenced by tests/Salvo.App.Tests (line 17) using the old name. This leaves two naming schemes in one solution, which is confusing and error-prone for anyone navigating the codebase or wiring installer paths. (Note this component is the Burn out-of-process BA that the tracked MSIX migration is retiring, so weigh the rename against imminent deletion.)


**Fix.** Do not rename this project. It is a cosmetic consistency issue with no functional impact, and the tracked MSIX migration already lists src/StartupGroups.Installer.UI/ (and the two installer/StartupGroups.* directories) for deletion in Phase 3 cleanup. Investing M-effort in a folder/csproj/namespace/XAML/slnx/test/build.ps1 rename of a component slated for removal is wasted churn. Leave the name as-is and let Phase 3 delete it; if the migration slips indefinitely, revisit and rename then. No action needed now.


<sub>effort M · risk medium · needs-nuance (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>build-config</code> · Copyright embeds build-time UtcNow.Year, undermining the Deterministic=true build — `Directory.Build.props:12`</summary>


**Problem.** Directory.Build.props sets <Deterministic>true</Deterministic> (line 9) — the intent being byte-reproducible output for identical source — but Copyright is defined as 'Copyright (c) $([System.DateTime]::UtcNow.Year)' (line 12). That expression is evaluated at build time, so the AssemblyCopyrightAttribute (and thus the produced binary) changes based on the wall-clock year: the same commit built in December 2026 vs January 2027 yields different assemblies. This is a (small, once-a-year) violation of the reproducibility the Deterministic flag advertises. Relatedly, the CI/Release workflows do not pass ContinuousIntegrationBuild=true, which is the companion flag that normalizes embedded source paths for reproducible/CI builds.


**Fix.** Replace the clock-derived year with an input-derived copyright string — a fixed start year or range, e.g. 'Copyright (c) 2025 Salvo' — so output is source-determined. Add ContinuousIntegrationBuild=true for CI builds; prefer a conditional in Directory.Build.props (<ContinuousIntegrationBuild Condition="'$(GITHUB_ACTIONS)'=='true'">true</ContinuousIntegrationBuild>) so both ci.yml and release.yml get it without editing each dotnet invocation. This is the higher-value half, as it normalizes embedded PDB source paths.


<sub>effort S · risk low · confirmed (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>build-config</code> · Installer.UI hardcodes RuntimeIdentifier + SelfContained in the csproj (not publish-gated), so every solution build pulls the full self-contained runtime — `src/StartupGroups.Installer.UI/StartupGroups.Installer.UI.csproj:22`</summary>


**Problem.** StartupGroups.Installer.UI.csproj sets <SelfContained>true</SelfContained> (line 22) and <RuntimeIdentifier>win-x64</RuntimeIdentifier> (line 23) unconditionally in the project, not just at publish. Because RuntimeIdentifier is set for all builds, the CI step 'dotnet build Salvo.slnx -c Release' (ci.yml line 45, release.yml line 56) restores a RID-specific graph and copies the entire self-contained .NET desktop runtime (~70MB, per the project's own comment) into this project's bin on every build — wasted work for a component that is only ever produced via the installer scripts and is being retired by the MSIX migration. This is also asymmetric with Salvo.App, which deliberately does NOT set a RID in the csproj and instead receives -r win-x64 --self-contained at publish time (ci.yml lines 116-121). So the two shipping executables handle RID/self-contained in two different ways with no central <Platforms>/<RuntimeIdentifiers> definition.


**Fix.** Delete lines 22-23 (SelfContained and RuntimeIdentifier) from StartupGroups.Installer.UI.csproj. No build.ps1 change is needed: installer/StartupGroups.Bundle/build.ps1 already publishes with `-r win-x64 --self-contained true`, so the shipping BA payload is unaffected while a plain solution build becomes framework-dependent and fast — and consistent with how Salvo.App is handled. If the Burn/Velopack installer is being retired by the MSIX migration, deleting the whole project (and its slnx entry) is the better end-state.


<sub>effort S · risk low · confirmed (high)</sub>

</details>


### xc:consistency+conventions  (4)

<details>
<summary><b>[low]</b> <code>error-handling</code> · Two JSON config stores diverge on write-durability and failure logging — `src/Salvo.App/Services/SettingsStore.cs:31`</summary>


**Problem.** Salvo has two primary JSON persistence stores that handle the identical concern (persist user config to disk, recover on load) two different ways. JsonConfigStore writes atomically and logs parse failures: SaveInternal writes to `ConfigPath + ".tmp"` then `File.Move(tempPath, ConfigPath, overwrite:true)` (JsonConfigStore.cs:113-115), and Load catches `JsonException` and logs it via the injected ILogger (JsonConfigStore.cs:73-76). SettingsStore does neither: Save does a direct non-atomic `File.WriteAllText(_settingsPath, json)` (line 31), and Load uses a bare `catch { return new AppSettings(); }` (lines 47-50) with no injected logger at all — a crash or power loss mid-write corrupts settings.json, and on next launch every user setting (theme, channel, auto-start, AlwaysRunAsAdmin) silently resets to defaults with zero diagnostic trail. The channel-seed marker write (App.xaml.cs:235) and the GitHub cache write (CachelessGithubSource.cs:118) follow the same non-atomic direct-write shape, so JsonConfigStore is the lone store that got the durable pattern.


**Fix.** Recommendation is sound as written. Two refinements: (1) When adding the logger, make it optional/nullable defaulting to NullLogger (mirror JsonConfigStore's `ILogger<JsonConfigStore>? logger = null`) so the direct `new SettingsStore()` at App.xaml.cs:366 still compiles. (2) The shared AtomicJson.Write helper is a nice-to-have but not required to close this; the minimal fix is temp-file + File.Move in Save plus logging the swallowed exception in Load before discarding. Extending the same atomic write to the marker (App.xaml.cs:235) and GitHub cache (CachelessGithubSource.cs:118) is optional follow-up, lower value since those are recreatable.


<sub>effort S · risk low · confirmed (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>duplication</code> · Two parallel operation-result types with divergent status vocabularies — `src/Salvo.Core/WindowsStartup/StartupOperationResult.cs:10`</summary>


**Problem.** The codebase models 'operation outcome (success / needs-admin / failed) + human message' with two independent types. OperationResult (Models/OperationResult.cs:14) is a `sealed record` class with a 5-value `OperationStatus` enum (Succeeded, AlreadyInState, NotFound, NeedsElevation, Failed), an `IsSuccess` property, and a `NeedsElevation()` factory; it is the return type of IAppOrchestrator launch/stop. StartupOperationResult (WindowsStartup/StartupOperationResult.cs:10) is a `readonly record struct` with a 3-value `StartupOperationStatus` enum (Ok, NeedsAdmin, Failed), a `Succeeded` property, and a `NeedsAdmin()` factory; it is the return type of every IWindowsStartupService method (IWindowsStartupService.cs:7-15) and RegistryRunValueWriter. So the same 'needs elevation' concept is spelled `NeedsElevation`/`NeedsAdmin`, the same 'did it work' predicate is `IsSuccess`/`Succeeded`, and one is a class while the other is a struct — callers that touch both subsystems must keep two mental models.


**Fix.** Do not merge the two types — that would force launch-domain fields (Source, Metrics) and states (AlreadyInState, NotFound) onto the registry layer and lose the deliberate lightweight-struct choice. If any change is wanted, limit it to aligning the shared vocabulary for readability: rename StartupOperationResult's Succeeded to IsSuccess and NeedsAdmin to NeedsElevation so the two domains read consistently. This is a minor, optional cosmetic tidy, not a medium-severity duplication defect, and could equally be left as-is since the two subsystems have no shared caller.


<sub>effort M · risk medium · needs-nuance (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>consistency</code> · TrayViewModel hand-rolls commands (new RelayCommand) against the [RelayCommand] source-gen convention used everywhere else — `src/Salvo.App/ViewModels/TrayViewModel.cs:48`</summary>


**Problem.** Every other ViewModel declares commands via the CommunityToolkit `[RelayCommand]` source generator (31 in MainWindowViewModel, 9 in AppEntryEditorViewModel, etc.). TrayViewModel — itself an ObservableObject, so the generator is available — instead hand-instantiates them: `public ICommand ShowMainWindowCommand => new RelayCommand(ShowMainWindow);` and `ExitCommand` (lines 48-49), plus `new AsyncRelayCommand(...)` at lines 188-189. Beyond the stylistic split, the two expression-bodied properties allocate a brand-new RelayCommand on every get, so each binding/access holds a different instance, defeating reference equality and CommandManager requery, and re-subscribing ShowMainWindow each time.


**Fix.** Cache the two commands in `private readonly` fields (e.g. `private readonly RelayCommand _showMainWindowCommand;` initialized in the constructor) and expose them via get-only properties, eliminating the per-get allocation while keeping the manual wiring that Initialize() and the tray-icon LeftClick/DoubleClick reuse. Converting to `[RelayCommand]` is optional and secondary — it would require making `Exit` a non-static instance method and may trip CA1822 under the repo's TreatWarningsAsErrors/EnforceCodeStyleInBuild settings, so the readonly-field cache is the safer path. Treat this as a minor consistency cleanup, not a behavioral bug.


<sub>effort S · risk low · needs-nuance (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>consistency</code> · Redundant per-property enum converter duplicates the context-wide UseStringEnumConverter setting — `src/Salvo.Core/Models/AppEntry.cs:9`</summary>


**Problem.** ConfigurationJsonContext sets `UseStringEnumConverter = true` globally (ConfigurationJsonContext.cs:12), which already serializes every enum in the graph as a string. AppEntry.Kind is nonetheless decorated with an explicit `[JsonConverter(typeof(JsonStringEnumConverter<AppKind>))]` (AppEntry.cs:9). It is the only enum property in the serialized config models carrying such an attribute — Node/Edge/FlowCondition/Configuration enums all rely solely on the context setting — so the attribute is redundant and, worse, implies a per-property opt-in rule that does not actually exist, which could mislead a future edit that removes the context-level flag.


**Fix.** Remove the redundant `[JsonConverter]` attribute on AppEntry.Kind and rely on the context-wide UseStringEnumConverter, consistent with every other enum in the config model graph.


<sub>effort S · risk low · confirmed (high)</sub>

</details>


### xc:duplication+dead-code  (7)

<details>
<summary><b>[low]</b> <code>duplication</code> · Windows command-line parsing (exe path + args split) reimplemented 4 times with divergent edge-case handling — `src/Salvo.Core/Services/AppOrchestrator.cs:509`</summary>


**Problem.** The same 'split a Windows command string into a leading executable path (quoted or first-space-delimited) plus the remainder' logic is copy-pasted across four independent methods, each subtly different: AppOrchestrator.ParseDirect (src/Salvo.Core/Services/AppOrchestrator.cs:509), WindowsServicesProvider.ExtractExecutable (src/Salvo.Core/Services/WindowsServicesProvider.cs:97), RegistryRunValueEditorViewModel.TryParseCommand (src/Salvo.App/ViewModels/RegistryRunValueEditorViewModel.cs:415), and WindowsStartupIconLoader.ExtractExecutablePath (src/Salvo.App/Services/WindowsStartupIconLoader.cs:57). They already disagree on inputs: only ExtractExecutable strips the NT-object '\??\' prefix; ParseDirect silently falls through to a space-split on an unbalanced opening quote (treating a leading '"' as part of the path) whereas TryParseCommand returns null; ExtractExecutablePath rejects close<=1 while others use close>1. Any future fix (e.g. handling escaped quotes) must be applied in four places and today's inconsistencies are latent bugs.


**Fix.** Extract a single shared helper in Salvo.Core (e.g. CommandLine.SplitExecutable(command) -> (string Path, string Remainder), plus a separate argv tokenizer for the arg-list variant) and route all four sites through it, unifying the unbalanced-quote policy and close-index threshold and folding in the '\\??\\' stripping. Crucially, keep each call site's distinct wrapper behavior intact — ParseDirect's cmd.exe empty-input fallback and (FileName, Arguments) shape, WindowsStartupIconLoader's env-expansion + File.Exists gating, WindowsServicesProvider's File.Exists check, and TryParseCommand's null-on-ambiguity contract — since a naive full merge would change behavior. Add unit tests covering quoted paths, unbalanced quotes, and the '\\??\\' prefix to lock the unified semantics.


<sub>effort M · risk medium · confirmed (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>duplication</code> · GitHub-release HTTP client and fetch logic duplicated between the two IUpdateService implementations — `src/Salvo.App/Services/MsixUpdateService.cs:187`</summary>


**Problem.** MsixUpdateService and VelopackUpdateService each carry near-identical infrastructure: CreateReleaseClient (src/Salvo.App/Services/MsixUpdateService.cs:187) and CreateReleaseBodyClient (src/Salvo.App/Services/UpdateService.cs:85) build the exact same proxy-bypassing HttpClientHandler (UseProxy/UseDefaultCredentials/UseCookies=false, Credentials=null) with the same 'Salvo' user-agent, github.v3 Accept header, and 15s timeout (a third copy of the handler config lives in CleanHttpClientFileDownloader at UpdateService.cs:24). Both services also duplicate the GitHub repo-path derivation (`new Uri(AppBranding.SupportUrl); AbsolutePath.Trim('/'); https://api.github.com/repos/{repoPath}/releases/...`) in FetchLatestReleaseAsync (MsixUpdateService.cs:171) vs TryFetchGithubReleaseBodyAsync (UpdateService.cs:227), and each declares its own private GitHub-release DTO with a `body` property. This will drift as one path is maintained and the other is not.


**Fix.** Given VelopackUpdateService is scheduled for retirement (Phase 3 of the MSIX migration, per MsixUpdateService's own docstring), the lowest-risk option is to leave the duplication until that class is deleted, after which it disappears on its own. If a fix is wanted before then, keep it minimal: extract only a tiny static helper for the two shared pieces that actually match — a plain-HttpClient factory (the proxy-bypass handler + UA/Accept/timeout) and a repo-path builder off AppBranding.SupportUrl — and have both services call it. Do NOT try to unify CleanHttpClientFileDownloader's handler into it (it needs redirect/decompression config and subclasses Velopack's downloader), and do NOT introduce a single shared release DTO/GitHubReleaseClient abstraction — the two DTOs differ in shape (superset vs body-only) and building a shared client abstraction adds coupling that will be unwound at Velopack retirement.


<sub>effort M · risk medium · needs-nuance (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>duplication</code> · Self-relaunch-as-administrator routine duplicated in App and MainWindowViewModel — `src/Salvo.App/ViewModels/MainWindowViewModel.cs:476`</summary>


**Problem.** The 'resolve Environment.ProcessPath, start a new ProcessStartInfo with UseShellExecute=true + Verb="runas", then Shutdown, catching the UAC-cancel Win32Exception' sequence exists twice: App.TryRelaunchAsAdminIfConfigured (src/Salvo.App/App.xaml.cs:352-401) and MainWindowViewModel.RestartAsAdmin (src/Salvo.App/ViewModels/MainWindowViewModel.cs:476-509). The two copies have already diverged: the App copy forwards the original args plus a '--no-elevate-relaunch' guard flag, while RestartAsAdmin forwards no args at all and never passes the guard flag, so the elevation entry points are inconsistent and any change to how the app re-launches itself (args, working dir, guard flag) must be mirrored by hand.


**Fix.** Extract only the shared core — build the runas ProcessStartInfo (UseShellExecute=true, Verb=\"runas\", WorkingDirectory), Process.Start, Shutdown, and catch the UAC-cancel Win32Exception (code 1223) — into a helper such as ProcessElevation.RelaunchSelfAsAdmin(string arguments), taking the forwarded-args string as a parameter. App keeps passing args + SkipElevateFlag; RestartAsAdmin passes string.Empty. Leave the gating logic (SkipElevateFlag arg check, ElevationDetector.IsElevated, AlwaysRunAsAdmin settings lookup) in App, since it has no VM analog. Also reconcile the intentionally different catch behavior (App swallows all Win32Exception and returns false to continue non-elevated; VM narrows to 1223 and surfaces other errors via dialog) — the helper should either expose an error callback or the callers should keep their own outer catch so this behavioral difference is preserved deliberately rather than by accident.


<sub>effort S · risk medium · confirmed (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>duplication</code> · AppIconLoader and WindowsStartupIconLoader are near-identical background STA icon-load loops — `src/Salvo.App/Services/WindowsStartupIconLoader.cs:12`</summary>


**Problem.** WindowsStartupIconLoader.LoadFor (src/Salvo.App/Services/WindowsStartupIconLoader.cs:12) and AppIconLoader.LoadFor (src/Salvo.App/Services/AppIconLoader.cs:16) are byte-for-byte the same except the element type, the ResolveSource projection, and the thread name: both filter items whose Icon is null, project (vm, source), bail if empty, spin a background STA thread (IsBackground, BelowNormal priority), iterate calling AppIconCache.Get(source) inside try/catch, and dispatcher.BeginInvoke(() => vm.Icon = icon, DispatcherPriority.Background). The entire threading/dispatch body is copy-pasted.


**Fix.** Extract one generic helper, e.g. IconLoad.LoadAsync<T>(IEnumerable<T> items, Func<T,string?> resolveSource, Action<T,BitmapSource> assign, string threadName), and have both loaders supply only the projection and assignment.


<sub>effort M · risk low · confirmed (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>dead-code</code> · IElevationClient.IsElevated is never consumed and duplicates ElevationDetector.IsElevated — `src/Salvo.Core/Elevation/IElevationClient.cs:5`</summary>


**Problem.** IElevationClient declares bool IsElevated (src/Salvo.Core/Elevation/IElevationClient.cs:5), implemented in ElevationClient (src/Salvo.Core/Elevation/ElevationClient.cs:24-32) with a WindowsIdentity/WindowsPrincipal admin-role check. Every actual elevation-state read in the codebase goes through the static Salvo.Core.Launch.ElevationDetector.IsElevated (App.xaml.cs:361, MainWindowViewModel.cs:112, RegistryRunValueEditorViewModel.cs:128, WindowsStartupEntryViewModel.cs:33, EtwResourceMonitor.cs:59); a grep for '.IsElevated' shows no call site uses the injected IElevationClient.IsElevated. The member is dead and its implementation re-derives (without ElevationDetector's Lazy caching and try/catch fallback) logic that already exists in one canonical place.


**Fix.** Delete IsElevated from IElevationClient and ElevationClient; keep ElevationDetector.IsElevated as the single source of truth.


<sub>effort S · risk low · confirmed (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>dead-code</code> · AppIconCache.Set is an unreferenced public method — `src/Salvo.App/Services/AppIconCache.cs:22`</summary>


**Problem.** AppIconCache.Set(string source, BitmapSource? bitmap) (src/Salvo.App/Services/AppIconCache.cs:22) has no callers anywhere in src or tests (grep for 'AppIconCache.Set' returns only the definition). The cache is only ever populated via the internal Get path. The method is dead surface area.


**Fix.** Remove AppIconCache.Set (the ConcurrentDictionary is already written through Get).


<sub>effort S · risk low · confirmed (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>duplication</code> · Identical AppEntry-from-fields construction plus known-app lookup duplicated within AppEntryEditorViewModel — `src/Salvo.App/ViewModels/AppEntryEditorViewModel.cs:196`</summary>


**Problem.** GetNeedsValueFlagSet (src/Salvo.App/ViewModels/AppEntryEditorViewModel.cs:196-208) and RefreshSuggestions (src/Salvo.App/ViewModels/AppEntryEditorViewModel.cs:301-314) build a new AppEntry from the same eight view-model properties (Name/Kind/Path/Service/Args/WorkingDirectory/DelayAfterSeconds/Enabled) and then run the identical two-line lookup `var matchers = _matchers.GetMatchers(entry); var known = _knownApps.FindMatch(entry, matchers);`. Both the field-copy and the lookup are duplicated verbatim.


**Fix.** Add a private helper (e.g. KnownApp? FindKnownApp() that builds the AppEntry once via a ToAppEntry() method and returns the matched KnownApp) and call it from both methods.


<sub>effort S · risk low · confirmed (high)</sub>

</details>


### xc:magic-numbers+hardcoding  (10)

<details>
<summary><b>[low]</b> <code>duplication</code> · Windows Run / StartupApproved registry key paths duplicated verbatim across three files — `src/Salvo.Core/WindowsStartup/RegistryRunValueWriter.cs:13`</summary>


**Problem.** The well-known Run and StartupApproved registry sub-key paths are hardcoded as identical string literals in three separate places: RegistryRunValueWriter.cs:13-16 (RunPath, RunWow64Path, StartupApprovedRun, StartupApprovedRun32), WindowsStartupService.cs:9-13 (same four plus StartupApprovedFolder), and Salvo.Elevator/Program.cs:96-98 (runPath, approvedRun, approvedRun32). All three participate in the same enable/disable/rename/delete feature, and the elevator path is reached only after UAC where mistakes are hardest to notice. A typo or Windows-path change in one copy would silently desync the in-process HKCU path from the elevated HKLM path (e.g. an entry could be written under one key and its approval bit toggled under another). The codebase already centralizes exactly this kind of value in AppIdentifiers (WindowsPersonalizeRegistryKey, InstallRegistryKey), so the pattern for a home exists.


**Fix.** Consolidate the Run/StartupApproved sub-key paths (and ideally the shared source->(root,path,approvedRoot,approvedPath) resolution) into one shared static class in Salvo.Core.WindowsStartup, and reference it from RegistryRunValueWriter, WindowsStartupService, and Salvo.Elevator/Program.DeleteRunValue. While doing so, drop the unused RunWow64Path const in RegistryRunValueWriter.cs:14. Frame this as a maintainability/DRY cleanup (single source of truth across the in-process and post-UAC elevator paths), not a correctness fix — no active defect exists today.


<sub>effort M · risk low · confirmed (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>duplication</code> · GitHub API host, Accept header, and User-Agent duplicated across the three update-service files — `src/Salvo.App/Services/MsixUpdateService.cs:175`</summary>


**Problem.** The GitHub REST access details are copy-pasted across MsixUpdateService, VelopackUpdateService (UpdateService.cs) and CachelessGithubSource. The base URL "https://api.github.com/repos/{repoPath}/releases/..." appears three times (MsixUpdateService.cs:175, UpdateService.cs:233, CachelessGithubSource.cs:70); the Accept media type "application/vnd.github.v3+json" appears three times (MsixUpdateService.cs:200, UpdateService.cs:98, CachelessGithubSource.cs:74); and the User-Agent product literal "Salvo" is hardcoded twice (MsixUpdateService.cs:199, UpdateService.cs:97) instead of using the existing AppBranding.AppName (which already resolves to "Salvo"). This is exactly the duplicated-value case the audit prioritizes, and the hardcoded "Salvo" UA bypasses an existing branding constant.


**Fix.** Add a small GitHubApi constants class in Salvo.Core exposing the host (\"https://api.github.com\"), the vnd.github.v3+json Accept value, and a helper to build repos/{repoPath}/releases endpoints, and reference it from all three sites. Replace the literal \"Salvo\" UA with AppBranding.AppName in the two HttpClient factories (MsixUpdateService.CreateReleaseClient and VelopackUpdateService.CreateReleaseBodyClient). Do NOT try to unify the HttpClient creation with CachelessGithubSource — it is a Velopack GithubSource subclass using Downloader.DownloadString with a header dictionary and an intentionally different \"Velopack\" User-Agent; only share the host/media-type string constants there.


<sub>effort M · risk low · confirmed (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>magic-number</code> · Update HTTP timeouts hardcoded (15s x2, 30s x1) while the purpose-built Timeouts.UpdateCheckerHttp is dead — `src/Salvo.App/Services/UpdateService.cs:99`</summary>


**Problem.** The HTTP client timeout for update/release-body fetches is hardcoded as TimeSpan.FromSeconds(15) in two files (UpdateService.cs:99 and MsixUpdateService.cs:201), and CachelessGithubSource.cs:82 hardcodes yet a third value (Downloader.DownloadString(url, headers, timeout: 30)). Meanwhile Timeouts.cs:35 defines `UpdateCheckerHttp = TimeSpan.FromSeconds(8)` under the "Network calls" comment specifically for this purpose — and a repo-wide grep shows it is referenced nowhere. So an intent-carrying constant is dead code while three call sites hardcode two other values, and the update path's network timeout is effectively un-tunable and inconsistent (8 vs 15 vs 30).


**Fix.** Treat as a low-priority cleanup. Minimum viable fix: either delete the unreferenced Timeouts.UpdateCheckerHttp constant, or wire it to the call site it was named for. Do NOT collapse all three sites onto one value — the two 15s release-body fetches and the 30s latest-release lookup are distinct operations. If centralizing, introduce two intent-named constants (e.g. ReleaseBodyHttp = 15s and UpdateCheckHttp = 30s, or repurpose UpdateCheckerHttp for the latter), each preserving the current effective value, rather than merging them and changing the 30s to 15s. Given the Velopack/Burn path is being retired per the MSIX migration, prioritize the MsixUpdateService.cs:201 site and consider just deleting the dead constant for the legacy Velopack sites.


<sub>effort S · risk low · needs-nuance (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>consistency</code> · Command-line argument flags scattered across four+ files with no central registry; --show-main-window duplicated — `src/Salvo.App/App.xaml.cs:159`</summary>


**Problem.** The app's CLI contract is spread across unrelated files as ad-hoc literals: App.xaml.cs defines SkipElevateFlag "--no-elevate-relaunch" (line 34), EnableAutoStartArg "--enable-autostart" (159), ShowMainWindowArg "--show-main-window" (160); VelopackUpdateService defines RestartedAfterUpdateArg "--restarted-after-update" (UpdateService.cs:225); AppIdentifiers defines TrayCommandLineFlag "--tray" (AppIdentifiers.cs:6); and the elevator protocol flag "--payload" is a bare literal on both sides (ElevationClient.cs:64 producer, Salvo.Elevator/Program.cs:124 consumer). "--show-main-window" is also duplicated as a raw string in StartupGroups.Installer.UI/InstallerBootstrapperApplication.cs:310. Because these are a cross-process contract, a mismatch fails silently (the flag is simply never recognized).


**Fix.** Narrow the fix to the flags that are actually bare/duplicated across process boundaries. Priority 1: define a single constant for '--payload' in Salvo.Core and reference it from both ElevationClient (producer) and Salvo.Elevator/Program.cs (consumer) — this is the clearest cross-assembly literal pair with silent-failure risk. Priority 2: have App reference a shared constant for '--show-main-window'/'--enable-autostart' that the installer also uses (low value given the installer is being retired). Do NOT churn the already-centralized single constants (RestartedAfterUpdateArg, TrayCommandLineFlag, SkipElevateFlag) — moving them into a registry is optional polish with little benefit. If a central CommandLineArgs class is created alongside AppIdentifiers, scope it to genuine inter-process flags only.


<sub>effort M · risk low · needs-nuance (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>magic-number</code> · Process-wait timeouts hardcoded (RunCommand 60000ms, kill 5000ms) instead of using Timeouts — `src/Salvo.Core/Services/AppOrchestrator.cs:482`</summary>


**Problem.** AppOrchestrator.RunCommand caps a spawned command with process.WaitForExit(60_000) (line 482) — a bare 60-second literal whose own comment calls it a 'generous timeout' — and ProcessInspector.KillProcesses uses process.WaitForExit(5_000) (ProcessInspector.cs:178) as a kill-grace window. Both are behavioral tuning values living as inline magic numbers while every other orchestration/probe timeout in this codebase is centralized in Timeouts (OrchestratorServiceOperation, ProbePoll*, PidResolve*, etc.), making these two the odd ones out and un-tunable.


**Fix.** Add Timeouts.RunCommandExecution = TimeSpan.FromSeconds(60) and Timeouts.ProcessKillGrace = TimeSpan.FromSeconds(5). Because Process.WaitForExit only accepts an int (no TimeSpan overload), reference them with a cast: AppOrchestrator.cs:482 -> process.WaitForExit((int)Timeouts.RunCommandExecution.TotalMilliseconds); ProcessInspector.cs:178 -> process.WaitForExit((int)Timeouts.ProcessKillGrace.TotalMilliseconds).


<sub>effort S · risk low · confirmed (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>magic-number</code> · Icon pixel sizes hardcoded at call sites (32 duplicated, plus 48 and 16) — `src/Salvo.App/Services/AppIconCache.cs:17`</summary>


**Problem.** Requested icon dimensions are inline literals at each call site: ShellIconExtractor.GetImage(source, 32) appears in both AppIconCache.cs:17 and AddAppPickerViewModel.cs:209 (same value duplicated), GetImage(Path, 48) in AppEntryEditorViewModel.cs:117, and IconSize = 16 in TrayMenuFactory.cs:22. The app already has a home for exactly this — Animations/Durations.cs defines a UiMetrics static class for UI dimension constants — yet these icon sizes bypass it, so the list/picker icon size (32) is maintained in two places.


**Fix.** Introduce a single named constant only for the shared list/picker icon size (e.g. UiMetrics.ListIconSize = 32) and reference it from both AppIconCache.cs:17 and AddAppPickerViewModel.cs:209, giving the duplicated 32 one source of truth. Leave the single-use 48 (AppEntryEditorViewModel) and 16 (TrayMenuFactory) as inline literals — they appear exactly once each, the GetImage(size)/IconSize parameters already name their meaning, and hoisting them into a shared constants class adds indirection without eliminating any duplication. If unification is desired for consistency anyway, treat it as optional polish rather than a fix.


<sub>effort S · risk low · needs-nuance (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>magic-number</code> · Elevator process exit codes are undocumented magic numbers forming a cross-process protocol — `src/Salvo.Elevator/Program.cs:26`</summary>


**Problem.** The elevator returns bare integer exit codes with implicit meaning — 2 for bad/missing payload (lines 23,31,66,80), 3 for an unexpected exception (line 36), 1 for operation failure (lines 62,73), 0 for success — and the other side, ElevationClient.WaitAsync, interprets them purely as `process.ExitCode == 0` (ElevationClient.cs:93). The distinct non-zero codes are a real success/failure protocol between two executables but are unnamed on both ends, so their semantics are undiscoverable and easy to break when editing either side.


**Fix.** If addressed at all, define a small enum LOCAL to the Salvo.Elevator project (e.g. ExitCode { Success = 0, OperationFailed = 1, BadPayload = 2, Unhandled = 3 }) and use it only to name the return values inside Program.cs for readability. Do not promote it to Salvo.Core or attempt to consume the granular codes in ElevationClient: the client only needs (and only checks) success vs non-zero, so the '== 0' comparison there is already correct and should stay as-is. Frame this as a minor single-file readability improvement, not a cross-process protocol fix.


<sub>effort S · risk low · needs-nuance (medium)</sub>

</details>

<details>
<summary><b>[low]</b> <code>hardcoded-value</code> · settings.json filename not centralized in AppPaths alongside the other data-file names — `src/Salvo.App/Services/SettingsStore.cs:17`</summary>


**Problem.** SettingsStore hardcodes the filename "settings.json" (line 17) while AppPaths centralizes every sibling data-file/folder name — ConfigFileName "config.json", BenchmarksDbFileName, LogFolderName, LocalDataFolderName (AppPaths.cs:7-11). The settings file is the one user-data artifact whose name lives outside AppPaths, an inconsistency that also shows up as a fragile manual sync elsewhere (InstallerBootstrapperApplication.cs:669 carries a 'must stay in sync with AppPaths.LocalDataFolderName' comment and re-hardcodes the same names).


**Fix.** Add `public const string SettingsFileName = "settings.json";` and a `public static string SettingsFilePath => Path.Combine(UserDataFolder, SettingsFileName);` helper to AppPaths, then have SettingsStore use `AppPaths.SettingsFilePath` (mirroring the existing ConfigFilePath usage). Drop the InstallerBootstrapperApplication justification from the rationale — that is retired pre-rename installer code using old brand names that does not consume AppPaths and is unaffected by this change.


<sub>effort S · risk low · confirmed (medium)</sub>

</details>

<details>
<summary><b>[low]</b> <code>dead-code</code> · AppIdentifiers.MainExecutableName is defined but never referenced — `src/Salvo.Core/Branding/AppIdentifiers.cs:9`</summary>


**Problem.** AppIdentifiers.MainExecutableName = "Salvo.exe" (line 9) is a public const, but a repo-wide grep finds no usage anywhere in src — the sibling ElevatorExecutableName is used by ElevationPaths, but the main-exe constant is dead. A hardcoded executable name that exists only as an unused constant is a latent trap: future code is likely to hardcode "Salvo.exe" again rather than discover this, and the constant can silently rot if the exe is ever renamed.


**Fix.** Remove the unused MainExecutableName const. Do NOT adopt the alternative of wiring existing exe-path resolution to it — App.xaml.cs, MainWindowViewModel.cs, and TaskSchedulerAutoStartService.cs correctly resolve the running executable via Environment.ProcessPath / MainModule.FileName, which is more robust than a hardcoded "Salvo.exe" and should be left as-is.


<sub>effort S · risk low · confirmed (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>duplication</code> · "shell:" URI-scheme prefix literal duplicated across PathResolver and ProcessLauncher — `src/Salvo.Core/Services/PathResolver.cs:12`</summary>


**Problem.** The special-case "shell:" prefix is hardcoded in two Salvo.Core files that must agree: PathResolver.Resolve returns the raw path unchanged when it StartsWith("shell:") (PathResolver.cs:12), and ProcessLauncher.ResolveWorkingDirectory branches on the same StartsWith("shell:") to pick a working directory (ProcessLauncher.cs:63). They encode one shared assumption (this string marks a shell-launch target) in two literals; if one changes the working-directory handling for shell entries silently diverges from resolution.


**Fix.** If consolidating shell literals, prefer centralizing the higher-value, actually-scattered "shell:AppsFolder\\" prefix (currently three separately-named local consts across ProcessMatcherResolver, KnownAppsDatabase, and ShellInstalledAppsProvider) into one shared constant, and derive/reference the bare "shell:" scheme from that same location so PathResolver and ProcessLauncher agree. Extracting only the two-site bare "shell:" prefix is low value on its own since it is an immutable OS-defined scheme.


<sub>effort S · risk low · needs-nuance (high)</sub>

</details>


### xc:performance+allocations  (6)

<details>
<summary><b>[medium]</b> <code>performance</code> · RefreshRunningStates snapshots the full process table once per app, every 3s, on the UI thread — `src/Salvo.App/ViewModels/MainWindowViewModel.cs:726`</summary>


**Problem.** RefreshRunningStates() (line 726) loops every app in every group and calls _orchestrator.IsRunning(app.ToModel()) synchronously on the UI dispatcher. For executable apps this reaches ProcessInspector.IsRunning (ProcessInspector.cs:20), which calls Process.GetProcessesByName(name) per matcher — and GetProcessesByName internally enumerates the ENTIRE process table (NtQuerySystemInformation) and filters by name. So for N configured apps you take N full process-table snapshots. The AUMID path is worse: FindProcessesByAumid (ProcessInspector.cs:137) calls Process.GetProcesses() (another full snapshot) and opens a process handle for every running process to read its AUMID — so an app matched by AUMID costs O(all_processes) handle opens. This runs on the DispatcherTimer every StatusRefreshInterval = 3s (Timeouts.cs:15) plus once synchronously during MainWindowViewModel construction at startup. Net cost is O(apps x all_processes) on the UI thread every 3 seconds, causing periodic hitching and constant background CPU even when nothing changes. Each tick also allocates a fresh AppEntry via app.ToModel() (line 734) per app.


**Fix.** Per refresh, take ONE Process.GetProcesses() snapshot: build a name->processes map to satisfy all exe matchers, and make a single AUMID pass over that same snapshot (open each handle once) instead of one full O(all_processes) pass per AUMID app. Run the whole sweep on a background thread and marshal only the resulting bool[] back to the UI to avoid occupying the UI thread. Drop the 'cache matchers' item — ProcessMatcherResolver already caches matcher resolution by path; the residual ToModel() allocation is negligible. Note the timer already runs at DispatcherPriority.Background, so the primary wins are reduced CPU and avoiding UI-thread blocking during large/AUMID sweeps, not eliminating visible hitching.


<sub>effort M · risk medium · needs-nuance (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>performance</code> · JsonConfigStore.Load() has no caching; config.json is read and parsed 3x on the startup UI thread — `src/Salvo.Core/Services/JsonConfigStore.cs:26`</summary>


**Problem.** Load() (line 26) always does File.ReadAllText (line 38) + JsonSerializer.Deserialize + a FlowMigration.NeedsMigration scan over all groups, with no in-memory cache. During cold start it is invoked three times, all on the UI thread before first paint: App.OnStartup (App.xaml.cs:110, whose return value is discarded entirely), TrayViewModel ctor -> RebuildGroups (TrayViewModel.cs:70), and MainWindowViewModel ctor -> LoadFromConfig(_configStore.Load()) (MainWindowViewModel.cs:131). All three view-models are built at startup because TrayViewModel is resolved unconditionally at App.xaml.cs:114 (even in tray-only mode) and pulls in MainWindowViewModel. Additionally, on every config-change event the TrayViewModel handler (TrayViewModel.cs:42) reloads from disk via RebuildGroups even though the Changed event already carries the fresh Configuration payload.


**Fix.** Prefer the finding's "minimum" approach over full internal caching. (1) Delete the discarded configStore.Load() at App.xaml.cs:110 — Load's file-creation/migration is idempotent and will run on the first real load; keep BeginWatching() (watcher targets the directory and debounces via _lastLoadUtc, so ordering is unaffected). (2) Load the Configuration once in OnStartup and hand the same instance to both view-models (e.g. construct them with the pre-loaded config, or expose a shared getter) so parse+migration runs once. (3) Change TrayViewModel's Changed handler (TrayViewModel.cs:42) to pass the event's Configuration into RebuildGroups instead of calling _configStore.Load() again — mirroring MainWindowViewModel.OnConfigStoreChanged (line 660); this also removes a per-config-change disk re-read at runtime. Be cautious about the proposed in-store caching: JsonConfigStore both reads and writes, mutates-and-saves during migration (68), and fires a FileSystemWatcher, so a cache would need careful invalidation on Save/watcher events for medium risk and little extra benefit over steps 1-3.


<sub>effort M · risk medium · confirmed (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>architecture</code> · Whole telemetry/benchmark/ETW subsystem is eagerly constructed at startup, even in tray-only mode — `src/Salvo.App/ViewModels/MainWindowViewModel.cs:67`</summary>


**Problem.** MainWindowViewModel's constructor declares optional dependencies ILaunchTelemetryService (line 67), ILaunchBenchmarkStore (line 68) and BenchmarksViewModel (line 69). These are all registered in DI, so Microsoft.Extensions.DependencyInjection resolves and injects them rather than using the null defaults. Because MainWindowViewModel is always built at startup (TrayViewModel dependency, resolved at App.xaml.cs:114), this eagerly constructs LaunchTelemetryService -> EtwResourceMonitor (registered singleton, App.xaml.cs:294) and SqliteLaunchBenchmarkStore (App.xaml.cs:290). EtwResourceMonitor's ctor kicks off Task.Run(TryStart) (EtwResourceMonitor.cs:34) which forces first-load of the large Microsoft.Diagnostics.Tracing (TraceEvent) assembly, and SqliteLaunchBenchmarkStore forces the SQLite native dll load + schema creation (SqliteLaunchBenchmarkStore.cs:65). Both are deferred to background threads (good), but the heavy assembly/native-library loads and JIT still happen inside the cold-start window even when the user launches nothing and the main window never shows.


**Fix.** Make the benchmark/telemetry/ETW stack lazy: inject Lazy<ILaunchTelemetryService>/Lazy<ILaunchBenchmarkStore> or a factory, and only trigger construction when the user first launches a group or opens the Benchmarks view. That keeps TraceEvent + SQLite off the first-paint / tray-ready path.


<sub>effort M · risk medium · needs-nuance (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>performance</code> · LoadHistoricalBenchmarksAsync issues N sequential SQLite queries (new connection each) at startup — `src/Salvo.App/ViewModels/MainWindowViewModel.cs:686`</summary>


**Problem.** LoadHistoricalBenchmarksAsync (line 686), fired during LoadFromConfig at startup, loops every app and awaits _benchmarkStore.GetRecentAsync(app.ComputedAppId, 1) one at a time (line 695). Each GetRecentAsync opens a brand-new SqliteConnection and runs its own query (SqliteLaunchBenchmarkStore.cs:146-158), and the await uses ConfigureAwait(true) so every iteration also hops back to the UI thread to _dispatcher.Invoke(ApplyMetrics). For a user with many apps this is N connection opens + N round-trips + N UI-thread marshals, serialized.


**Fix.** Optional cleanup, not a startup-perf fix. If simplifying, replace the per-app loop with a single query returning the latest launch per app_id — e.g. a correlated subquery/window: `SELECT * FROM launches l WHERE requested_at_utc = (SELECT MAX(requested_at_utc) FROM launches WHERE app_id = l.app_id)`, or `WHERE app_id IN (...)` then reduce to newest-per-id in memory — read over one connection and apply once. Note the queries are already indexed and connection-pooled, and the work is fire-and-forget off the cold-start path, so treat this as readability/tidiness rather than a measurable performance win; do not frame it around connection-open or UI-marshal costs.


<sub>effort M · risk low · needs-nuance (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>performance</code> · settings.json is read and deserialized twice during startup — `src/Salvo.App/App.xaml.cs:366`</summary>


**Problem.** TryRelaunchAsAdminIfConfigured constructs a throwaway `new SettingsStore().Current` (line 366) purely to read the AlwaysRunAsAdmin flag. That SettingsStore ctor calls AppPaths.EnsureUserDirectories() (3x Directory.CreateDirectory) and File.ReadAllText + JSON deserialize of settings.json (SettingsStore.cs:14-19). Moments later the DI singleton ISettingsStore (SettingsStore) is built and reads+parses settings.json a second time, and AppPaths.EnsureUserDirectories() was also already called at App.xaml.cs:46. So settings.json is parsed twice and the user directories are ensured three-plus times on the cold path.


**Fix.** Keep the pre-host lightweight load (it must run before the host/Serilog are built, so it cannot be replaced by the DI singleton without regressing the relaunch fast-exit path). Eliminate the redundancy by loading AppSettings once before host build and feeding that instance into the DI SettingsStore registration (e.g., register the pre-loaded AppSettings and have SettingsStore accept it), so settings.json is parsed exactly once. Do NOT move the elevation decision after host build. The triple EnsureUserDirectories is harmless (no-op when dirs exist) and App.xaml.cs:46 is independently needed to guarantee the log folder exists before Serilog is configured, so at most drop it from the throwaway path — but given the negligible magnitude, this is optional cleanup rather than a required fix.


<sub>effort S · risk low · needs-nuance (high)</sub>

</details>

<details>
<summary><b>[low]</b> <code>performance</code> · Tray command properties allocate a new RelayCommand on every getter access — `src/Salvo.App/ViewModels/TrayViewModel.cs:48`</summary>


**Problem.** ShowMainWindowCommand (line 48) and ExitCommand (line 49) are expression-bodied getters that `new RelayCommand(...)` on every read. They are consumed by TrayMenuFactory.Build (TrayMenuFactory.cs:48,53), which is rebuilt on every config change, and ShowMainWindowCommand is also invoked from App.OnStartup:130 — each read creates a distinct command instance, so command identity/CanExecute caching is lost and each menu rebuild churns fresh allocations.


**Fix.** Cache each command in an init-once readonly field (e.g. `private readonly RelayCommand _showMainWindowCommand;` assigned in the constructor, exposed via `public ICommand ShowMainWindowCommand => _showMainWindowCommand;`), matching how TrayGroupItem already stores its Launch/Stop commands. Frame this as a code-consistency cleanup rather than a performance fix — the allocation savings are negligible and there is no CanExecute behavior to preserve.


<sub>effort S · risk low · needs-nuance (high)</sub>

</details>


---

## Deliberately NOT doing (rejected / out-of-scope)

These were considered and rejected during verification — do **not** re-introduce them.

- Do not polish, rebrand, or bug-fix the StartupGroups.Installer.UI Burn project — it is already non-functional against the renamed app and slated for deletion in MSIX Phase 3. All its branding/staleness/channel-picker/RID findings resolve by deleting the project, not fixing it.
- Do not build a shared GitHub-release HTTP/DTO abstraction spanning the Velopack update path — VelopackUpdateService and CachelessGithubSource are retiring in Phase 3; defer the duplication until they are deleted (only the tiny Salvo-owned factory is worth extracting if touched at all).
- Do not pursue the full AppOrchestrator God-class split — the graph executor and lifecycle methods are already covered by GraphOrchestratorTests/AppOrchestratorTests and splitting adds interface seams with little payoff; only extract ParseDirect for unit-testability.
- Do not merge OperationResult and StartupOperationResult into one type — that forces launch-domain fields/states onto the registry layer and discards the deliberate lightweight-struct choice; at most align the Succeeded/NeedsAdmin naming.
- Do not add a full NodeKind enum for compile-time dispatch safety — the authoritative discriminators are XAML Tag strings, so type-checking is inherently runtime; making the unrecognized-kind default throw plus shared const strings is sufficient.
- Do not add a root .editorconfig unless the team explicitly wants cosmetic IDExxxx style enforcement — compiler and CA warnings are already build-failing via TreatWarningsAsErrors; this is a preference, not a defect.
- Do not introduce a full re-runnable/self-healing SQLite init or an EventLog source in the elevator — a doc correction plus a plain %LOCALAPPDATA% log file are the proportionate fixes; benchmark telemetry loss does not affect the launch path.
- Do not extract single-use magic values (48/16 icon sizes, 0.35 ghost opacity, the bare 'shell:' scheme on its own) or churn already-centralized single-constant flags — no duplication is removed.
