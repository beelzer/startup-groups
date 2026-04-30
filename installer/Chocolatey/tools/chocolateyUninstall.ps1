$ErrorActionPreference = 'Stop'

# Chocolatey uninstall script for StartupGroups.
#
# `Get-AppxPackage` matches by package Identity Name from
# Package.appxmanifest. The MSIX uninstall removes the package + per-package
# AppData but does not touch the user's roaming `%AppData%\StartupGroups\`
# config. That's intentional — keeps groups + settings around in case the
# user reinstalls.

$pkg = Get-AppxPackage -Name 'StartupGroups' -ErrorAction SilentlyContinue
if ($pkg) {
    Remove-AppxPackage -Package $pkg.PackageFullName
    Write-Host "Removed $($pkg.PackageFullName)."
} else {
    Write-Host 'StartupGroups MSIX not found; nothing to uninstall.'
}
