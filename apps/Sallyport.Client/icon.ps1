# Draws Sallyport.ico, the client's and setup's icon (Explorer, the Start menu, Windows' installed apps), from the same
# mark the window, the tray and the page draw (BrandMark.cs, Chrome.tsx): a gateway - a sallyport is a fortress's gate -
# with a way out rising through it, white on a rounded tile in the brand's gradient. Every number is that 32-unit grid.
#   powershell -File apps\Sallyport.Client\icon.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
$frames = foreach ($size in $sizes) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.ScaleTransform($size / 32, $size / 32)
    $tile = New-Object System.Drawing.Drawing2D.GraphicsPath
    $tile.AddArc(1, 1, 16, 16, 180, 90); $tile.AddArc(15, 1, 16, 16, 270, 90)
    $tile.AddArc(15, 15, 16, 16, 0, 90); $tile.AddArc(1, 15, 16, 16, 90, 90); $tile.CloseFigure()
    $fill = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 1, 1), (New-Object System.Drawing.PointF 31, 31),
        ([System.Drawing.Color]::FromArgb(0x3D, 0x8B, 0xFF)), ([System.Drawing.Color]::FromArgb(0x5B, 0x4B, 0xE0))
    $g.FillPath($fill, $tile)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), 2.75
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    $gate = New-Object System.Drawing.Drawing2D.GraphicsPath
    $gate.AddLine(8.5, 24, 8.5, 15.5); $gate.AddArc(8.5, 8, 15, 15, 180, 180); $gate.AddLine(23.5, 15.5, 23.5, 24)
    $g.DrawPath($pen, $gate)
    $g.DrawLine($pen, 16, 24.5, 16, 15.5)
    $g.DrawLines($pen, [System.Drawing.PointF[]]@((New-Object System.Drawing.PointF 13, 18.5), (New-Object System.Drawing.PointF 16, 15.5), (New-Object System.Drawing.PointF 19, 18.5)))
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
