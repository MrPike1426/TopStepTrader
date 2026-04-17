Add-Type -AssemblyName System.Drawing

function Make-Frame {
    param([int]$Size)

    $bmp = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode    = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic

    $bg = [System.Drawing.Color]::FromArgb(255, 10, 14, 30)
    $g.Clear($bg)

    $green = [System.Drawing.Color]::FromArgb(255,  0, 230, 118)
    $cyan  = [System.Drawing.Color]::FromArgb(255,  0, 200, 230)
    $dim   = [System.Drawing.Color]::FromArgb(180,  0, 160,  70)

    $s = [float]$Size

    $barCount = 4
    $pad      = [float]($s * 0.08)
    $gap      = [float]($s * 0.05)
    $totalW   = [float]($s - 2.0 * $pad)
    $barW     = [float](($totalW - ($barCount - 1) * $gap) / $barCount)
    $baseY    = [float]($s * 0.90)

    $heights = @(0.28, 0.46, 0.65, 0.87)

    for ($i = 0; $i -lt $barCount; $i++) {
        $x = [float]($pad + $i * ($barW + $gap))
        $h = [float]($s * $heights[$i])
        $y = [float]($baseY - $h)

        $rect = New-Object System.Drawing.RectangleF($x, $y, $barW, $h)
        $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            $rect, $dim, $green,
            [System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
        $g.FillRectangle($brush, $rect)
        $brush.Dispose()
    }

    # Arrow above tallest bar (32px+)
    if ($Size -ge 32) {
        $lastX  = [float]($pad + 3.0 * ($barW + $gap))
        $lastH  = [float]($s * $heights[3])
        $cx     = [float]($lastX + $barW / 2.0)
        $arrowY = [float]($baseY - $lastH - $s * 0.03)

        $aw = [float]($barW * 0.95)
        $ah = [float]($s * 0.13)

        $arrowPts = @(
            [System.Drawing.PointF]::new($cx,           $arrowY - $ah),
            [System.Drawing.PointF]::new($cx + $aw/2.0, $arrowY),
            [System.Drawing.PointF]::new($cx - $aw/2.0, $arrowY)
        )
        $arrowBrush = New-Object System.Drawing.SolidBrush($cyan)
        $g.FillPolygon($arrowBrush, $arrowPts)
        $arrowBrush.Dispose()
    }

    # Baseline rule
    $ruleAlpha = if ($Size -ge 48) { 140 } else { 80 }
    $ruleThick = [float]([Math]::Max(1, $Size / 48))
    $rulePen   = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb($ruleAlpha, 255, 255, 255), $ruleThick)
    $g.DrawLine($rulePen, $pad, $baseY, ($s - $pad), $baseY)
    $rulePen.Dispose()

    $g.Dispose()
    return $bmp
}

function To-PngBytes($bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return ,$ms.ToArray()
}

$sizes  = @(16, 32, 48, 256)
$frames = @{}
foreach ($sz in $sizes) {
    $bmp = Make-Frame $sz
    $frames[$sz] = To-PngBytes $bmp
    $bmp.Dispose()
    Write-Host "  Drew ${sz}x${sz}"
}

# Build ICO binary
$count   = $sizes.Count
$dataOff = 6 + $count * 16

$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)

$bw.Write([uint16]0)       # reserved
$bw.Write([uint16]1)       # type=icon
$bw.Write([uint16]$count)

$offset = [uint32]$dataOff
foreach ($sz in $sizes) {
    $bytes = $frames[$sz]
    $dim   = if ($sz -eq 256) { [byte]0 } else { [byte]$sz }
    $bw.Write($dim)                     # width
    $bw.Write($dim)                     # height
    $bw.Write([byte]0)                  # colorCount
    $bw.Write([byte]0)                  # reserved
    $bw.Write([uint16]1)                # planes
    $bw.Write([uint16]32)               # bpp
    $bw.Write([uint32]$bytes.Length)    # imageSize
    $bw.Write([uint32]$offset)          # offset
    $offset += [uint32]$bytes.Length
}

foreach ($sz in $sizes) {
    $bw.Write($frames[$sz])
}

$bw.Flush()
$iconBytes = $ms.ToArray()
$bw.Dispose()
$ms.Dispose()

$outDir  = Split-Path $PSCommandPath -Parent
$outPath = Join-Path $outDir "app.ico"
[System.IO.File]::WriteAllBytes($outPath, $iconBytes)

Write-Host "Saved: $outPath  ($([Math]::Round($iconBytes.Length/1KB,1)) KB)"
