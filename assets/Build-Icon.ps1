# Renders the app icon (same vector design as icon.svg) at multiple sizes with WPF
# and packs the PNG frames into a single multi-resolution .ico file.
#
# Usage:  powershell -Sta -File assets\Build-Icon.ps1
#
# Requires Windows PowerShell 5.1 (uses WPF Rendering). No external tools needed.
[CmdletBinding()]
param(
    [string]$IcoPath    = '',
    [string]$PreviewDir = ''
)

$ErrorActionPreference = 'Stop'

$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $IcoPath)    { $IcoPath    = Join-Path $scriptDir '..\src\DefenderPerformanceTool\icon.ico' }
if (-not $PreviewDir) { $PreviewDir = $scriptDir }

Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName PresentationFramework

$mediaNs = 'xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"'

function ConvertFrom-PathData([string]$data) {
    $path = [System.Windows.Markup.XamlReader]::Parse("<Path $mediaNs Data='$data'/>")
    $geo = $path.Data
    $geo.Freeze()
    return $geo
}

function New-VerticalBrush([string]$topColor, [string]$bottomColor) {
    $brush = New-Object System.Windows.Media.LinearGradientBrush
    $brush.StartPoint = New-Object System.Windows.Point(0, 0)
    $brush.EndPoint   = New-Object System.Windows.Point(0, 1)
    $c = { [System.Windows.Media.ColorConverter]::ConvertFromString($args[0]) }
    $brush.GradientStops.Add((New-Object System.Windows.Media.GradientStop((& $c $topColor), 0.0)))
    $brush.GradientStops.Add((New-Object System.Windows.Media.GradientStop((& $c $bottomColor), 1.0)))
    $brush.Freeze()
    return $brush
}

# --- Design (coordinates in a 256x256 space, mirrors icon.svg) -----------------

$shieldGeo = ConvertFrom-PathData 'M128 14 C166 32 196 40 220 44 C220 134 196 196 128 242 C60 196 36 134 36 44 C60 40 90 32 128 14 Z'
$shineGeo  = ConvertFrom-PathData 'M128 14 C166 32 196 40 220 44 C219 80 214 108 204 132 C158 148 98 148 52 132 C42 108 37 80 36 44 C60 40 90 32 128 14 Z'

$shieldBrush   = New-VerticalBrush '#5FB2F7' '#0A5AB0'
$shineBrush    = New-VerticalBrush '#47FFFFFF' '#00FFFFFF'
$barBrush      = New-VerticalBrush '#FFFFFF' '#D9EDFF'
$outlinePen    = New-Object System.Windows.Media.Pen((New-Object System.Windows.Media.SolidColorBrush([System.Windows.Media.ColorConverter]::ConvertFromString('#8C063466'))), 7.0)
$outlinePen.Freeze()

$bars = @(
    (New-Object System.Windows.Rect(74,  136, 26, 40)),
    (New-Object System.Windows.Rect(115, 102, 26, 74)),
    (New-Object System.Windows.Rect(156, 70,  26, 106))
)

function Render-IconPng([int]$size) {
    $group = New-Object System.Windows.Media.DrawingGroup
    $scale = $size / 256.0
    $group.Transform = New-Object System.Windows.Media.ScaleTransform($scale, $scale)

    $dc = $group.Open()
    $dc.DrawGeometry($shieldBrush, $outlinePen, $shieldGeo)
    $dc.DrawGeometry($shineBrush, $null, $shineGeo)
    foreach ($r in $bars) {
        $dc.DrawRoundedRectangle($barBrush, $null, $r, 5.0, 5.0)
    }
    $dc.Close()

    $visual = New-Object System.Windows.Media.DrawingVisual
    $vdc = $visual.RenderOpen()
    $vdc.DrawDrawing($group)
    $vdc.Close()

    $bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)

    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $ms = New-Object System.IO.MemoryStream
    $encoder.Save($ms)
    return ,$ms.ToArray()
}

# --- Render all frames ----------------------------------------------------------

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$frames = @{}
foreach ($s in $sizes) {
    $frames[$s] = Render-IconPng $s
}

# --- Write multi-resolution .ico (all frames are 32-bit PNG) --------------------

$fs = [System.IO.File]::Create($IcoPath)
$bw = New-Object System.IO.BinaryWriter($fs)

$bw.Write([uint16]0)              # reserved
$bw.Write([uint16]1)              # type: icon
$bw.Write([uint16]$sizes.Count)   # image count

$offset = 6 + 16 * $sizes.Count
foreach ($s in $sizes) {
    $bw.Write([byte]($s -band 0xFF))    # width  (256 is stored as 0)
    $bw.Write([byte]($s -band 0xFF))    # height (256 is stored as 0)
    $bw.Write([byte]0)                  # palette colors
    $bw.Write([byte]0)                  # reserved
    $bw.Write([uint16]1)                # color planes
    $bw.Write([uint16]32)               # bits per pixel
    $bw.Write([uint32]$frames[$s].Length)
    $bw.Write([uint32]$offset)
    $offset += $frames[$s].Length
}
foreach ($s in $sizes) {
    $bw.Write($frames[$s])
}
$bw.Close()

Write-Host "Wrote $IcoPath ($($sizes.Count) sizes: $($sizes -join ', '))"

# --- Previews -------------------------------------------------------------------

if ($PreviewDir) {
    foreach ($s in 32, 256) {
        $out = Join-Path $PreviewDir "icon-$s.png"
        [System.IO.File]::WriteAllBytes($out, $frames[$s])
        Write-Host "Wrote $out"
    }
}
