#Requires -Version 5.1
<#
.SYNOPSIS
    Build the Startup Groups MSI installer.
.DESCRIPTION
    1. Publishes StartupGroups.App and StartupGroups.Elevator as single-file win-x64 self-contained exes.
    2. Runs wix build against Package.wxs, producing StartupGroups.msi.
       WiX preprocessor constants are sourced from Directory.Build.props (single source of truth).
#>

$ErrorActionPreference = 'Stop'
$ScriptDir   = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot    = Resolve-Path (Join-Path $ScriptDir '..\..')
$PublishDir  = Join-Path $RepoRoot 'artifacts\publish'
$OutputDir   = Join-Path $RepoRoot 'artifacts\installer'
$MsiPath     = Join-Path $OutputDir 'StartupGroups.msi'

# --- Read source-of-truth values from Directory.Build.props ---
$PropsPath = Join-Path $RepoRoot 'Directory.Build.props'
[xml]$Props = Get-Content $PropsPath
$Pg = $Props.Project.PropertyGroup

$ProductName   = [string]$Pg.Product
$CompanyName   = [string]$Pg.Company
$ProductVer    = [string]$Pg.Version
$UpgradeCode   = [string]$Pg.AppUpgradeCode
$SupportUrl    = [string]$Pg.AppSupportUrl
$AboutUrl      = [string]$Pg.AppAboutUrl

# WiX requires 4-part version "W.X.Y.Z"; pad if source is 3-part.
if ($ProductVer -notmatch '^\d+\.\d+\.\d+\.\d+$') {
    $ProductVer = "$ProductVer.0"
}

# Dev iteration only: rewrite the build component (3rd part) to a
# minute-based monotonic counter so every local rebuild is a strictly
# greater version than the last. MajorUpgrade then fires on re-install,
# replaces files, and removes the previous ARP entry. CI builds keep the
# semver from Directory.Build.props untouched (CI/GITHUB_ACTIONS env vars
# are set by the runner). Component cap is 65535 — minutes since 2026-04-01
# stays under that for ~45 days, plenty for a dev cycle.
if (-not ($env:CI -or $env:GITHUB_ACTIONS)) {
    $epoch = [datetime]'2026-04-01'
    $buildNum = [int](([datetime]::UtcNow - $epoch).TotalMinutes) % 65535
    $vparts = $ProductVer.Split('.')
    $ProductVer = "$($vparts[0]).$($vparts[1]).$buildNum.0"
    Write-Host "Dev build version override: $ProductVer" -ForegroundColor DarkYellow
}

$MainExeName     = 'StartupGroups.exe'
$ElevatorExeName = 'StartupGroups.Elevator.exe'
$RegistryKey     = "Software\$ProductName"

New-Item -ItemType Directory -Path $PublishDir -Force | Out-Null
New-Item -ItemType Directory -Path $OutputDir  -Force | Out-Null

Write-Host 'Publishing StartupGroups.App...' -ForegroundColor Cyan
dotnet publish (Join-Path $RepoRoot 'src\StartupGroups.App\StartupGroups.App.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $PublishDir -nologo -v minimal

Write-Host 'Publishing StartupGroups.Elevator...' -ForegroundColor Cyan
dotnet publish (Join-Path $RepoRoot 'src\StartupGroups.Elevator\StartupGroups.Elevator.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $PublishDir -nologo -v minimal

Write-Host 'Building MSI...' -ForegroundColor Cyan
$wixArgs = @(
    'build',
    (Join-Path $ScriptDir 'Package.wxs'),
    '-define', "PublishDir=$PublishDir",
    '-define', "ProductName=$ProductName",
    '-define', "CompanyName=$CompanyName",
    '-define', "ProductVersion=$ProductVer",
    '-define', "UpgradeCode=$UpgradeCode",
    '-define', "SupportUrl=$SupportUrl",
    '-define', "AboutUrl=$AboutUrl",
    '-define', "MainExeName=$MainExeName",
    '-define', "ElevatorExeName=$ElevatorExeName",
    '-define', "RegistryKey=$RegistryKey",
    '-loc', (Join-Path $ScriptDir 'en-us.wxl'),
    '-ext', 'WixToolset.UI.wixext',
    '-ext', 'WixToolset.Util.wixext',
    '-arch', 'x64',
    '-bindpath', $ScriptDir,
    '-out', $MsiPath
)
& wix @wixArgs

if ($LASTEXITCODE -ne 0) {
    Write-Host "wix build failed ($LASTEXITCODE)" -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host ''
Write-Host "MSI built: $MsiPath" -ForegroundColor Green
