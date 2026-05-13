$ErrorActionPreference = 'Stop'

# Chocolatey install script for Salvo.
#
# Pulls the .msix from the GitHub release and registers it via
# Add-AppxPackage. The MSIX itself drives all install state — we don't
# need a custom InstallLocation, registry write, or shortcut.
#
# Both URL + checksum need updating per release. A future CI job can
# automate this. For now it's a manual edit before `choco pack`.

$packageName = 'salvo'
$version     = '0.0.0'  # bump per release
$msixUrl     = "https://github.com/beelzer/salvo/releases/download/v$version/Salvo-$version.0.msix"
$msixSha256  = ''  # fill in from sha256sum of the released .msix

$tempDir  = Join-Path $env:TEMP "$packageName-install"
$msixPath = Join-Path $tempDir "Salvo-$version.0.msix"

Get-ChocolateyWebFile -PackageName $packageName `
                      -FileFullPath $msixPath `
                      -Url $msixUrl `
                      -Checksum $msixSha256 `
                      -ChecksumType 'sha256'

Add-AppxPackage -Path $msixPath -ForceApplicationShutdown

Write-Host "Salvo $version installed via Add-AppxPackage."
