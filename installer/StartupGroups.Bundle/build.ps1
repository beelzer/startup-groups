#Requires -Version 5.1
<#
.SYNOPSIS
    Build the Startup Groups Burn bundle (Setup.exe wrapping Velopack's Setup + managed BA).
.DESCRIPTION
    1. Publishes StartupGroups.Installer.UI as a self-contained net10.0-windows
       multi-file WPF app. Single-file is forbidden — Burn's apphost loader expects
       BA.exe + BA.dll + runtimeconfig.json side by side.
    2. Generates Generated.PayloadGroup.wxs by enumerating every file in the
       publish folder (sans the BA EXE itself) and emitting <Payload> entries.
       WiX 6's <Payloads Include="..." /> globbing element would obviate this,
       but the toolchain is pinned to 5.0.2.
    3. Runs wix build against Bundle.wxs + the generated payload group, producing
       StartupGroups-Bundle-Setup.exe.

    Assumes Velopack's Setup.exe has already been built (run vpk pack first).
    Defaults to looking in ./Releases/ next to the repo root, where vpk's
    default output lands. Override via -VelopackSetupPath.
.PARAMETER VelopackSetupPath
    Path to the Velopack Setup.exe to wrap (e.g. Releases/StartupGroups-win-Setup.exe
    for Stable or Releases/StartupGroups-canary-Setup.exe for Canary).
#>
param(
    [string]$VelopackSetupPath = ''
)

$ErrorActionPreference = 'Stop'
$ScriptDir   = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot    = Resolve-Path (Join-Path $ScriptDir '..\..')
$BaProj      = Join-Path $RepoRoot 'src\StartupGroups.Installer.UI\StartupGroups.Installer.UI.csproj'
$BaPublish   = Join-Path $RepoRoot 'artifacts\publish-installer-ui'
$OutputDir   = Join-Path $RepoRoot 'artifacts\installer'
$BundlePath  = Join-Path $OutputDir 'StartupGroups-Bundle-Setup.exe'
$IconPath    = Join-Path $RepoRoot 'src\StartupGroups.App\Assets\app.ico'

# --- Read source-of-truth values from Directory.Build.props ---
$PropsPath = Join-Path $RepoRoot 'Directory.Build.props'
[xml]$Props = Get-Content $PropsPath
$Pg = $Props.Project.PropertyGroup

$ProductName        = [string]$Pg.Product
$CompanyName        = [string]$Pg.Company
$ProductVer         = [string]$Pg.Version
$BundleUpgradeCode  = [string]$Pg.AppBundleUpgradeCode
$SupportUrl         = [string]$Pg.AppSupportUrl
$AboutUrl           = [string]$Pg.AppAboutUrl

# WiX requires 4-part version "W.X.Y.Z"; pad if source is 3-part.
if ($ProductVer -notmatch '^\d+\.\d+\.\d+\.\d+$') {
    $ProductVer = "$ProductVer.0"
}

# Dev iteration only: bump the build component per minute so each dev rebuild
# is a strictly higher bundle version than the last. This makes Burn treat
# successive dev builds as a MajorUpgrade of each other (rather than letting
# the new build silently overwrite the old build's package cache, which
# orphans the chained package). MUST stay in sync with the same logic in
# installer/StartupGroups.Installer/build.ps1 — see comment there. CI builds
# fall back to the stable semver from Directory.Build.props.
if (-not ($env:CI -or $env:GITHUB_ACTIONS)) {
    $epoch = [datetime]'2026-04-01'
    $buildNum = [int](([datetime]::UtcNow - $epoch).TotalMinutes) % 65535
    $vparts = $ProductVer.Split('.')
    $ProductVer = "$($vparts[0]).$($vparts[1]).$buildNum.0"
    Write-Host "Dev bundle version override: $ProductVer" -ForegroundColor DarkYellow
}

# --- Resolve VelopackSetupPath ---
# vpk's default output dir is ./Releases relative to whatever cwd it ran in.
# CI passes -VelopackSetupPath explicitly. Local dev: probe the conventional
# locations in priority order (caller-supplied > default Releases > artifacts).
if (-not $VelopackSetupPath) {
    $candidates = @(
        (Join-Path $RepoRoot 'Releases\StartupGroups-win-Setup.exe'),
        (Join-Path $RepoRoot 'Releases\StartupGroups-canary-Setup.exe'),
        (Join-Path $OutputDir 'StartupGroups-win-Setup.exe'),
        (Join-Path $OutputDir 'StartupGroups-canary-Setup.exe')
    )
    $VelopackSetupPath = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $VelopackSetupPath -or -not (Test-Path $VelopackSetupPath)) {
    throw "Velopack Setup.exe not found. Run 'vpk pack ...' first, or pass -VelopackSetupPath. Tried: $($candidates -join '; ')"
}
$VelopackSetupPath = (Resolve-Path $VelopackSetupPath).Path
Write-Host "Wrapping Velopack Setup: $VelopackSetupPath" -ForegroundColor Cyan

# Wipe any prior publish so stale runtime DLLs don't bloat the bundle.
if (Test-Path $BaPublish) { Remove-Item -Recurse -Force $BaPublish }
New-Item -ItemType Directory -Path $BaPublish -Force | Out-Null
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

Write-Host 'Publishing StartupGroups.Installer.UI (self-contained, multi-file)...' -ForegroundColor Cyan
# IMPORTANT: do NOT pass -p:PublishSingleFile=true. Burn's BA host expects
# the standard BA.exe + BA.dll + runtimeconfig.json layout; single-file
# extraction breaks the engine ↔ BA handoff over the OOP IPC pipe.
dotnet publish $BaProj `
    -c Release -r win-x64 --self-contained true `
    -o $BaPublish -nologo -v minimal

Write-Host 'Generating PayloadGroup from publish folder...' -ForegroundColor Cyan
$PayloadGroupPath = Join-Path $ScriptDir 'Generated.PayloadGroup.wxs'
$mainExeName = 'StartupGroups.Installer.UI.exe'
$mainExePath = Join-Path $BaPublish $mainExeName

# The BA EXE itself is referenced via <BootstrapperApplication SourceFile="..."/>
# in Bundle.wxs and gets auto-included by Burn — declaring it as a Payload too
# would produce a duplicate-id error. Everything else from publish is fair game.
$payloadXml = New-Object System.Xml.XmlDocument
$payloadXml.AppendChild($payloadXml.CreateXmlDeclaration('1.0', 'UTF-8', $null)) | Out-Null
$wixNs = 'http://wixtoolset.org/schemas/v4/wxs'
$root = $payloadXml.CreateElement('Wix', $wixNs)
$payloadXml.AppendChild($root) | Out-Null
$frag = $payloadXml.CreateElement('Fragment', $wixNs)
$root.AppendChild($frag) | Out-Null
$group = $payloadXml.CreateElement('PayloadGroup', $wixNs)
$group.SetAttribute('Id', 'InstallerUiPayloads')
$frag.AppendChild($group) | Out-Null

$publishFiles = Get-ChildItem -Path $BaPublish -File -Recurse | Where-Object { $_.Name -ne $mainExeName }
foreach ($f in $publishFiles) {
    $rel = $f.FullName.Substring($BaPublish.Length).TrimStart('\', '/')
    $payload = $payloadXml.CreateElement('Payload', $wixNs)
    $payload.SetAttribute('Name', $rel)
    $payload.SetAttribute('SourceFile', $f.FullName)
    $group.AppendChild($payload) | Out-Null
}

$payloadXml.Save($PayloadGroupPath)
Write-Host "  wrote $($publishFiles.Count) payload entries to $PayloadGroupPath" -ForegroundColor DarkGray

Write-Host 'Building Burn bundle...' -ForegroundColor Cyan
$wixArgs = @(
    'build',
    (Join-Path $ScriptDir 'Bundle.wxs'),
    $PayloadGroupPath,
    '-define', "ProductName=$ProductName",
    '-define', "CompanyName=$CompanyName",
    '-define', "ProductVersion=$ProductVer",
    '-define', "BundleUpgradeCode=$BundleUpgradeCode",
    '-define', "SupportUrl=$SupportUrl",
    '-define', "AboutUrl=$AboutUrl",
    '-define', "VelopackSetupPath=$VelopackSetupPath",
    '-define', "IconPath=$IconPath",
    '-define', "MainExePath=$mainExePath",
    '-ext', 'WixToolset.BootstrapperApplications.wixext',
    '-ext', 'WixToolset.Util.wixext',
    '-arch', 'x64',
    '-out', $BundlePath
)
& wix @wixArgs

if ($LASTEXITCODE -ne 0) {
    Write-Host "wix build failed ($LASTEXITCODE)" -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host ''
Write-Host "Bundle built: $BundlePath" -ForegroundColor Green
