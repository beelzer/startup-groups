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

> Releases are coming. For now, build from source.

## Build from source

Requirements:
- **Windows 10/11**
- **.NET 10 SDK** (see [global.json](global.json))
- **WiX 4** (only needed if building the MSI)

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

## License

[MIT](LICENSE) © 2026 beelzer
