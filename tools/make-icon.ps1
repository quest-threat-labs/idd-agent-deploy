<#
    Generates src/HybridAgentDeploy.Gui/appicon.ico.

    Checked in and re-runnable rather than a one-off, so the icon can be regenerated or
    adjusted without anyone having to reverse-engineer how it was made. The .ico itself is
    committed — the build must not depend on this script having been run.

    The mark is a downward arrow into a bar: "install onto a machine". Chosen because at
    16 pixels almost nothing else survives. Anything with interior detail — a shield outline,
    lettering, a domain-controller glyph — turns to mush at tab and taskbar size, which is
    where an operator actually has to pick this window out from a dozen others.
#>

[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\src\HybridAgentDeploy.Gui\appicon.ico')
)

Add-Type -AssemblyName System.Drawing

# Sizes Windows actually asks for: taskbar/tab, Explorer small, Explorer medium, and the
# 256px entry used by large icon views and the Alt-Tab switcher.
$sizes = 16, 24, 32, 48, 64, 128, 256

$background = [System.Drawing.Color]::FromArgb(255, 20, 63, 110)   # deep blue
$mark       = [System.Drawing.Color]::FromArgb(255, 255, 255, 255)

function New-Frame([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded square, inset slightly so the corners are not clipped by the icon bounds.
    $inset  = [Math]::Max(1, [int]($size * 0.04))
    $side   = $size - (2 * $inset)
    $radius = [Math]::Max(2, [int]($size * 0.18))

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc($inset, $inset, $d, $d, 180, 90)
    $path.AddArc($inset + $side - $d, $inset, $d, $d, 270, 90)
    $path.AddArc($inset + $side - $d, $inset + $side - $d, $d, $d, 0, 90)
    $path.AddArc($inset, $inset + $side - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $brush = New-Object System.Drawing.SolidBrush $background
    $g.FillPath($brush, $path)

    # Arrow shaft and head, then the bar it lands on.
    $markBrush = New-Object System.Drawing.SolidBrush $mark
    $cx = $size / 2.0

    $shaftW = [Math]::Max(2.0, $size * 0.13)
    $shaftTop = $size * 0.20
    $shaftBottom = $size * 0.50
    $g.FillRectangle($markBrush, [float]($cx - $shaftW / 2), [float]$shaftTop, [float]$shaftW, [float]($shaftBottom - $shaftTop))

    $headW = $size * 0.42
    $headTip = $size * 0.68
    $head = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF ([float]($cx - $headW / 2), [float]$shaftBottom)),
        (New-Object System.Drawing.PointF ([float]($cx + $headW / 2), [float]$shaftBottom)),
        (New-Object System.Drawing.PointF ([float]$cx,               [float]$headTip))
    )
    $g.FillPolygon($markBrush, $head)

    $barH = [Math]::Max(2.0, $size * 0.10)
    $barW = $size * 0.52
    $g.FillRectangle($markBrush, [float]($cx - $barW / 2), [float]($size * 0.76), [float]$barW, [float]$barH)

    $g.Dispose(); $brush.Dispose(); $markBrush.Dispose(); $path.Dispose()
    return $bmp
}

# Each frame is stored as a PNG. Windows Vista and later read PNG-compressed ICO entries at
# every size, and it keeps the file small enough to read in a diff listing.
$frames = @()
foreach ($size in $sizes) {
    $bmp = New-Frame $size
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames += [pscustomobject]@{ Size = $size; Bytes = $ms.ToArray() }
    $ms.Dispose(); $bmp.Dispose()
}

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out

# ICONDIR
$w.Write([uint16]0)                 # reserved
$w.Write([uint16]1)                 # type: icon
$w.Write([uint16]$frames.Count)

# ICONDIRENTRY per frame. Offsets follow the directory, so compute the header size first.
$offset = 6 + (16 * $frames.Count)
foreach ($f in $frames) {
    $w.Write([byte]$(if ($f.Size -ge 256) { 0 } else { $f.Size }))   # 0 means 256
    $w.Write([byte]$(if ($f.Size -ge 256) { 0 } else { $f.Size }))
    $w.Write([byte]0)               # palette entries
    $w.Write([byte]0)               # reserved
    $w.Write([uint16]1)             # colour planes
    $w.Write([uint16]32)            # bits per pixel
    $w.Write([uint32]$f.Bytes.Length)
    $w.Write([uint32]$offset)
    $offset += $f.Bytes.Length
}

foreach ($f in $frames) { $w.Write($f.Bytes) }

$w.Flush()
$resolved = [System.IO.Path]::GetFullPath($OutputPath)
[System.IO.File]::WriteAllBytes($resolved, $out.ToArray())
$w.Dispose(); $out.Dispose()

Write-Host ("wrote {0} ({1:N0} bytes, {2} frames: {3})" -f $resolved, (Get-Item $resolved).Length, $frames.Count, ($sizes -join ', '))
