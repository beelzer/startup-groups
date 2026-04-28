# Startup Groups

A Windows launcher that boots groups of apps in the right order — with adaptive readiness detection, wave-parallel orchestration, and built-in launch benchmarking.

> **Stop waiting on Slack to be ready before clicking your IDE.** Define a group, hit launch, and your full work environment comes up in the right order without you babysitting it.

![Startup Groups main window](docs/screenshots/main-window.png)

## Features

- **Group your apps** — define named groups (e.g. *Work*, *Gaming*, *Streaming*) with the apps that belong together.
- **Wave-parallel launch** — apps with no dependency launch concurrently. `DelayAfterSeconds > 0` closes a wave; the next wave waits for `max(time_until_all_ready, delay)`.
- **Adaptive readiness detection** — four parallel probes per app (input-idle, main-window-found, CPU/IO quiet, service-running). First-wins, no per-app config required.
- **Launch benchmarking** — every launch is timed and stored. Cold-vs-warm tracking, bottleneck analysis, history view in-app.
- **Drag-to-reorder** apps within a group.
- **Tray-resident** with quick-launch from the system tray.
- **Auto-start with Windows** via Task Scheduler (elevation-aware).
- **Windows Startup tab** to inspect/edit existing Registry `Run` keys and Task Scheduler entries.
- **Service control** — start/stop Windows services as part of a group, via a dedicated UAC-elevated helper.
- **Themes** — system / light / dark, Fluent (WPF-UI).
- **Localized** — 10 languages out of the box (en, fr, de, ja, ru, ar, he, th, hi, more).

## Install

Download **`StartupGroups-Setup.exe`** from the [releases page](https://github.com/beelzer/startup-groups/releases/latest) and run it.

The installer is a Mica-themed Fluent setup wizard (Welcome → License → Progress → Success, with optional Customize for picking the update channel + auto-start). Once installed, in-app updates are silent — no UAC, no installer flicker.

For IT departments and silent deployment, a vanilla **`StartupGroups-<version>.msi`** is also published on every release.

> The installer is unsigned, so Windows SmartScreen will show a warning on first install. Click **More info** → **Run anyway**.

## Build from source

Requirements:
- **Windows 10/11**
- **.NET 10 SDK** (see [global.json](global.json))
- **WiX 5** (only needed if building the MSI or the Burn bundle)

```powershell
# clone
git clone https://github.com/beelzer/startup-groups.git
cd startup-groups

# build
dotnet build StartupGroups.slnx -c Release

# run the WPF app
dotnet run --project src/StartupGroups.App -c Release

# run the test suite
dotnet test
```

To build the MSI installer:

```powershell
./installer/StartupGroups.Installer/build.ps1
```

To build the Burn bundle (`Setup.exe`) — wraps the MSI with the Mica/Fluent installer UI:

```powershell
# Run after the MSI build above; the bundle chains the produced .msi.
./installer/StartupGroups.Bundle/build.ps1
```

## Architecture

Three projects under [src/](src/):

| Project | Role |
|---|---|
| **StartupGroups.App** | WPF UI (WPF-UI Fluent theme, CommunityToolkit.Mvvm). Views, ViewModels, tray, settings, drag-reorder. |
| **StartupGroups.Core** | Domain model, launch orchestration, Win32 interop, readiness probes, SQLite benchmark store, JSON config. |
| **StartupGroups.Elevator** | Tiny admin helper invoked via UAC for privileged ops (service start/stop, machine-scope Run keys). |

Tests live in [tests/StartupGroups.Core.Tests](tests/StartupGroups.Core.Tests/) (xUnit + FluentAssertions).

### Tech stack

.NET 10 · WPF · WPF-UI · CommunityToolkit.Mvvm · Serilog · Microsoft.Data.Sqlite · WiX 4

## Cutting a release

1. Bump `<Version>` in [Directory.Build.props](Directory.Build.props).
2. Commit and push to `main`.
3. Tag the commit with a matching `vX.Y.Z` and push the tag:

   ```bash
   git tag v0.2.0
   git push origin v0.2.0
   ```

4. The [release workflow](.github/workflows/release.yml) will build the MSI, run the tests, and publish a GitHub release with the installer attached. Auto-update will pick it up on the next check.

## Roadmap

See [ROADMAP.md](ROADMAP.md) for in-flight work, planned features, and migration notes.

## License

[MIT](LICENSE) © 2026 beelzer
