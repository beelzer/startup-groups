# Roadmap

Living list of work that's been thought about but isn't done yet. Items aren't commitments — they're notes-to-future-self with the reasoning preserved so we don't relitigate decisions.

## Auto-update flow

Current path is the classic Windows update pattern: download MSI from the latest GitHub release → `msiexec /i` (UAC) → app exits → WiX MajorUpgrade replaces files → relaunch. It works, but there's room to polish and eventually a better architecture to migrate to.

### Polish (small wins on the current MSI flow)

- **Silent install** — pass `/quiet` to `msiexec` so users don't see the WiX UI on every update. Roughly a 1-line change in `UpdateInstaller.LaunchInstallerAndExit`.
- **SHA256 verification** — publish a hash with each release; verify the downloaded MSI before exec. Defense-in-depth even with HTTPS.
- **Temp file cleanup** — wipe stale `%TEMP%\StartupGroups-Update-*.msi` from prior runs at app startup. ~10 LOC.
- **Inline release notes** — show "What's new in X.Y.Z" inside the app before downloading instead of just the "See what's new" link to GitHub.

### Major: migrate to Velopack

[Velopack](https://github.com/velopack/velopack) is the modern Squirrel.Windows successor — actively maintained, .NET-first, what Slack/Discord/GitHub Desktop-style apps use. Replacing our manual MSI flow with it would buy us:

- **Delta updates**: 1–10 MB per update instead of 256 MB. Client computes file-level diffs against the current install.
- **No UAC for updates** — per-user install in `%LocalAppData%\Programs\StartupGroups`.
- **Channels** (stable / beta / nightly) built-in via `--channel` flag in `vpk pack` / `vpk upload`. Users could opt into nightly in Settings.
- Code-signing integration.
- Cross-platform (Windows / macOS / Linux).

**Tradeoffs:**
- Per-user install only — no Program Files, no SCCM/Intune corporate deployment.
- The current WiX MSI work isn't wasted: most projects keep both — Velopack as the primary user-facing path, MSI as an "Enterprise" download for IT departments.

**Effort:** ~1–2 days. NuGet packages + `vpk pack` step in CI + replace `UpdateInstaller.cs` with `UpdateManager` calls + adjust release workflow to publish via `vpk upload github`.

**When:** After the simple flow is proven stable. v0.2 or v0.3 timeframe.

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

The headline trio if I had to pick three to close before calling it 1.0: **CLI args**, **toast notifications**, **finishing the auto-update install path** (already in flight).

## Installer

### Polish

- **Branded BMPs** — replace WiX stock red dialog images with BMPs derived from `branding/app-icon.svg`. Two files: `Banner.bmp` (493×58) and `Dialog.bmp` (493×312), referenced via `<WixVariable Id="WixUIBannerBmp" />` etc.
- **Non-elevated post-install launch** — the current `LaunchApp` custom action with `Impersonate="yes"` inherits the elevation token from msiexec. Switch to `WixShellExec` so the launched app runs under the shell's (always unelevated) token regardless of how msiexec was invoked.

### Size (currently 256 MB)

It's the cost of a self-contained .NET 10 runtime + WPF + ReadyToRun. Options if it ever becomes a problem:

- **Disable ReadyToRun** (`PublishReadyToRun=false`) — saves ~80 MB. Costs ~50–150 ms cold start. Probably not worth it for a launcher where startup speed matters.
- **Framework-dependent** — saves ~250 MB but requires users to install .NET 10 Desktop Runtime separately (~50 MB second download). UX hit.
- **WiX Burn bootstrapper** — chains runtime install with app install transparently. Best UX, biggest engineering lift (~days, requires a Burn project + custom BAL).

Migrating to Velopack would also help here (delta updates).

## CI / repo hygiene

- **Auto-prune old releases** — workflow that keeps the latest N releases and deletes binaries from older ones (preserving tags). Worth setting up at ~20+ releases. Sketch in chat history; uses `dev-drprasad/delete-older-releases`.
- **Code signing** — not planned. Staying free OSS, accepting the SmartScreen warning on first install. Revisit only if downloads become a real friction point.

## Decisions already locked in (for future-me reference)

- **License**: MIT.
- **FluentAssertions pinned to 7.x** — 8.x switched to a non-OSI license (Xceed Fair-Source) that isn't free for commercial use. Dependabot ignore rule in `.github/dependabot.yml`.
- **Squash-only merging** with PR title + body as the squash commit message; conventional commits enforced via PR title lint.
- **No corporate MSI track** for now — single user-facing distribution. Will reconsider if Velopack migration creates an actual MSI/Velopack split.
