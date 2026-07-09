#Requires -Version 5.1
<#
.SYNOPSIS
    Fast local dev redeploy of Salvo. Run after code changes to refresh
    the installed app on this machine.
.DESCRIPTION
    Two modes:

      (default) LOOSE / REGISTERED deploy — the fast inner loop. Publishes
      Salvo.App straight into artifacts\msix-stage and registers it in
      place with `Add-AppxPackage -Register`. No MakeAppx pack, no signing.
      Requires Windows Developer Mode (Settings -> Privacy & security ->
      For developers). This is how the current Salvo dev install is wired.

      -Packed — builds a real .msix via installer\Msix\build.ps1 (R2R
      Release publish + MakeAppx) and installs that. Closer to what ships
      to users, but slower. Use before cutting a release to smoke-test the
      actual package.

    Either mode closes a running Salvo first (so its files aren't locked)
    and force-updates from any version, so re-deploying the same or an
    older version number still works.
.PARAMETER Configuration
    Build configuration for the loose deploy. Default Debug (fastest).
    Pass Release to exercise the cold-start / R2R-adjacent code paths.
.PARAMETER Packed
    Build + install a real .msix instead of registering loose files.
.EXAMPLE
    .\installer\Msix\redeploy.ps1
    Fast Debug redeploy — the everyday inner loop.
.EXAMPLE
    .\installer\Msix\redeploy.ps1 -Configuration Release
    Loose redeploy of a Release build.
.EXAMPLE
    .\installer\Msix\redeploy.ps1 -Packed
    Build the real signed/unsigned .msix and install it.
#>
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$Packed
)

$ErrorActionPreference = 'Stop'
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot  = Resolve-Path (Join-Path $ScriptDir '..\..')
$AppProj   = Join-Path $RepoRoot 'src\Salvo.App\Salvo.App.csproj'
$Manifest  = Join-Path $ScriptDir 'Package.appxmanifest'
$ImagesDir = Join-Path $ScriptDir 'Images'
$StageDir  = Join-Path $RepoRoot 'artifacts\msix-stage'
$Publisher = 'CN=SalvoDev'

# --- Resolve version from Directory.Build.props (W.X.Y.Z; Store reserves Z) ---
[xml]$props = Get-Content (Join-Path $RepoRoot 'Directory.Build.props')
$Version = ([string]$props.Project.PropertyGroup.Version).Trim()
if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') { $Version = "$Version.0" }

# --- Close any running Salvo so publish/register don't hit locked files ---
$running = Get-Process Salvo -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "Closing running Salvo (PID $($running.Id -join ', '))..." -ForegroundColor DarkYellow
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 400   # let file handles release
}

if ($Packed) {
    # Real-package path: delegate to the production build, then install.
    & (Join-Path $ScriptDir 'build.ps1') -Configuration Release -Version $Version
    $msix = Join-Path $RepoRoot "artifacts\msix\Salvo-$Version.msix"
    Write-Host "Installing $msix ..." -ForegroundColor Cyan
    Add-AppxPackage -Path $msix -ForceUpdateFromAnyVersion
    Write-Host "Salvo $Version installed from packaged .msix." -ForegroundColor Green
    return
}

# --- Loose deploy: publish into the staging dir -------------------------
Write-Host "Publishing Salvo.App ($Configuration, win-x64) -> artifacts\msix-stage" -ForegroundColor Cyan
if (Test-Path $StageDir) { Remove-Item -Recurse -Force $StageDir }
New-Item -ItemType Directory -Path $StageDir -Force | Out-Null

# Multi-file self-contained (matches build.ps1). No R2R here — keeps the
# inner loop fast; -Packed is the path that exercises R2R.
dotnet publish $AppProj `
    -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=false `
    -p:PublishReadyToRun=false `
    -p:PublishTrimmed=false `
    -o $StageDir -nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }

# --- Stage the manifest (as AppxManifest.xml) + visual assets ---
$stagedManifest = Join-Path $StageDir 'AppxManifest.xml'
Copy-Item $Manifest $stagedManifest
[xml]$xml = Get-Content $stagedManifest
$xml.Package.Identity.Version   = $Version
$xml.Package.Identity.Publisher = $Publisher
$xml.Save($stagedManifest)
Copy-Item -Recurse $ImagesDir (Join-Path $StageDir 'Images')

# --- Register in place (fast; no MakeAppx / no signing) ---
Write-Host "Registering Salvo $Version from artifacts\msix-stage ..." -ForegroundColor Cyan
Add-AppxPackage -Register $stagedManifest -ForceUpdateFromAnyVersion

Write-Host ''
Write-Host "Salvo $Version deployed. Launch it from the Start menu (search 'Salvo')." -ForegroundColor Green
