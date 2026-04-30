# Distribution migration plan: Velopack/Burn → MSIX

Pivoting StartupGroups distribution from Velopack/Burn to MSIX as the single
packaging format. Goal is to ship a single signed MSIX through GitHub direct
download, Microsoft Store, winget, Chocolatey, and Scoop.

## End product

A single signed MSIX shipping through five paths. We architecturally support
all five from day one — the MSIX builds work for all of them — but only
GitHub direct download is plumbed at launch. The rest light up incrementally
as approvals roll in.

| Path | Status at launch | Goes live when |
|---|---|---|
| **GitHub direct download** | **Live** | Phase 1 — cert in hand, signed MSIX uploaded |
| Microsoft Store | Architecturally supported | Partner Center cert review approves (~24-72h after submission) |
| winget | Architecturally supported | `microsoft/winget-pkgs` manifest PR approved (~24h) |
| Chocolatey | Architecturally supported | Community moderation approves (days-to-weeks; deferrable) |
| Scoop | Architecturally supported | Bucket entry added (no approval gate) |

For each non-GitHub channel, going live is **manifest work** (submit a PR or
create a bucket), not engineering work. The same signed `.msix` binary
serves all five.

## What "live" looks like at Phase 1 launch

- README has a "Download for Windows" link pointing at the latest
  `.appinstaller` URL on GitHub releases
- User clicks → Windows App Installer (the OS-provided Mica wizard) opens →
  app installs
- Updates apply silently between launches via `.appinstaller`'s `<OnLaunch>`
  settings
- "Check for updates" flyout in Settings still works for users who want to
  see release notes
- One ARP entry, owned by Windows
- Codebase has zero Burn, zero Velopack, zero MSI — pipeline is MSIX-only

## Locked decisions

| # | Decision | Rationale |
|---|---|---|
| 1 | Drop the canary channel | Pre-1.0 single-dev project; bleeding-edge users build from source |
| 2 | Drop the `.msi` admin track | MSIX is enterprise-deployable via Intune/SCCM/MDM in 2026 |
| 3 | Skip the WPF launcher refactor | All problems it solved disappear under MSIX |
| 4 | Single MSIX identity: `StartupGroups` | No channel suffix |
| 5 | SignPath Foundation primary, Certum fallback | Free if approved; ~€85 + €30/yr if not |
| 6 | Stage migration in 4 phases | Continuous dogfooding, no signing block |

## Architecture

- **Same source tree builds two MSIX variants**: a "Sideload" build signed
  with our own cert, a "Store" build with Microsoft's cert (Microsoft signs
  during certification). Difference is one MSBuild dimension and one
  manifest field — `Publisher=`. All other behaviour identical.
- **Full-trust desktop bridge** (`Windows.FullTrustApplication` entry point
  + `runFullTrust` capability) keeps existing `%AppData%\StartupGroups\`
  paths working. No settings migration. WPF + Mica + WPF-UI all keep
  working as-is.
- **In-app update flyout preserved**: Mica panel, GitHub-style release
  notes (`#NN`, compare links, `@username`), ESC closes, View on GitHub
  button. The flyout's polish work isn't thrown away.
- **`.appinstaller` XML** at GitHub releases drives transparent background
  updates for sideloaded installs (`<OnLaunch HoursBetweenUpdateChecks="8"/>`).
  User experience: app just gets newer between launches.

## Sizing the change

**Adds**: ~500 lines
- `installer/Msix/StartupGroups.Package.wapproj`
- `installer/Msix/Package.appxmanifest`
- One `msix-build` CI job + one `msix-release` CI job
- A small `.appinstaller` template
- A few hundred lines in `UpdateService` swapping Velopack for Store/MSIX APIs

**Removes** (Phase 3 cleanup): ~1500-2500 lines
- `installer/StartupGroups.Bundle/` (Burn)
- `installer/StartupGroups.Installer/` (MSI)
- `src/StartupGroups.Installer.UI/` (Burn BA + 5 views)
- `UpdateChannel` enum + every reference (settings, marker file, flyout subtitle, Settings dropdown)
- `App.xaml.cs.TrySeedChannelFromRunningVersion` + `.channel-seeded` marker
- Velopack NuGet + Velopack code in `UpdateService`
- `MsiMessageFilter` + tests, `DownloadSpeedTracker`
- All canary publish steps + MSI build steps in CI

Net: codebase gets meaningfully smaller despite gaining a new packaging format.

## Phases

### Phase 0 — Build the pipeline (now → ~2 weeks)

You keep using current canary builds for personal dogfooding. The MSIX
pipeline gets built in parallel.

**Engineering work**:
- Add `installer/Msix/` with `.wapproj` + `Package.appxmanifest`
- Add CI job producing unsigned `.msix` artifact (downloadable from Action
  run for verification)
- Parameterise the signing step so cert arrival is a "flip a secret on" event
- Smoke-test the elevated helper (`StartupGroups.Elevator.exe`) inside the
  packaged container — highest-risk piece
- Refactor `UpdateService` to call MSIX/`PackageManager` APIs (alongside
  Velopack via build flag)

**External work** (wall-clock, non-blocking):
- Apply to SignPath Foundation — free OSS signing, 2-6 week review
- Reserve "Startup Groups" in Partner Center — free for individuals,
  1-3 day identity reservation. Note the Publisher ID.

**Output**: downloadable unsigned MSIX you can install via Windows
Developer Mode for verification. Not yet user-facing.

### Phase 1 — GitHub direct download live (when cert lands)

**Trigger**: SignPath approves *or* you decide to buy Certum (~€100 +
€30/yr). Both are 2-6 weeks wall-clock from Phase 0 day 1.

**Work**:
- Add cert to GitHub Actions secrets
- Flip the signing step on (already parameterised from Phase 0)
- Generate `.appinstaller` XML at release time
- First signed release uploads `.msix` + `.appinstaller` to GitHub release
- Update README with "Download for Windows" link

**Output**: anyone with Windows can download from GitHub releases and
install. Updates apply silently in background. **This is the launch.**

### Phase 2 — Add channels incrementally (parallel, no engineering blockers)

Each channel comes online independently as its approval/submission
completes. None block any other.

- **Microsoft Store** (~24-72h cert review, free): rebuild MSIX with Store
  `Publisher=`, submit `.msixupload` to Partner Center, await cert review.
  Add "Get it from Store" badge once live.
- **winget** (~24h after submit, free): add WinGet Releaser GitHub Action
  — auto-PRs to `microsoft/winget-pkgs` on every release.
- **Chocolatey** (days-to-weeks first time, free): write `.nuspec` +
  `chocolateyInstall.ps1`, submit to community.chocolatey.org. Defer until
  there's user demand.
- **Scoop** (no gate): create your own bucket repo with a manifest. Lowest
  priority.

You can do these in any order, or skip any of them. Each is a 1-4 hour task
per channel.

### Phase 3 — Legacy cleanup (1-2 releases after Phase 1 stable)

One large PR retires the legacy stack:
- `installer/StartupGroups.Bundle/` (Burn)
- `installer/StartupGroups.Installer/` (MSI)
- `src/StartupGroups.Installer.UI/` (Burn BA + 5 views)
- `UpdateChannel` enum + every reference + the marker file logic
- Velopack NuGet + Velopack code in `UpdateService`
- `MsiMessageFilter` + tests, `DownloadSpeedTracker`
- All canary publish steps in `ci.yml`
- All MSI build/upload steps in `release.yml`

Pipeline becomes MSIX-only. Codebase shrinks by ~1500-2500 lines net.

## Wall-clock dependencies

- **SignPath approval**: 2-6 weeks. Apply day 1.
- **Partner Center identity**: 1-3 days. Do day 1.
- **Certum cert** (only if SignPath fails): 1-2 weeks.
- **Store cert review** (Phase 2): 24-72h on first submission.
- **winget manifest approval** (Phase 2): <24h after submit.

None of this blocks Phase 0 from starting.

## Top risks

| Risk | Likelihood | Mitigation |
|---|---|---|
| Elevated helper breaks under MSIX container | Medium | Smoke-test in Phase 0, fix early. Usually absolute paths. |
| SignPath rejection | Medium | Buy Certum (~€100 + €30/yr) |
| Settings loss when MSIX uninstall wipes per-package AppData | Medium | Add in-app "Export settings"; document migration |
| SmartScreen reputation reset on first signed binary | High | Expected; reputation builds over weeks regardless of cert tier |

## Key references

- [Microsoft sample: github-actions-for-desktop-apps](https://github.com/microsoft/github-actions-for-desktop-apps)
- [PowerToys appxmanifest.xml](https://github.com/microsoft/PowerToys/blob/main/installer/MSIX/appxmanifest.xml)
- [App Installer auto-update overview](https://learn.microsoft.com/en-us/windows/msix/app-installer/auto-update-and-repair--overview)
- [WinGet Releaser action](https://github.com/vedantmgoyal9/winget-releaser)
- [Velopack Setup.exe CLI source](https://github.com/velopack/velopack/blob/develop/src/bins/src/setup.rs) (the path we're leaving behind)
