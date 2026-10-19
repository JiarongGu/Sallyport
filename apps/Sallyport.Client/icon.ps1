# Draws Sallyport.ico, the client's and setup's icon (Explorer, the Start menu, Windows' installed apps), from the same
# mark the tray and the window draw: an arched door - a sallyport is a gate - white on the brand blue.
#   powershell -File apps\Sallyport.Client\icon.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
$frames = foreach ($size in $sizes) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $disc = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0x5B, 0x9D, 0xFF))
    $g.FillEllipse($disc, 0.5, 0.5, $size - 1.5, $size - 1.5)
    $w = $size * 0.38; $x = ($size - $w) / 2; $top = $size * 0.27; $bottom = $size * 0.78
    $door = New-Object System.Drawing.Drawing2D.GraphicsPath
    $door.AddArc($x, $top, $w, $w, 180, 180)
    $door.AddLine($x + $w, $top + $w / 2, $x + $w, $bottom)
    $door.AddLine($x + $w, $bottom, $x, $bottom)
    $door.CloseFigure()
    $g.FillPath([System.Drawing.Brushes]::White, $door)
    $g.Dispose()
    $png = New-Object System.IO.MemoryStream
    $bitmap.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    , $png.ToArray()
}

# ICONDIR (6 bytes), an ICONDIRENTRY (16 bytes) per frame, then each frame as a PNG: Windows reads PNG frames at every size.
$out = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $out
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $edge = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte]$edge); $writer.Write([byte]$edge); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
    $offset += $frames[$i].Length
}
foreach ($frame in $frames) { $writer.Write($frame) }
$writer.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'Sallyport.ico'), $out.ToArray())
