#requires -Version 7
# Rebuilds src/StartupGroups.App/Assets/{app,tray-light,tray-dark}.ico from the
# geometry declared in branding/app-icon.svg. Run from the repo root:
#   pwsh branding/build-icons.ps1
#
# A single badged design is used for all three .ico files — the tray icon is
# colourful so it reads on light taskbars AND the dark Windows 11 hidden-icons
# flyout without needing a theme-aware variant. The two tray-*.ico files exist
# only because TrayViewModel still loads them by name; keeping them identical
# makes the system-theme detection a no-op until we decide to simplify the loader.
#
# Keep coordinates in lockstep with branding/app-icon.svg.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path -Parent $PSScriptRoot
$assetsDir = Join-Path $repoRoot 'src/StartupGroups.App/Assets'

# Design grid is 64x64. Background is a rounded blue square; bars are white
# with reduced opacity for the outer two so the middle bar pops as the bottleneck.
$background = @{ X = 4; Y = 4; W = 56; H = 56; Rx = 12; Color = '2563EB' }
$bars = @(
    @{ X = 12; Y = 15; W = 24; H = 10; Rx = 5; Color = 'FFFFFF'; Alpha = 178 },
    @{ X = 20; Y = 27; W = 32; H = 10; Rx = 5; Color = 'FFFFFF'; Alpha = 255 },
    @{ X = 30; Y = 39; W = 18; H = 10; Rx = 5; Color = 'FFFFFF'; Alpha = 178 }
)

$variants = @(16, 24, 32, 48, 64, 128, 256)

function New-RoundedRectPath {
    param(
        [Parameter(Mandatory)] [single] $X,
        [Parameter(Mandatory)] [single] $Y,
        [Parameter(Mandatory)] [single] $W,
        [Parameter(Mandatory)] [single] $H,
        [Parameter(Mandatory)] [single] $R
    )
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [single][math]::Min($R * 2, [math]::Min($W, $H))
    if ($d -le 0) {
        $path.AddRectangle([System.Drawing.RectangleF]::new($X, $Y, $W, $H)) | Out-Null
        return $path
    }
    $path.AddArc($X,             $Y,             $d, $d, 180, 90) | Out-Null
    $path.AddArc($X + $W - $d,   $Y,             $d, $d, 270, 90) | Out-Null
    $path.AddArc($X + $W - $d,   $Y + $H - $d,   $d, $d,   0, 90) | Out-Null
    $path.AddArc($X,             $Y + $H - $d,   $d, $d,  90, 90) | Out-Null
    $path.CloseFigure()
    return $path
}

function ConvertTo-DrawingColor {
    param(
        [Parameter(Mandatory)] [string] $HexRgb,
        [int] $Alpha = 255
    )
    $argb = [System.Convert]::ToInt32($HexRgb, 16)
    return [System.Drawing.Color]::FromArgb(
        [byte]$Alpha,
        [byte](($argb -shr 16) -band 0xFF),
        [byte](($argb -shr 8) -band 0xFF),
        [byte]($argb -band 0xFF))
}

function New-BadgeBitmap {
    param([Parameter(Mandatory)] [int] $Size)
    $bmp = New-Object System.Drawing.Bitmap $Size, $Size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)

    $scale = $Size / 64.0

    # Background badge.
    $bgColor = ConvertTo-DrawingColor -HexRgb $background.Color
    $bgBrush = New-Object System.Drawing.SolidBrush $bgColor
    $bgPath = New-RoundedRectPath `
        -X ([single]($background.X * $scale)) `
        -Y ([single]($background.Y * $scale)) `
        -W ([single]($background.W * $scale)) `
        -H ([single]($background.H * $scale)) `
        -R ([single]($background.Rx * $scale))
    $g.FillPath($bgBrush, $bgPath)
    $bgPath.Dispose()
    $bgBrush.Dispose()

    # Bars.
    foreach ($bar in $bars) {
        $color = ConvertTo-DrawingColor -HexRgb $bar.Color -Alpha $bar.Alpha
        $brush = New-Object System.Drawing.SolidBrush $color
        $path = New-RoundedRectPath `
            -X ([single]($bar.X * $scale)) `
            -Y ([single]($bar.Y * $scale)) `
            -W ([single]($bar.W * $scale)) `
            -H ([single]($bar.H * $scale)) `
            -R ([single][math]::Min(($bar.Rx * $scale), (($bar.H * $scale) / 2)))
        $g.FillPath($brush, $path)
        $path.Dispose()
        $brush.Dispose()
    }

    $g.Dispose()
    return $bmp
}

function Write-IcoFromPngs {
    param(
        [Parameter(Mandatory)] [string] $OutPath,
        [Parameter(Mandatory)] [hashtable[]] $Entries   # each: @{ Size = int; Png = byte[] }
    )
    $count = $Entries.Count
    $headerSize = 6 + 16 * $count
    $stream = [System.IO.MemoryStream]::new()
    $w = [System.IO.BinaryWriter]::new($stream)

    $w.Write([uint16]0)           # reserved
    $w.Write([uint16]1)           # type = icon
    $w.Write([uint16]$count)

    $dataOffset = $headerSize
    foreach ($e in $Entries) {
        $sz = [byte]0
        if ($e.Size -lt 256) { $sz = [byte]$e.Size }
        $w.Write($sz)             # width  (0 means 256)
        $w.Write($sz)             # height
        $w.Write([byte]0)         # palette count
        $w.Write([byte]0)         # reserved
        $w.Write([uint16]1)       # colour planes
        $w.Write([uint16]32)      # bpp
        $w.Write([uint32]$e.Png.Length)
        $w.Write([uint32]$dataOffset)
        $dataOffset += $e.Png.Length
    }
    foreach ($e in $Entries) { $w.Write($e.Png) }
    $w.Flush()

    [System.IO.File]::WriteAllBytes($OutPath, $stream.ToArray())
    $w.Dispose()
    $stream.Dispose()
}

$entries = @()
foreach ($size in $variants) {
    $bmp = New-BadgeBitmap -Size $size
    $ms = [System.IO.MemoryStream]::new()
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $entries += @{ Size = $size; Png = $ms.ToArray() }
    $bmp.Dispose()
    $ms.Dispose()
}

foreach ($fileName in @('app.ico', 'tray-light.ico', 'tray-dark.ico')) {
    $outPath = Join-Path $assetsDir $fileName
    Write-Host "Writing $outPath"
    Write-IcoFromPngs -OutPath $outPath -Entries $entries
}

$preview = New-BadgeBitmap -Size 256
$preview.Save((Join-Path $PSScriptRoot 'app-icon-preview.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$preview.Dispose()

Write-Host "Done."
