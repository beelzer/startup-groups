# Roadmap

Living list of work that's been thought about but isn't done yet. Items aren't commitments — they're notes-to-future-self with the reasoning preserved so we don't relitigate decisions.

## Update + installer architecture

The current setup ships a 250 MB WiX MSI per release and a custom auto-updater that downloads the whole MSI on every update. That's "fine" rather than "great". The plan below replaces it with a best-in-class stack: delta updates, channels, modern Fluent installer UI, enterprise compatibility — without throwing away the foundation we've already built.

### The stack

| Layer | Tool | Why |
|---|---|---|
| Auto-update mechanism (deltas, channels, rollback) | **Velopack** (NuGet `Velopack` + `vpk` CLI) | Active successor to Squirrel.Windows / Clowd.Squirrel. Per-file Zstandard binary deltas typically 5–30 MB instead of 250 MB. First-class channel support with runtime switching. Per-user install means no UAC on update. |
| In-app update UI (what users see every release) | **WPF + WPF-UI Fluent flyout in main app** | Where the polish ROI is highest — users see this every release. Progress, release notes, defer/install actions, dark/light following Windows. |
| First-install / repair / uninstall UI | **Custom managed Bootstrapper Application — WPF + WPF-UI Fluent** | Visual Studio Installer pattern. Burn provides install correctness; our WPF app provides the visual identity. Mica background, dark-mode-following, identical look to the running app. |
| Install correctness layer | **WiX 5 Burn bundle** wrapping the existing MSI | Free MSI rollback, repair, files-in-use detection, Add/Remove Programs entry, transactional safety. No need to reinvent. |
| Enterprise distribution | The same MSI inside the Burn bundle | IT departments can deploy `Setup.exe` silently or extract the MSI for SCCM / Intune. |

### Why this combo and not single-tool

Tools considered and why each was rejected as the *sole* solution:

- **Velopack alone**: per-user only; first-install splash is a single-button affair, not a Fluent wizard.
- **WiX/Burn alone**: MSP patches are heavy, not file-level deltas; channels would be DIY.
- **MSIX**: cannot ship without a code-signing cert that chains to a trusted root (we've ruled out signing for now).
- **ClickOnce**: still maintained but the install UI is a Microsoft-imposed dialog you can't customize.
- **InnoSetup / NSIS**: no first-party delta updates, no channels.
- **Squirrel.Windows / Clowd.Squirrel**: dead and pointing-to-Velopack respectively.
- **NetSparkle**: lovely WPF UI, but no deltas — kills it for a 250 MB payload.
- **Themed `WixStdBA`**: can match PowerToys' green-bar look but can't do Fluent / Mica / Dark-mode-following — limited to WiX's own widget set.

### Phase 1 status — ✅ COMPLETE as of v0.2.11

End-to-end auto-update flow validated on a real install. **First known-good baseline: v0.2.8** (the first release with `CachelessGithubSource`). Anything older has at least one of: 401 from the Velopack 0.0.1297 bug, or a 15–30 min cache-lag in update detection. Existing users on < v0.2.8 should manually run `StartupGroups-win-Setup.exe` once to land on the modern track.

**What's in (post-v0.2.11):**

- Velopack code shipped (`UpdateService.cs`, `Program.cs` owning Main, `VelopackApp.Build().Run()` before WPF init).
- Velopack pinned to `0.0.1589-ga2c5a97`. **Do not downgrade to 0.0.1297** — that build sends a literal `Authorization: Bearer` header with an empty token even when `accessToken` is null, which GitHub rejects with 401. The fix landed in 0.0.1298+.
- `CleanHttpClientFileDownloader` still ships as defense-in-depth (disables proxy / default creds / cookies on the underlying `HttpClientHandler`). It is **not** what fixed the original 401 — that was the Velopack bug — but it's harmless and protects against future Windows-level interception.
- `CachelessGithubSource` (`Services/CachelessGithubSource.cs`) overrides `GithubSource.GetReleases` to hit `/releases/latest` instead of `/releases`. **The `/releases` listing endpoint is served via Fastly with 15–30 min staleness for anonymous clients**; new releases vanish from it for the cache window. `/releases/latest` is fresh. Without this workaround, every shipped user (we don't ship a token) would miss new updates for that lag.
- Release workflow produces Velopack assets (`Setup.exe`, `releases.win.json`, full nupkg, `RELEASES`, `Portable.zip`) and an MSI side-by-side. Order matters — see "Workflow gotchas". MSI track is preserved for Phase 3 / enterprise.
- Update UX:
  - Progress bar (height 14) with live `X.X / Y.Y MB (Z%)` text during download (`MainWindowViewModel.FormatDownloadProgress` + `UpdateProgressText`).
  - After Velopack restarts the process post-update, the main window opens automatically (otherwise our launcher app would only restart the tray icon). Implemented via `restartArgs: ["--restarted-after-update"]` passed to `ApplyUpdatesAndRestart`, detected in `App.OnStartup`.
- CI optimizations: CodeQL switched to `ubuntu-latest` with `build-mode: none` (5–8 min → 1–2 min). NuGet caching via `setup-dotnet`. `paths-ignore` on docs-only changes.
- Tests stabilized: `DetectAsync_FastestProbeWins` margin widened 10× → 200× for contended CI; `DetectAsync_CancelsOtherProbes_WhenOneFires` awaits a `TaskCompletionSource` before asserting `WasCancelled`.

**Still open (Phase 2+ candidates):**

- Per-file binary deltas. Pass `--releases <prior>` to `vpk pack` so updates ship as ~few-MB diffs instead of full 84 MB. Currently every update is a fresh full nupkg.
- "First-update-after-fix" gotcha. Any UI/protocol change for the update flow takes effect only on the *next* update — the running version drives the download. Add a release-notes hint when something material changes.
- Release Drafter sub-job flakes intermittently with GitHub GraphQL "Something went wrong" — purely cosmetic (release notes are generated separately at tag time via `gh api .../releases/generate-notes`). Soft-fail or remove as discussed.

### Workflow gotchas (release.yml ordering — both sequences have a known failure mode)

The release workflow has been through three iterations because of subtle interactions between `gh release create`, `vpk upload --merge`, and `release-drafter`. **Read this before changing release.yml.**

**Sequence A: `gh release create` first, then `vpk upload --merge`** — caused **duplicate releases**. `gh release create` publishes immediately; `vpk upload --merge` then sees the existing published release but creates a new draft with the Velopack assets alongside it instead of merging. Fix: never use this order.

**Sequence B: `vpk upload` first (no `--merge`), then `gh release upload` for the MSI** — fails when `release-drafter` has auto-created a draft for the same tag, because vpk refuses with `[FTL] There is already an existing release tagged 'vX.Y.Z'`. Fix: delete any pre-existing release for the tag *before* `vpk upload` runs (PR #20, merged).

**Current sequence (post-PR #20, post-PR #23):**

1. Build MSI (single-file payload, separate publish dir).
2. Publish for Velopack (multi-file, separate publish dir).
3. `vpk pack`.
4. **Delete any existing release for this tag** (will hit release-drafter's draft).
5. `vpk upload github` (creates a new draft with all Velopack assets).
6. `gh release upload --clobber` to append the MSI to that draft.
7. Generate notes via `gh api repos/.../releases/generate-notes`, write to a temp file, then `gh release edit --draft=false --notes-file <file>` to publish.

**Why step 7 is roundabout**: `gh release edit` does **not** accept `--generate-notes` (only `gh release create` does). Discovered when v0.2.3's release got stuck as a draft after the build succeeded. PR #23 fixed it by generating notes via the API and passing them via `--notes-file`.

**Step 4 has a known intermittent hang.** `gh api repos/.../releases` (the same listing endpoint Velopack avoids — see CachelessGithubSource) sometimes hangs for 5+ minutes due to GitHub server-side cache misbehavior. It eventually completes successfully. If a release run is "stuck on Remove any existing draft", just wait — don't cancel.

**If you change this**, mind the constraint: vpk requires no pre-existing release with the tag, but release-drafter creates one for every PR merged into main. Future-proof by always running the delete step.

### Battle scars (one-line summaries — useful when something inevitably breaks)

| Symptom | Cause | Fix |
|---|---|---|
| Update check returns 401 from GitHub via Velopack | **Velopack 0.0.1297 bug** — `GithubSource.GetReleases` sends `Authorization: Bearer` with empty token even when `accessToken` is null. GitHub correctly rejects. Fixed upstream in 0.0.1298+ with an `IsNullOrEmpty` guard. | Pin Velopack to `0.0.1589-ga2c5a97` or later. The `CleanHttpClientFileDownloader` proxy bypass was suspected but is **not** the actual fix; we keep it as defense-in-depth. |
| Update check returns "no updates" but a newer release is on GitHub | **GitHub's anonymous `/releases` listing endpoint** is served via Fastly with 15–30 min staleness. Default `GithubSource` enumerates `/releases` so anonymous clients (us — we ship no token) miss new releases. `/releases/latest` and `/releases/tags/<tag>` are fresh; only the listing has the stale cache. | `CachelessGithubSource` overrides `GetReleases` to call `/releases/latest` instead. Use this in `UpdateService.cs`, never the default `GithubSource`. |
| App restarts after update but goes straight to tray, no main window | Our launcher app is tray-resident; `OnStartup` doesn't open the main window unless the user clicks the tray icon. Velopack's `ApplyUpdatesAndRestart` doesn't pass a "this was an update" signal by default. | Pass `restartArgs: ["--restarted-after-update"]` to `ApplyUpdatesAndRestart` (constant lives on `VelopackUpdateService`). Detect in `App.OnStartup` via `e.Args.Any(...)` and call `_trayViewModel.ShowMainWindowCommand.Execute(null)`. |
| Progress bar shows fill but no numeric data | Default WPF `ProgressBar` doesn't render text. | Bind a separate `TextBlock` to `UpdateProgressText` (set in `Progress<int>` callback via `FormatDownloadProgress(percent, totalBytes)`). `totalBytes` flows through via `UpdateCheckResult.DownloadSizeBytes` from `info.TargetFullRelease.Size`. |
| Release publishes assets but stays as a draft | `gh release edit --generate-notes` — that flag exists only on `gh release create`. The build succeeded but the publish step errored. | Generate notes via `gh api repos/.../releases/generate-notes` and pass them via `--notes-file`. PR #23. |
| Release workflow hangs on "Remove any existing draft for this tag" | `gh api repos/.../releases` listing intermittently slow on GitHub's side (same root cause as the cache lag). | Wait. It always completes eventually (5–10 min observed). Do not cancel. |
| Two releases per tag (one published with MSI, one draft with Velopack assets) | `gh release create` + `vpk upload --merge` interaction | Remove `gh release create`. Let vpk own release creation. |
| `vpk upload` fails with "release already exists" | release-drafter created a draft on PR merge | Delete pre-existing release for the tag in workflow before vpk runs |
| `Velopack 0.0.1295 was not found` during NuGet restore | NU1603 treated as error because `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` | Pin to a version that exists. We use 0.0.1297. Bump deliberately. |
| Update check threw → `Last checked at` never updates in UI | `CheckAsync` returned null on exception → ViewModel early-returned without setting `LastChecked` | Always return a `UpdateCheckResult` with `CheckedAt` set, even on error |
| Setup.exe says "Already installed, Repair/Cancel" on a fresh machine | Velopack detects residual files in `%LocalAppData%\StartupGroups\` from prior MSI install | Click Repair — does a fresh install. Same per-user data folder is used by both installers. |
| `release-drafter@v6` errored with `target_commitish=refs/pull/N/merge` | v6 monolithic action couldn't be told to skip release update on PR events | Migrate to v7 split-action: separate `release-drafter/autolabeler@v7` and `release-drafter/drafter@v7` jobs, gated by event |
| `Page App.xaml` was duplicate after switching from `ApplicationDefinition` to own `Main` | WPF SDK auto-includes XAML files as Pages. Explicit `<Page Include>` duplicates. | Don't add explicit Page item — SDK handles it |
| `actions/checkout@v6` PRs from Dependabot blocked auto-merge with "workflow permission" | Default `GITHUB_TOKEN` can't modify `.github/workflows/*.yml` files | Manually merge with user PAT, or just live with it (one PR per Dependabot bump that touches workflows) |
| `Validate PR Title` job SKIPPED on Dependabot PRs blocks merge | Required check + skipped conclusion = blocked merge | Move `if:` from job-level to step-level so the job still reports SUCCESS |
| FluentAssertions 8.x unavailable — license issue | 8.x uses Xceed Fair-Source (non-OSI, not free for commercial use) | Pinned to 7.x in `.github/dependabot.yml` ignore rules. Don't accept Dependabot major bumps for FluentAssertions. |

### Phase 1 — Velopack MVP on stable channel (~1–2 days)

Replace the custom updater with Velopack while keeping the existing MSI build alive.

**Deliverables:**
- Add `Velopack` NuGet package to `StartupGroups.App`
- Add `VelopackApp.Build().Run()` at the top of `App.OnStartup` (lets the runtime hook install/uninstall/firstrun events)
- Replace [src/StartupGroups.App/Services/UpdateInstaller.cs](src/StartupGroups.App/Services/UpdateInstaller.cs) and [UpdateChecker.cs](src/StartupGroups.App/Services/UpdateChecker.cs) with `Velopack.UpdateManager` calls
- Add `vpk` CLI step to [.github/workflows/release.yml](.github/workflows/release.yml): after `dotnet publish`, run `vpk pack` then `vpk upload github`
- Keep the WiX MSI build alongside as a parallel artifact (don't delete — needed for Phase 3)

**Validation before moving on:**
- A vX → vY update applies a delta of < 30 MB for a code-only change
- App relaunches in ~2 sec after update completes
- Previous version preserved in `LocalAppData\StartupGroups\packages\` for rollback
- Rollback works when forced (deliberately corrupt a delta to test)

**Migration risk: existing MSI users won't auto-migrate to Velopack.** They need to uninstall the MSI and run the new `Setup.exe` once. Plan a transition release that displays an in-app banner pointing to the new installer. Cheap to do now while the user base is small.

### Phase 2 — Channels + branded in-app update flyout (~1 day)

This is where the polish ROI is highest — every update goes through this UI.

**Deliverables:**
- CI workflow matrix:
  - Tag `v*` (no prerelease suffix) → `vpk pack --channel stable`
  - Tag `v*-beta.*` → `vpk pack --channel beta`
  - Daily cron / push-to-main → `vpk pack --channel nightly`
- Add **Channel** picker to Settings (Stable / Beta / Nightly)
- On change: `new UpdateManager(feed, new UpdateOptions { ExplicitChannel = chosen, AllowVersionDowngrade = true })`. **The `AllowVersionDowngrade` flag is critical** — without it, beta-to-stable users get stuck on a higher beta version.
- Replace the current update toast with a proper WPF flyout:
  - Hero header — "Update available · v0.X.Y"
  - Markdown-rendered release notes pulled from the GitHub release body
  - Progress bar driven by `IProgress<int>` from `DownloadUpdatesAsync`
  - Speed and ETA text below the bar
  - Defer / Update Now buttons
  - Dark/light following Windows

**Validation:**
- Switching stable → nightly downloads the latest nightly even when SemVer is lower
- Switching back to stable downgrades cleanly with `AllowVersionDowngrade`
- Release notes render correctly with code blocks, lists, links

**Risk: GitHub anonymous API rate limit dropped to 60 req/hour in May 2025.** Cache `releases.<channel>.json` locally for 1 hour and add a manual "Check now" button. Asset downloads aren't rate-limited.

### Phase 3 — Burn bundle wrapper with custom WPF managed BA (~3–5 days)

The "modern Fluent installer" experience. Visual Studio Installer pattern: Burn handles install correctness, our WPF app draws the UI.

**Architecture:**

```
Setup.exe (Burn bundle)
├── StartupGroups.Installer.UI.exe (managed BA — WPF + WPF-UI Fluent)
│   ├── Welcome screen
│   ├── License screen (MIT, scroll-to-accept)
│   ├── Optional "Customize" screen (install path, channel picker, auto-start checkbox)
│   ├── Progress screen (Fluent ProgressBar, animated icon, current operation)
│   └── Success screen (checkmark, "Launch StartupGroups" button)
└── StartupGroups.msi (existing MSI — installed under the hood by Burn)
```

**Deliverables:**
- New project `installer/StartupGroups.Bundle/Bundle.wxs` — Burn bundle definition referencing the existing MSI as a chained package
- New project `src/StartupGroups.Installer.UI/` — WPF self-contained EXE
  - Inherits from `BootstrapperApplication` (`WixToolset.Mba.Core`)
  - WPF-UI Fluent theme matching the main app's color palette, typography, accents
  - Mica background on Windows 11 / acrylic fallback on Windows 10
  - Dark/light following Windows
  - Five screens above as separate UserControls
  - Burn engine progress events plumbed into WPF view-models
- Build script produces `Setup.exe` (Burn bundle EXE containing the BA + MSI)
- Update [.github/workflows/release.yml](.github/workflows/release.yml) to attach `Setup.exe` as the primary download asset; keep `StartupGroups.msi` as a secondary asset for IT
- Update [README.md](README.md) install section to point at `Setup.exe`

**Reusability bonus:** the same `Setup.exe` handles install, **repair**, **uninstall**, and **change features** — all flow through the same WPF UI. Re-running it after install offers a "What would you like to do? Repair / Uninstall / Cancel" modal.

**Validation:**
- `Setup.exe` launches and shows Mica/Fluent UI matching the main app
- Installs to Program Files (per-machine) and registers Velopack metadata for future per-user updates
- Re-running `Setup.exe` after install offers Repair / Uninstall flow
- Uninstall properly removes app, Velopack metadata, Start Menu shortcut, registry entry
- Dark mode follows Windows
- Keyboard navigation, screen-reader labels, tab order all functional

**Risks:**
- Managed BAs are the less-trodden WiX path; `IBootstrapperApplication` interop docs are thinner than `WixStdBA`. Plan for some debugging time.
- Bugs in the BA can wedge installs (loop forever, deadlock UI thread). Robust error handling + a "fallback to text-mode UI" path for catastrophic BA failures.
- Self-contained WPF BA adds ~70 MB to `Setup.exe`. Acceptable — first install is one-time, and Velopack delta updates take over from there.

### Phase 4 — Polish and post-launch (ongoing)

After the architecture is in place, the small wins:

- **Non-elevated post-install launch** — the current MSI's `LaunchApp` custom action with `Impersonate="yes"` inherits the elevation token from msiexec. Switch to `WixShellExec` so the launched app runs under the shell's unelevated token regardless of how msiexec was invoked.
- **Optional .NET runtime chaining via Burn** — instead of bundling the 250 MB self-contained runtime, Burn can chain a .NET 10 Desktop Runtime install. Turns `Setup.exe` into a tiny ~10 MB downloader. Worth it only if first-install download size becomes friction.
- **`winget` manifest** — point at the GitHub release `Setup.exe`. Free `winget upgrade` from the OS.
- **Portable ZIP** as a third release asset for power users who don't want any installer at all.
- **Code signing** — when added, both Velopack (`vpk pack --signParams "..."`) and the Burn bundle (`signtool` over the EXE) support it natively. Azure Trusted Signing is currently the cheapest route.

### Migration & rollout

| Step | What ships | What users see |
|---|---|---|
| 1 | v0.2.0: Velopack + new `Setup.exe`, old MSI auto-update message | Existing v0.1.x MSI users see in-app banner: "v0.2.0 changes how updates work — please uninstall and reinstall via the new Setup.exe at https://..." |
| 2 | v0.2.x onward (Velopack deltas) | Updates happen silently in-app; no UAC, no MSI flicker. The Phase 2 WPF flyout is the visible UI |
| 3 | v0.3.0: managed BA Fluent installer | Existing v0.2.x users continue auto-updating via Velopack — they don't see the new Setup.exe unless they reinstall. New downloads see the Fluent first-install experience |

### Open questions / risks (consolidated)

- **Velopack is 0.0.x.** Pre-1.0 versioning, but production-stable (maintainer has shipped Squirrel → Clowd.Squirrel → Velopack continuously since ~2014). Pin `vpk` tool version in CI to avoid surprise breaking changes.
- **GitHub Releases as feed source long-term.** Fine for current scale; switchable to S3 / B2 / Cloudflare R2 later by changing the feed URL passed to `UpdateManager`.
- **First-install download size doesn't shrink with this plan.** Velopack helps with *updates*. The Burn-chained .NET runtime install (Phase 4) is the lever if first-install size becomes a real friction point.
- **Channel switch + `AllowVersionDowngrade`** is opt-in. Forget the flag and beta-to-stable switchers get stuck. Add a unit test that constructs the `UpdateManager` with the right options.

## Feature gaps for v1.0

Things a polished launcher should have that we haven't built yet.

| Feature | Why | Effort |
|---|---|---|
| CLI args (`--launch-group "Work"`) | Trigger groups from shortcuts, scripts, Task Scheduler | Small |
| Toast notifications on launch complete/failed | Status feedback without alt-tabbing | Small |
| Per-app run-as-admin flag | Currently no way to elevate an individual app | Small |
| Multi-instance prevention | App can be launched twice currently | Small (named mutex) |
| Conditional launch (only if service X running, on certain networks, etc.) | Flexible orchestration | Medium |
| Scheduled launches (run group at time T / on event) | Overlaps with Task Scheduler but in-app is nicer | Medium |
| Pre/post launch hooks (custom scripts) | Power-user flexibility | Medium |
| Backup/restore for benchmark SQLite DB | Don't lose history across reinstalls | Small |
| In-app log viewer | Logs are written to file but not surfaced in the UI | Small |
| Import/export groups (JSON) | Config is JSON on disk but no UI for it | Small |
| Telemetry opt-in/opt-out toggle in Settings | Service exists but isn't exposed | Small |

The headline trio if I had to pick three to close before calling it 1.0: **CLI args**, **toast notifications**, and **finishing the update + installer architecture** above.

## CI / repo hygiene

- **Auto-prune old releases** — workflow that keeps the latest N releases and deletes binaries from older ones (preserving tags). Worth setting up at ~20+ releases. Uses `dev-drprasad/delete-older-releases`.
- **Code signing** — not planned for now. Staying free OSS, accepting the SmartScreen warning on first install. Revisit only if downloads become a real friction point. Both Velopack and the Burn bundle support signing natively when ready.

## Decisions already locked in (for future-me reference)

- **License**: MIT.
- **FluentAssertions pinned to 7.x** — 8.x switched to a non-OSI license (Xceed Fair-Source) that isn't free for commercial use. Dependabot ignore rule in [.github/dependabot.yml](.github/dependabot.yml).
- **Squash-only merging** with PR title + body as the squash commit message; conventional commits enforced via PR title lint.
- **Update mechanism**: Velopack — per-user, deltas, channels. Existing custom `UpdateInstaller.cs` removed in Phase 1.
- **Installer UI**: Custom WPF + WPF-UI Fluent managed Bootstrapper Application via WiX Burn. Visual Studio Installer pattern. Rejected `WixStdBA` even with theming — limited to WiX's own widget set, can't do Fluent / Mica / dark-mode-following.
- **Per-machine vs per-user**: Hybrid. `Setup.exe` installs the MSI per-machine (Program Files); Velopack handles per-user auto-updates from there.
- **Code signing**: Not planned now, accepted SmartScreen warning. Both tools in the stack support signing when added later.
