# app.ico:  .\make-icon.ps1 -OutIco ..\app.ico -OutPreview app.png
# file.ico: .\make-icon.ps1 -OutIco ..\file.ico -OutPreview file.png -Kind file   (icon for .md files)
param([string]$OutIco, [string]$OutPreview, [ValidateSet('app', 'file')][string]$Kind = 'app')
Add-Type -AssemblyName System.Drawing

function Draw-FileIcon([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $g.ScaleTransform($size / 256.0, $size / 256.0)

    # page with a folded top-right corner
    $fold = 56; $l = 40; $t = 12; $rgt = 216; $b = 244
    $page = New-Object System.Drawing.Drawing2D.GraphicsPath
    $page.AddLines([System.Drawing.Point[]]@(
        [System.Drawing.Point]::new($l, $t), [System.Drawing.Point]::new($rgt - $fold, $t),
        [System.Drawing.Point]::new($rgt, $t + $fold), [System.Drawing.Point]::new($rgt, $b),
        [System.Drawing.Point]::new($l, $b)))
    $page.CloseFigure()
    $g.FillPath([System.Drawing.Brushes]::White, $page)
    $edge = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 150, 160, 175)), 8
    $edge.LineJoin = 'Round'
    $g.DrawPath($edge, $page)
    $g.FillPolygon((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 205, 212, 222))), [System.Drawing.Point[]]@(
        [System.Drawing.Point]::new($rgt - $fold, $t), [System.Drawing.Point]::new($rgt - $fold, $t + $fold), [System.Drawing.Point]::new($rgt, $t + $fold)))
    $g.DrawLines($edge, [System.Drawing.Point[]]@(
        [System.Drawing.Point]::new($rgt - $fold, $t), [System.Drawing.Point]::new($rgt - $fold, $t + $fold), [System.Drawing.Point]::new($rgt, $t + $fold)))

    # blue Markdown mark on the lower part of the page
    $blue = [System.Drawing.Color]::FromArgb(255, 37, 99, 235)
    $pen = New-Object System.Drawing.Pen $blue, 18
    $pen.LineJoin = 'Miter'; $pen.StartCap = 'Square'; $pen.EndCap = 'Square'
    $g.DrawLines($pen, [System.Drawing.Point[]]@(
        [System.Drawing.Point]::new(72, 208), [System.Drawing.Point]::new(72, 130),
        [System.Drawing.Point]::new(106, 166), [System.Drawing.Point]::new(140, 130),
        [System.Drawing.Point]::new(140, 208)))
    $pen.EndCap = 'Flat'
    $g.DrawLine($pen, 182, 124, 182, 176)
    $g.FillPolygon((New-Object System.Drawing.SolidBrush $blue), [System.Drawing.Point[]]@(
        [System.Drawing.Point]::new(156, 170), [System.Drawing.Point]::new(208, 170), [System.Drawing.Point]::new(182, 210)))

    $g.Dispose()
    return $bmp
}

function Draw-Icon([int]$size) {
    if ($Kind -eq 'file') { return Draw-FileIcon $size }
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $g.ScaleTransform($size / 256.0, $size / 256.0)

    # rounded-square background with a vertical blue gradient
    $r = 52; $x = 12; $y = 12; $w = 232; $h = 232
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($x, $y, $r, $r, 180, 90); $path.AddArc($x + $w - $r, $y, $r, $r, 270, 90)
    $path.AddArc($x + $w - $r, $y + $h - $r, $r, $r, 0, 90); $path.AddArc($x, $y + $h - $r, $r, $r, 90, 90)
    $path.CloseFigure()
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.Point]::new(0, 12)), ([System.Drawing.Point]::new(0, 244)),
        ([System.Drawing.Color]::FromArgb(255, 79, 140, 255)), ([System.Drawing.Color]::FromArgb(255, 37, 70, 214))
    $g.FillPath($grad, $path)
    $g.TranslateTransform(-10, 2)

    # Markdown mark: "M" and a down arrow, in white
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), 24
    $pen.LineJoin = 'Miter'; $pen.StartCap = 'Square'; $pen.EndCap = 'Square'
    $g.DrawLines($pen, [System.Drawing.Point[]]@(
        [System.Drawing.Point]::new(56, 180), [System.Drawing.Point]::new(56, 82),
        [System.Drawing.Point]::new(100, 128), [System.Drawing.Point]::new(144, 82),
        [System.Drawing.Point]::new(144, 180)))
    $pen.EndCap = 'Flat'
    $g.DrawLine($pen, 198, 76, 198, 140)
    $g.FillPolygon([System.Drawing.Brushes]::White, [System.Drawing.Point[]]@(
        [System.Drawing.Point]::new(164, 132), [System.Drawing.Point]::new(232, 132), [System.Drawing.Point]::new(198, 182)))

    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
    $bmp = Draw-Icon $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($s -eq 256) { $bmp.Save($OutPreview, [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
    , $ms.ToArray()
}

# ICO container with PNG-compressed entries
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$len); $bw.Write([UInt32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($OutIco, $out.ToArray())
"wrote $OutIco ($($out.Length) bytes)"

