#Requires -Version 5.1
<#
.SYNOPSIS
    Generate a StartupGroups.appinstaller XML file pointing at a tagged
    GitHub release's .msix asset, with auto-update settings wired so App
    Installer silently keeps users on the latest release.
.DESCRIPTION
    The .appinstaller XML drives App Installer's modern install + auto-update
    flow for sideloaded MSIX. Uri (the AppInstaller element's own
    self-reference) MUST stay stable across releases — App Installer polls
    that URL on launch every <HoursBetweenUpdateChecks> hours. We use the
    GitHub `releases/latest/download/...` redirect for that.

    MainPackage.Uri is the explicit per-version URL pointing at the .msix
    inside this specific release (deterministic; never moves).
.PARAMETER Version
    4-part W.X.Y.Z. Z stays 0 (Store reserves the revision field).
.PARAMETER MsixFileName
    Filename of the .msix asset uploaded to the GitHub release.
.PARAMETER RepoUrl
    GitHub repo URL, e.g. https://github.com/beelzer/startup-groups.
.PARAMETER Tag
    Release tag (e.g. v0.3.0).
.PARAMETER Publisher
    Manifest Publisher CN= value. Must match the .msix's signature subject
    or App Installer rejects the install. Default matches the dev build.
.PARAMETER OutputPath
    Where to write the .appinstaller XML.
#>
param(
    [Parameter(Mandatory)] [string]$Version,
    [Parameter(Mandatory)] [string]$MsixFileName,
    [Parameter(Mandatory)] [string]$RepoUrl,
    [Parameter(Mandatory)] [string]$Tag,
    [string]$Publisher = 'CN=StartupGroupsDev',
    [string]$OutputPath = ''
)

$ErrorActionPreference = 'Stop'

if (-not $OutputPath) {
    $ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    $OutputPath = Join-Path $ScriptDir '..\..\artifacts\msix\StartupGroups.appinstaller'
}

$RepoUrl = $RepoUrl.TrimEnd('/')
$selfUri = "$RepoUrl/releases/latest/download/StartupGroups.appinstaller"
$mainPackageUri = "$RepoUrl/releases/download/$Tag/$MsixFileName"

# 2021 schema — supports OnLaunch background polling + AutomaticBackgroundTask
# + ForceUpdateFromAnyVersion. Older schemas exist; pick the newest broadly
# supported one (Windows 10 1909+; we target 1809+ but App Installer on
# older builds gracefully ignores newer attributes).
$xml = @"
<?xml version="1.0" encoding="utf-8"?>
<AppInstaller
    xmlns="http://schemas.microsoft.com/appx/appinstaller/2021"
    Uri="$selfUri"
    Version="$Version">

    <MainPackage
        Name="StartupGroups"
        Publisher="$Publisher"
        Version="$Version"
        ProcessorArchitecture="x64"
        Uri="$mainPackageUri" />

    <UpdateSettings>
        <OnLaunch HoursBetweenUpdateChecks="8" ShowPrompt="false" UpdateBlocksActivation="false" />
        <AutomaticBackgroundTask />
        <ForceUpdateFromAnyVersion>true</ForceUpdateFromAnyVersion>
    </UpdateSettings>
</AppInstaller>
"@

$outputDir = Split-Path -Parent $OutputPath
if (-not (Test-Path $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
}

Set-Content -Path $OutputPath -Value $xml -Encoding UTF8 -NoNewline
Write-Host "AppInstaller written: $OutputPath" -ForegroundColor Green
Write-Host "  Self URI:        $selfUri" -ForegroundColor DarkGray
Write-Host "  MainPackage URI: $mainPackageUri" -ForegroundColor DarkGray
Write-Host "  Version:         $Version" -ForegroundColor DarkGray
