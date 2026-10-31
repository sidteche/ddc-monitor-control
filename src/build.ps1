# Builds SyncMasterControl.exe with the C# compiler that ships with Windows (.NET Framework 4).
# Run:  powershell -ExecutionPolicy Bypass -File src\build.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$src  = $PSScriptRoot
$root = Split-Path $src -Parent
$ico  = Join-Path $src 'app.ico'
$exe  = Join-Path $root 'SyncMasterControl.exe'
$csc  = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'

function New-IconPng([int]$size) {
    $bmp = New-Object Drawing.Bitmap $size, $size
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $s = $size / 256.0
    function RoundPath($x, $y, $w, $h, $r) {
        $p = New-Object Drawing.Drawing2D.GraphicsPath
        $d = $r * 2
        $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
        $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
        $p.CloseFigure(); $p
    }
    # Rounded blue tile
    $bg = New-Object Drawing.Drawing2D.LinearGradientBrush (New-Object Drawing.Point 0, 0), (New-Object Drawing.Point 0, $size),
        ([Drawing.Color]::FromArgb(0x5B, 0x9B, 0xFF)), ([Drawing.Color]::FromArgb(0x2F, 0x5F, 0xD8))
    $g.FillPath($bg, (RoundPath (8*$s) (8*$s) (240*$s) (240*$s) (52*$s)))
    # Monitor
    $white = New-Object Drawing.SolidBrush ([Drawing.Color]::White)
    $g.FillPath($white, (RoundPath (40*$s) (56*$s) (176*$s) (118*$s) (14*$s)))
    $screen = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(0x1E, 0x21, 0x28))
    $g.FillPath($screen, (RoundPath (52*$s) (68*$s) (152*$s) (94*$s) (6*$s)))
    $g.FillRectangle($white, 116*$s, 174*$s, 24*$s, 22*$s)
    $g.FillPath($white, (RoundPath (84*$s) (194*$s) (88*$s) (14*$s) (7*$s)))
    # Sun (brightness) on the screen
    $sun = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(0xFF, 0xC8, 0x3D))
    $cx = 128*$s; $cy = 115*$s
    $g.FillEllipse($sun, $cx - 17*$s, $cy - 17*$s, 34*$s, 34*$s)
    $pen = New-Object Drawing.Pen $sun.Color, ([Math]::Max(1, 7*$s))
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    for ($a = 0; $a -lt 360; $a += 45) {
        $rad = $a * [Math]::PI / 180
        $g.DrawLine($pen, $cx + [Math]::Cos($rad)*26*$s, $cy + [Math]::Sin($rad)*26*$s, $cx + [Math]::Cos($rad)*36*$s, $cy + [Math]::Sin($rad)*36*$s)
    }
    $g.Dispose()
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

if (-not (Test-Path $ico)) {
    # .ico with PNG-compressed images (supported since Windows Vista)
    $sizes = 256, 64, 48, 32, 24, 16
    $pngs = $sizes | ForEach-Object { , (New-IconPng $_) }
    $ms = New-Object IO.MemoryStream
    $w = New-Object IO.BinaryWriter $ms
    $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $d = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
        $w.Write([byte]$d); $w.Write([byte]$d); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([uint16]1); $w.Write([uint16]32)
        $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
        $offset += $pngs[$i].Length
    }
    foreach ($p in $pngs) { $w.Write($p) }
    [IO.File]::WriteAllBytes($ico, $ms.ToArray())
    Write-Host "Created icon: $ico"
}

& $csc /nologo /target:winexe /platform:x64 /optimize+ /out:$exe /win32icon:$ico `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll (Join-Path $src 'SyncMasterControl.cs')
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Write-Host "Built: $exe"
