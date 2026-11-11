# Genera app.ico (PNG multi-risoluzione) per l'eseguibile.
param([string]$Out = (Join-Path $PSScriptRoot '..\app.ico'))
Add-Type -AssemblyName System.Drawing

function Draw-Icon([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $u = $s / 16.0
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r = New-Object System.Drawing.RectangleF (0.5*$u), (2.5*$u), (15*$u), (11*$u)
    $d = 4.4*$u
    $p.AddArc($r.X, $r.Y, $d, $d, 180, 90); $p.AddArc($r.Right-$d, $r.Y, $d, $d, 270, 90)
    $p.AddArc($r.Right-$d, $r.Bottom-$d, $d, $d, 0, 90); $p.AddArc($r.X, $r.Bottom-$d, $d, $d, 90, 90); $p.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255,28,28,34))), $p)
    $y = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255,255,204,0))
    for ($i = 0; $i -lt 4; $i++) {
        $g.FillRectangle($y, [float]((2 + $i*3.2)*$u), [float](4.3*$u), [float](2.4*$u), [float](2.2*$u))
        $g.FillRectangle($y, [float]((2 + $i*3.2)*$u), [float](7.2*$u), [float](2.4*$u), [float](2.2*$u))
    }
    $g.FillRectangle($y, [float](4.2*$u), [float](10.1*$u), [float](7.6*$u), [float](1.7*$u))
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return ,$ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 256
$pngs = @(); foreach ($s in $sizes) { $pngs += ,(Draw-Icon $s) }
$fs = [System.IO.File]::Create($Out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $b = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$b); $w.Write([byte]$b); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($png in $pngs) { $w.Write($png) }
$w.Close()
Write-Host "Creato $Out"
