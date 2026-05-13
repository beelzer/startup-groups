$ErrorActionPreference = 'Stop'

# Chocolatey uninstall script for Salvo.
#
# `Get-AppxPackage` matches by package Identity Name from
# Package.appxmanifest. The MSIX uninstall removes the package + per-package
# AppData but does not touch the user's roaming `%AppData%\Salvo\`
# config. That's intentional — keeps groups + settings around in case the
# user reinstalls.

$pkg = Get-AppxPackage -Name 'Salvo' -ErrorAction SilentlyContinue
if ($pkg) {
    Remove-AppxPackage -Package $pkg.PackageFullName
    Write-Host "Removed $($pkg.PackageFullName)."
} else {
    Write-Host 'Salvo MSIX not found; nothing to uninstall.'
}
