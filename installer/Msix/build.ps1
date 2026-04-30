#Requires -Version 5.1
<#
.SYNOPSIS
    Build the StartupGroups MSIX package (Phase 0 of the Velopack→MSIX
    migration; see docs/MIGRATION_PLAN.md).
.DESCRIPTION
    Direct invocation of MakeAppx.exe + (optionally) SignTool.exe from the
    Windows 11 SDK. Bypasses the legacy `.wapproj` packaging project, which
    relies on Microsoft.DesktopBridge.targets — that file ships with the
    "Universal Windows Platform build tools" workload, which Microsoft
    removed from VS Build Tools 2026. Direct MakeAppx is the supported
    modern path.

    Steps:
      1. Publish StartupGroups.App with dotnet publish (multi-file,
         self-contained, no single-file extraction — MakeAppx needs
         discrete files for hashing and delta updates).
      2. Stage the publish output + manifest + visual assets in a single
         layout dir.
      3. Stamp the manifest's Version attribute with the build version.
      4. Run MakeAppx.exe pack against the staging dir.
      5. (Optional) Sign with SignTool if -CertPath is provided.
.PARAMETER Configuration
    Build configuration (default: Release).
.PARAMETER Version
    4-part MSIX version W.X.Y.Z. Defaults to Directory.Build.props Version
    with .0 appended. Z must stay 0 — Store reserves the revision field.
.PARAMETER OutputPath
    Output .msix path. Default: artifacts/msix/StartupGroups-<version>.msix.
.PARAMETER CertPath
    Optional .pfx file to sign the package with. If omitted, package is
    unsigned (installable only via Developer Mode or after Phase 1's signing
    cert lands).
.PARAMETER CertPassword
    Password for -CertPath, if any.
.PARAMETER Publisher
    Publisher CN= value baked into the manifest. Default
    "CN=StartupGroupsDev". Override to match your Partner Center publisher
    ID (Store builds) or your sideload signing cert subject.
#>
param(
    [string]$Configuration = 'Release',
    [string]$Version = '',
    [string]$OutputPath = '',
    [string]$CertPath = '',
    [string]$CertPassword = '',
    [string]$Publisher = 'CN=StartupGroupsDev'
)

$ErrorActionPreference = 'Stop'
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot  = Resolve-Path (Join-Path $ScriptDir '..\..')
$AppProj   = Join-Path $RepoRoot 'src\StartupGroups.App\StartupGroups.App.csproj'
$Manifest  = Join-Path $ScriptDir 'Package.appxmanifest'
$ImagesDir = Join-Path $ScriptDir 'Images'
$StageDir  = Join-Path $RepoRoot 'artifacts\msix-stage'
$OutputDir = Join-Path $RepoRoot 'artifacts\msix'

# --- Resolve version ---
if (-not $Version) {
    [xml]$props = Get-Content (Join-Path $RepoRoot 'Directory.Build.props')
    $base = [string]$props.Project.PropertyGroup.Version
    if ($base -notmatch '^\d+\.\d+\.\d+\.\d+$') { $base = "$base.0" }
    $Version = $base
}
if (-not $OutputPath) {
    $OutputPath = Join-Path $OutputDir "StartupGroups-$Version.msix"
}

# --- Resolve Windows SDK tools ---
# MakeAppx.exe + SignTool.exe live under
# C:\Program Files (x86)\Windows Kits\10\bin\<sdk-version>\x64\.
# Pick the highest installed SDK version that contains both tools.
$kitsRoot = 'C:\Program Files (x86)\Windows Kits\10\bin'
if (-not (Test-Path $kitsRoot)) {
    throw "Windows 10/11 SDK not found at $kitsRoot. Install it via Visual Studio or the standalone Windows SDK installer."
}
$sdkBin = Get-ChildItem $kitsRoot -Directory |
    Where-Object { $_.Name -match '^10\.' } |
    Sort-Object Name -Descending |
    ForEach-Object { Join-Path $_.FullName 'x64' } |
    Where-Object { (Test-Path (Join-Path $_ 'MakeAppx.exe')) -and (Test-Path (Join-Path $_ 'SignTool.exe')) } |
    Select-Object -First 1

if (-not $sdkBin) {
    throw "Could not find a Windows SDK with both MakeAppx.exe and SignTool.exe under $kitsRoot."
}
$makeAppx = Join-Path $sdkBin 'MakeAppx.exe'
$signTool = Join-Path $sdkBin 'SignTool.exe'
Write-Host "Using SDK tools at $sdkBin" -ForegroundColor DarkGray

# --- Clean staging + output dirs ---
if (Test-Path $StageDir)  { Remove-Item -Recurse -Force $StageDir }
New-Item -ItemType Directory -Path $StageDir -Force | Out-Null
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

# --- Publish the app ---
Write-Host "Publishing StartupGroups.App ($Configuration, win-x64, multi-file)..." -ForegroundColor Cyan
# PublishSingleFile must be FALSE for MSIX. MakeAppx hashes individual
# files for block-level deltas; single-file output collapses everything
# into one EXE and breaks both delta updates and Store certification.
dotnet publish $AppProj `
    -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=false `
    -p:PublishReadyToRun=false `
    -p:PublishTrimmed=false `
    -o $StageDir -nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }

# --- Copy manifest + images into the staging layout ---
Write-Host 'Staging manifest + visual assets...' -ForegroundColor Cyan
$stagedManifest = Join-Path $StageDir 'AppxManifest.xml'
Copy-Item $Manifest $stagedManifest

# Stamp the manifest's Identity/@Version + Identity/@Publisher with the
# requested values so the same manifest source can produce sideload + Store
# variants from one build pipeline.
[xml]$xml = Get-Content $stagedManifest
$ns = 'http://schemas.microsoft.com/appx/manifest/foundation/windows10'
$identity = $xml.Package.Identity
$identity.Version = $Version
$identity.Publisher = $Publisher
$xml.Save($stagedManifest)
Write-Host "  Version=$Version" -ForegroundColor DarkGray
Write-Host "  Publisher=$Publisher" -ForegroundColor DarkGray

# Images go alongside the published binaries — Package.appxmanifest's paths
# (`Images\StoreLogo.png` etc.) resolve relative to the package root.
$stagedImages = Join-Path $StageDir 'Images'
Copy-Item -Recurse $ImagesDir $stagedImages

# --- Pack the MSIX ---
Write-Host "Packing $OutputPath..." -ForegroundColor Cyan
& $makeAppx pack /d $StageDir /p $OutputPath /overwrite
if ($LASTEXITCODE -ne 0) { throw "MakeAppx pack failed (exit $LASTEXITCODE)." }

# --- Optional sign step ---
if ($CertPath) {
    Write-Host "Signing $OutputPath with $CertPath..." -ForegroundColor Cyan
    $signArgs = @('sign', '/fd', 'SHA256', '/a')
    if ($CertPassword) {
        $signArgs += @('/f', $CertPath, '/p', $CertPassword)
    } else {
        $signArgs += @('/f', $CertPath)
    }
    $signArgs += $OutputPath
    & $signTool @signArgs
    if ($LASTEXITCODE -ne 0) { throw "SignTool sign failed (exit $LASTEXITCODE)." }
}

Write-Host ''
Write-Host "MSIX built: $OutputPath" -ForegroundColor Green
Write-Host "  size: $((Get-Item $OutputPath).Length / 1MB | ForEach-Object { '{0:F1}' -f $_ }) MB" -ForegroundColor DarkGray
if (-not $CertPath) {
    Write-Host '  unsigned — install via Windows Developer Mode for testing.' -ForegroundColor DarkGray
}
