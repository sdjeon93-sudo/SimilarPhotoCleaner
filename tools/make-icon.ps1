# 앱 아이콘(app.ico)을 그린다. 사용법: make-icon.ps1 <out.ico>
param([string]$Out)
Add-Type -AssemblyName System.Drawing

function RoundRect([System.Drawing.Drawing2D.GraphicsPath]$p, [float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p.AddArc($x, $y, 2 * $r, 2 * $r, 180, 90); $p.AddArc($x + $w - 2 * $r, $y, 2 * $r, 2 * $r, 270, 90)
    $p.AddArc($x + $w - 2 * $r, $y + $h - 2 * $r, 2 * $r, 2 * $r, 0, 90); $p.AddArc($x, $y + $h - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $p.CloseFigure()
}

function Draw([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.ScaleTransform($size / 256.0, $size / 256.0)
    $bg = New-Object System.Drawing.Drawing2D.GraphicsPath; RoundRect $bg 8 8 240 240 52
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(37, 99, 235))), $bg)
    # 뒤쪽 사진(겹친 사진)
    $back = New-Object System.Drawing.Drawing2D.GraphicsPath; RoundRect $back 70 48 136 104 14
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(150, 255, 255, 255))), $back)
    # 앞쪽 사진
    $front = New-Object System.Drawing.Drawing2D.GraphicsPath; RoundRect $front 44 84 144 112 14
    $g.FillPath([System.Drawing.Brushes]::White, $front)
    $g.FillPolygon((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(96, 165, 250))),
        [System.Drawing.PointF[]]@((New-Object System.Drawing.PointF 58, 182), (New-Object System.Drawing.PointF 100, 130),
            (New-Object System.Drawing.PointF 130, 160), (New-Object System.Drawing.PointF 150, 142), (New-Object System.Drawing.PointF 176, 182)))
    $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(251, 191, 36))), 140, 100, 24, 24)
    # 초록 체크 배지
    $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(22, 163, 74))), 158, 158, 76, 76)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), 12
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    $g.DrawLines($pen, [System.Drawing.PointF[]]@((New-Object System.Drawing.PointF 178, 197), (New-Object System.Drawing.PointF 192, 211), (New-Object System.Drawing.PointF 216, 182)))
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $script:images.Add($ms.ToArray())
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$script:images = New-Object 'System.Collections.Generic.List[byte[]]'
foreach ($s in $sizes) { Draw $s }
$fs = [System.IO.File]::Create($Out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $images[$i].Length
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]$len); $w.Write([UInt32]$offset)
    $offset += $len
}
foreach ($img in $images) { $w.Write($img) }
$w.Close()
"ok"
