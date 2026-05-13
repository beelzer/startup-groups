# Rename plan: StartupGroups → Salvo

Authored 2026-05-14. Plan only — execute after context compact.

## Decision summary

The project is being rebranded from **Startup Groups** to **Salvo** before the MSIX distribution pipeline goes live publicly. See memory file `project_rename_to_salvo.md` for the why; this doc is the *how*.

**Locked trade-offs:**
- No backwards-compat for existing canary users. They reinstall.
- Full rename — folders, csprojs, namespaces, branding strings, CI references. Cleanest end state.
- New AppId, new MSI/Bundle UpgradeCodes, new Velopack identity — clean break.

**Out of scope for this PR:**
- Logo redesign (keep existing app.ico / app.png; same visual mark just labelled "Salvo").
- New domain purchase. Plan recommends `salvo.app` but that's a separate user action.
- Partner Center / SignPath applications (apply under "Salvo" identity *after* this rename merges).

## Order of operations

1. Check out `rename/salvo` branch (already exists with this plan doc committed).
2. Build the rename in the order below — each section can be committed separately for reviewability or all-at-once.
3. Verify with `dotnet build` + `dotnet test` + local MSIX build at the end.
4. Open PR. Merge after green CI.
5. **After merge**: rename the GitHub repo `startup-groups` → `salvo` via repo Settings. Update local remote URL with `git remote set-url`.

## Open decisions to confirm with user before starting

- [ ] **Company / Publisher display name**: keep "Salvo" as Company too, or use a personal name (e.g. user's name) for the MSIX `<PublisherDisplayName>` and copyright?
- [ ] **AssemblyName / EXE filename**: rename `StartupGroups.exe` → `Salvo.exe`? Or keep `StartupGroups.exe` for now to minimise diff? (Recommend rename for consistency.)
- [ ] **User-data folder names**: rename `%AppData%\StartupGroups\` → `%AppData%\Salvo\` and `%LocalAppData%\StartupGroups.UserData\` → `%LocalAppData%\Salvo.UserData\`? (Recommend yes — pre-1.0, clean break.)

Default behaviour if no answers: yes to all (full rename), Company stays as "Salvo", `Salvo.exe` as filename.

## Section 1 — Solution + project structure

Rename folders, csproj files, slnx entries.

| From | To |
| --- | --- |
| `StartupGroups.slnx` | `Salvo.slnx` |
| `src/StartupGroups.App/` (folder) | `src/Salvo.App/` |
| `src/StartupGroups.App/StartupGroups.App.csproj` | `src/Salvo.App/Salvo.App.csproj` |
| `src/StartupGroups.Core/` (folder) | `src/Salvo.Core/` |
| `src/StartupGroups.Core/StartupGroups.Core.csproj` | `src/Salvo.Core/Salvo.Core.csproj` |
| `src/StartupGroups.Elevator/` (folder) | `src/Salvo.Elevator/` |
| `src/StartupGroups.Elevator/StartupGroups.Elevator.csproj` | `src/Salvo.Elevator/Salvo.Elevator.csproj` |
| `tests/StartupGroups.App.Tests/` | `tests/Salvo.App.Tests/` |
| `tests/StartupGroups.App.Tests/StartupGroups.App.Tests.csproj` | `tests/Salvo.App.Tests/Salvo.App.Tests.csproj` |
| `tests/StartupGroups.Core.Tests/` | `tests/Salvo.Core.Tests/` |
| `tests/StartupGroups.Core.Tests/StartupGroups.Core.Tests.csproj` | `tests/Salvo.Core.Tests/Salvo.Core.Tests.csproj` |

**Leave alone (being retired in Phase 3):**
- `src/StartupGroups.Installer.UI/` — Burn BA project
- `installer/StartupGroups.Bundle/` — Burn bundle
- `installer/StartupGroups.Installer/` — MSI

These get deleted entirely in the Phase 3 cleanup PR; renaming them now is wasted work.

**Update inside each csproj:**
- `<RootNamespace>StartupGroups.App</RootNamespace>` → `Salvo.App` (etc per project)
- `<AssemblyName>StartupGroups</AssemblyName>` (in App.csproj) → `Salvo`
- All `<ProjectReference Include="..\..\src\StartupGroups.X\..." />` paths get updated
- The `<Target Name="CopyAssetsToPublish">` and elevator-copying targets in App.csproj reference paths

**Update `Salvo.slnx`** — replace all the `<Project Path="src/StartupGroups.X/..." />` and `tests/` entries with the new paths.

## Section 2 — Namespaces + using statements

Mechanical find-and-replace across all `.cs`, `.xaml`, `.xaml.cs`, `.resx` files:

- `namespace StartupGroups.App` → `namespace Salvo.App`
- `namespace StartupGroups.Core` → `namespace Salvo.Core`
- `namespace StartupGroups.Elevator` → `namespace Salvo.Elevator`
- `using StartupGroups.App.*` → `using Salvo.App.*`
- `using StartupGroups.Core.*` → `using Salvo.Core.*`
- `xmlns:loc="clr-namespace:StartupGroups.App.Localization"` → `clr-namespace:Salvo.App.Localization`
- `x:Class="StartupGroups.App.Views.MainWindow"` → `x:Class="Salvo.App.Views.MainWindow"`
- (Same pattern for all XAML `x:Class` + `clr-namespace:` references)

**Suggested approach**: PowerShell one-liner or a `dotnet tool install -g dotnet-rename` equivalent. Likely fine to script:

```powershell
Get-ChildItem -Recurse -Include *.cs,*.xaml,*.csproj,*.slnx,*.resx | ForEach-Object {
    (Get-Content $_.FullName -Raw) `
        -replace 'StartupGroups\.App', 'Salvo.App' `
        -replace 'StartupGroups\.Core', 'Salvo.Core' `
        -replace 'StartupGroups\.Elevator', 'Salvo.Elevator' `
        | Set-Content $_.FullName -NoNewline
}
```

Be careful not to touch `StartupGroups.Installer.UI` namespaces (the Burn BA project we're leaving alone).

## Section 3 — Branding constants

### `Directory.Build.props`

| Field | From | To |
| --- | --- | --- |
| `<Product>` | `Startup Groups` | `Salvo` |
| `<Company>` | `Startup Groups` | `Salvo` |
| `<AppId>` | `startup-groups` | `salvo` |
| `<AppUpgradeCode>` | existing GUID | **generate new GUID** (clean break for legacy MSI) |
| `<AppBundleUpgradeCode>` | existing GUID | **generate new GUID** (legacy Burn bundle; gets deleted in Phase 3, but consistency matters) |
| `<AppSupportUrl>` | `https://github.com/beelzer/startup-groups` | `https://github.com/beelzer/salvo` (after repo rename) |
| `<AppAboutUrl>` | `https://github.com/beelzer/startup-groups` | `https://github.com/beelzer/salvo` |

Generate new GUIDs with PowerShell: `[guid]::NewGuid().ToString().ToUpper()`.

### `src/Salvo.Core/Branding/AppBranding.cs` (was `src/StartupGroups.Core/...`)

- `AppName` string → "Salvo"
- `SupportUrl` constant → `https://github.com/beelzer/salvo`
- Anything else referencing "Startup Groups" or the old repo URL

### `src/Salvo.Core/Services/AppPaths.cs`

| Constant | From | To |
| --- | --- | --- |
| `AppFolderName` | `"StartupGroups"` | `"Salvo"` |
| `LocalDataFolderName` | `"StartupGroups.UserData"` | `"Salvo.UserData"` |
| (Other path-derived constants flow from these two) | | |

Net effect: app config moves from `%AppData%\StartupGroups\` → `%AppData%\Salvo\`. Existing canary users' data abandoned in place; matches pre-1.0 policy.

## Section 4 — MSIX

### `installer/Msix/Package.appxmanifest`

- `<Identity Name="StartupGroups"` → `<Identity Name="Salvo"`
- `<Identity Publisher="CN=StartupGroupsDev"` → `<Identity Publisher="CN=SalvoDev"`
- `<DisplayName>Startup Groups</DisplayName>` → `<DisplayName>Salvo</DisplayName>`
- `<PublisherDisplayName>Startup Groups</PublisherDisplayName>` → `<PublisherDisplayName>Salvo</PublisherDisplayName>`
- `<Description>...launches groups of programs at startup.</Description>` → rewrite around Salvo
- `<Application Id="App" Executable="StartupGroups.exe"` → `Executable="Salvo.exe"` (only if assembly is renamed; otherwise leave)
- `<uap:VisualElements DisplayName="Startup Groups"` → `"Salvo"`
- `<uap:VisualElements Description=...` → rewrite

### `installer/Msix/build.ps1`

- All `$AppProj`, `$Manifest`, output paths that hardcode `StartupGroups.*`
- The line `dotnet publish $AppProj ...` will pick up the renamed project automatically once the csproj is renamed, but the `-o $StageDir` and downstream output filenames need updating
- Output file naming: `StartupGroups-$Version.msix` → `Salvo-$Version.msix`
- Default `-Publisher` param: `'CN=StartupGroupsDev'` → `'CN=SalvoDev'`
- Update comment header

### `installer/Msix/generate-appinstaller.ps1`

- `<Identity Name="StartupGroups"` strings → `Salvo`
- `MainPackage Name="StartupGroups"` → `MainPackage Name="Salvo"`
- Default `-Publisher` param
- Output file `StartupGroups.appinstaller` → `Salvo.appinstaller`
- Default URL pattern: `releases/latest/download/StartupGroups.appinstaller` → `Salvo.appinstaller`

### Visual assets

`installer/Msix/Images/` — keep existing PNGs as-is. They're the icon design which we're keeping. If you want to redesign, separate task.

## Section 5 — Channel manifests

### `installer/Chocolatey/`

- Rename file: `startupgroups.nuspec` → `salvo.nuspec`
- Inside:
  - `<id>startupgroups</id>` → `<id>salvo</id>`
  - All `Startup Groups` / `StartupGroups` references in title/description/tags
  - `<projectUrl>https://github.com/beelzer/startup-groups</projectUrl>` → `salvo`
  - Other `iconUrl`, `bugTrackerUrl`, etc.
- `tools/chocolateyInstall.ps1`:
  - `$packageName = 'startupgroups'` → `'salvo'`
  - URL paths referencing `StartupGroups-X.msix` → `Salvo-X.msix`
  - The temp dir naming
- `tools/chocolateyUninstall.ps1`:
  - `Get-AppxPackage -Name 'StartupGroups'` → `'Salvo'`

### `installer/Scoop/`

- Rename file: `startupgroups.json` → `salvo.json`
- Inside:
  - `"description": "..."` → rewrite
  - All `startup-groups` → `salvo` in URLs
  - Asset path: `StartupGroups-$version.0.msix` → `Salvo-$version.0.msix`
  - `"StartupGroups.exe"` → `"Salvo.exe"` in shortcuts
  - `"Startup Groups"` → `"Salvo"` in shortcut display name

## Section 6 — Source code references

### `src/Salvo.App/Services/MsixUpdateService.cs`

- `AppInstallerUri` URL: `releases/latest/download/StartupGroups.appinstaller` → `Salvo.appinstaller`
- HTTP user-agent: `"StartupGroups"` → `"Salvo"`

### `src/Salvo.App/Services/UpdateService.cs` (the Velopack one)

- HTTP user-agent: `"StartupGroups"` → `"Salvo"`
- This whole file gets deleted in Phase 3; minor work, do it for consistency.

### `src/Salvo.App/App.xaml.cs`

- AUMID seed value (TrySetAppUserModelId) if it references `StartupGroups`
- Any other branding-related strings

### `src/Salvo.App/Program.cs`

- StartupObject namespace reference in csproj already handled
- Any literal strings

### `src/Salvo.App/Properties/PublishProfiles/win-x64.pubxml`

- Should be fine — no app name references

### `app.manifest` (Windows app manifest)

- `<assemblyIdentity name="StartupGroups.App"` → `name="Salvo.App"`

### Tests

- Test class names referencing `StartupGroups` → `Salvo`
- `using` statements

## Section 7 — Localisation

`src/Salvo.App/Resources/Strings.resx` + the 8 locale `.resx` files (`ar`, `de`, `fr`, `he`, `hi`, `ja`, `ru`, `th`):

- Every `<value>` string containing "Startup Groups" → replace with "Salvo" (or rewrite English; rely on machine-translation fallback for locales)
- `Strings.Designer.cs` doesn't contain strings, just keys — leave alone

Likely candidates (grep these patterns):
- "Startup Groups" (display name)
- "StartupGroups" (technical references — probably none in user strings, but check)

## Section 8 — CI workflows

### `.github/workflows/release.yml`

- Velopack pack: `vpk pack -u StartupGroups` → `-u Salvo` (twice — Stable + canary if still there)
- All output paths referencing `StartupGroups-Setup.exe`, `StartupGroups-Bundle-Setup.exe`, `StartupGroups-$version.msi`, `StartupGroups-$version.msix`, `StartupGroups.appinstaller` → `Salvo-*`
- Secret name references probably stay (they're still `MSIX_SIGNING_CERT_BASE64` etc., not app-name-specific)
- WinGet Releaser identifier: `StartupGroups.StartupGroups` → `Salvo.Salvo` (placeholder until Partner Center registers the real identifier)

### `.github/workflows/ci.yml`

- vpk pack: `-u StartupGroups` → `-u Salvo`
- All `StartupGroups-canary-Setup.exe` paths → `Salvo-canary-Setup.exe`
- Artifact name: `StartupGroups-msix-unsigned` → `Salvo-msix-unsigned`
- The canary publish steps are being retired in Phase 3 cleanup, so minimal effort there is OK

### `.github/workflows/semantic-pr.yml`

- Likely no change needed

## Section 9 — Documentation

### `README.md`

- Title (line 1)
- Tagline / description
- All "Startup Groups" references throughout
- Install matrix table — package names
- Repo URLs (post repo rename)

### `docs/MIGRATION_PLAN.md`

- All "StartupGroups" references → Salvo
- Note that the rename was performed

### `docs/RENAME_PLAN.md` (this file)

- Can be deleted once the rename merges, OR moved to `docs/history/` for posterity. Recommend delete.

### `LICENSE`

- Copyright line if it mentions "Startup Groups"

## Section 10 — Memory + repo settings

After the PR merges:

1. **GitHub repo rename**: Settings → Rename. `startup-groups` → `salvo`. GitHub auto-redirects old URLs.
2. **Local remote update**:
   ```powershell
   git remote set-url origin https://github.com/beelzer/salvo.git
   ```
3. **Update memory**: edit `project_rename_to_salvo.md` to status "executed"; update `MEMORY.md` accordingly.

## Verification checklist

Before opening the rename PR:

- [ ] `dotnet restore Salvo.slnx` — succeeds
- [ ] `dotnet build Salvo.slnx -c Release` — clean, 0 warnings
- [ ] `dotnet test Salvo.slnx -c Release` — all 88 tests pass (or whatever count after Burn/MSI tests get retired)
- [ ] `./installer/Msix/build.ps1` — produces `artifacts/msix/Salvo-X.Y.Z.0.msix` end-to-end
- [ ] Visual asset paths in the manifest still resolve (open the .msix as a zip; `Images/Square150x150Logo.png` etc. should be inside)
- [ ] `Add-AppxPackage -Register artifacts\msix-stage\AppxManifest.xml` succeeds; app appears in Start menu as "Salvo"; settings land at `%AppData%\Salvo\`
- [ ] `Get-AppxPackage Salvo | Remove-AppxPackage` cleans up

## Risks / things to watch

- **Velopack canary feed**: `vpk pack -u StartupGroups` is what existing canary users' UpdateManager polls. Changing to `-u Salvo` orphans them. Acceptable per pre-1.0 policy; just know it.
- **Test discovery**: if any test fixtures hardcode `StartupGroups` strings, the find-and-replace may have missed them. Run tests early to catch.
- **WiX legacy files**: `installer/StartupGroups.Bundle/` and `installer/StartupGroups.Installer/` still reference `StartupGroups` strings internally. They're being deleted in Phase 3. Don't touch them in this PR — adds noise.
- **MSIX manifest `Publisher` field**: must match the signing cert subject when we eventually sign. Default `CN=SalvoDev` is fine for unsigned/Developer Mode test installs. Update when SignPath cert lands.
- **AUMID changes**: if the app uses an Application User Model ID for taskbar grouping, changing it breaks taskbar pin continuity. Pre-1.0, accept.

## Estimated effort

- Sections 1+2 (folders, projects, namespaces): ~1h with scripted find-and-replace; another 30min fixing edge cases
- Sections 3+4 (branding, MSIX manifest): 30min
- Sections 5+6+7 (channel manifests, source refs, localisation): 30min
- Sections 8+9 (CI, docs): 30min
- Verification: 30min

Total: ~3 focused hours. Most of it is mechanical.
