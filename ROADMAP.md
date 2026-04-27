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
