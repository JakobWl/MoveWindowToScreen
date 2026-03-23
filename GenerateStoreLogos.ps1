Add-Type -AssemblyName System.Drawing

function New-StoreLogo {
    param(
        [int]$Width,
        [int]$Height,
        [string]$OutputPath,
        [System.Drawing.Image]$Icon,
        [string]$AppName = "Move Window`nTo Screen"
    )

    $bmp = New-Object System.Drawing.Bitmap($Width, $Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic

    # Background gradient (dark blue to purple)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point(0, 0)),
        (New-Object System.Drawing.Point($Width, $Height)),
        [System.Drawing.Color]::FromArgb(30, 30, 60),
        [System.Drawing.Color]::FromArgb(60, 30, 80)
    )
    $g.FillRectangle($brush, 0, 0, $Width, $Height)

    # Draw icon centered in upper portion
    $iconSize = [Math]::Min($Width, $Height) * 0.35
    $iconX = ($Width - $iconSize) / 2
    $iconY = if ($Width -eq $Height) { $Height * 0.15 } else { $Height * 0.2 }
    $g.DrawImage($Icon, [int]$iconX, [int]$iconY, [int]$iconSize, [int]$iconSize)

    # Draw app name text below icon
    $fontSize = [Math]::Min($Width, $Height) * 0.07
    $font = New-Object System.Drawing.Font("Segoe UI", [float]$fontSize, [System.Drawing.FontStyle]::Bold)
    $textBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $format = New-Object System.Drawing.StringFormat
    $format.Alignment = [System.Drawing.StringAlignment]::Center
    $format.LineAlignment = [System.Drawing.StringAlignment]::Near

    $textY = $iconY + $iconSize + ($Height * 0.05)
    $textRect = New-Object System.Drawing.RectangleF(0, [float]$textY, [float]$Width, [float]($Height - $textY))
    $g.DrawString($AppName, $font, $textBrush, $textRect, $format)

    # Subtle monitor icon decoration at bottom
    $smallSize = [Math]::Min($Width, $Height) * 0.04
    $monColor = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(80, 255, 255, 255))
    $monPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(80, 255, 255, 255), 2)

    # Two small monitor outlines at the bottom
    $monY = $Height * 0.85
    $mon1X = $Width * 0.35
    $mon2X = $Width * 0.55
    $g.DrawRectangle($monPen, [int]$mon1X, [int]$monY, [int]$smallSize, [int]($smallSize * 0.7))
    $g.DrawRectangle($monPen, [int]$mon2X, [int]$monY, [int]$smallSize, [int]($smallSize * 0.7))

    # Arrow between monitors
    $arrowPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(120, 255, 255, 255), 2)
    $arrowY = $monY + ($smallSize * 0.35)
    $g.DrawLine($arrowPen, [int]($mon1X + $smallSize + 4), [int]$arrowY, [int]($mon2X - 4), [int]$arrowY)
    # Arrowhead
    $g.DrawLine($arrowPen, [int]($mon2X - 4), [int]$arrowY, [int]($mon2X - 10), [int]($arrowY - 5))
    $g.DrawLine($arrowPen, [int]($mon2X - 4), [int]$arrowY, [int]($mon2X - 10), [int]($arrowY + 5))

    $bmp.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png)

    $g.Dispose()
    $bmp.Dispose()
    $brush.Dispose()
    $font.Dispose()
    $textBrush.Dispose()
    $format.Dispose()

    Write-Host "Created: $OutputPath ($Width x $Height)"
}

# Load the app icon from largest available PNG
$iconPath = "MoveWindowToScreen\Images\Square310x310Logo.png"
$iconBmp = [System.Drawing.Image]::FromFile((Resolve-Path $iconPath).Path)

$outDir = "StoreAssets"
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

# 9:16 Poster art (720x1080 and 1440x2160)
New-StoreLogo -Width 720 -Height 1080 -OutputPath "$outDir\PosterArt_720x1080.png" -Icon $iconBmp
New-StoreLogo -Width 1440 -Height 2160 -OutputPath "$outDir\PosterArt_1440x2160.png" -Icon $iconBmp

# 1:1 Box art (1080x1080 and 2160x2160)
New-StoreLogo -Width 1080 -Height 1080 -OutputPath "$outDir\BoxArt_1080x1080.png" -Icon $iconBmp
New-StoreLogo -Width 2160 -Height 2160 -OutputPath "$outDir\BoxArt_2160x2160.png" -Icon $iconBmp

# 1:1 App tile icon (300x300)
New-StoreLogo -Width 300 -Height 300 -OutputPath "$outDir\AppTileIcon_300x300.png" -Icon $iconBmp

$iconBmp.Dispose()

Write-Host "`nAll Store logos generated in '$outDir' folder!"
Write-Host "Upload these in Partner Center:"
Write-Host "  - 9:16 Poster art:  PosterArt_720x1080.png or PosterArt_1440x2160.png"
Write-Host "  - 1:1 Box art:      BoxArt_1080x1080.png or BoxArt_2160x2160.png"
Write-Host "  - 1:1 App tile:     AppTileIcon_300x300.png"
