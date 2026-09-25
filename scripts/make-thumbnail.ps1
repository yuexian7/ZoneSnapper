# Build the store cover from a generated artwork file.
# ASCII ONLY in this file: a .ps1 with non-ASCII comments has eaten statements three times
# (see the CS2 modding playbook, v1.1 addendum 7). Keep it that way.
#
# Why a script instead of just copying the artwork:
#   1) the generated PNG is 2.36 MB and the Paradox server rejects any single image over 2.1 MB
#      (and unlike Update, the first publish DOES validate), so it must be re-encoded;
#   2) the artwork carries an "AI generated" watermark in the bottom-right corner, which must not
#      end up on a store page - a bottom title bar covers exactly that region and gives branding;
#   3) the referenced file is a publish artefact, not a local gallery copy: if it is missing,
#      ModPublisher silently uploads Colossal's own placeholder instead (playbook v1.1 addendum 1).
param(
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$Target,
    [int]$Width = 1600,
    [int]$Height = 900,
    [int]$BarHeight = 132,
    [int]$JpegQuality = 88,
    [string]$Title = "ZONE SNAPPER",
    [string]$Subtitle = "Area borders that snap to your roads"
)

$ErrorActionPreference = "Stop"

# Load System.Drawing BY PATH. On 64-bit Windows PowerShell "Add-Type -AssemblyName System.Drawing"
# returns without error yet leaves [System.Drawing.Image] unresolvable (observed 2026-09-25 on this
# machine: 32-bit resolved it, 64-bit did not), so the name form alone is not enough.
$sdName = if ([Environment]::Is64BitProcess) { "Framework64" } else { "Framework" }
$sdPath = Join-Path $env:WINDIR ("Microsoft.NET\{0}\v4.0.30319\System.Drawing.dll" -f $sdName)
if (Test-Path -LiteralPath $sdPath) { Add-Type -Path $sdPath } else { Add-Type -AssemblyName System.Drawing }

$srcFull = (Resolve-Path -LiteralPath $Source).Path
$dstDir = Split-Path -Parent ([System.IO.Path]::GetFullPath($Target))
if ($dstDir -and -not (Test-Path -LiteralPath $dstDir)) {
    New-Item -ItemType Directory -Path $dstDir -Force | Out-Null
}
$dstFull = [System.IO.Path]::GetFullPath($Target)

$src = [System.Drawing.Image]::FromFile($srcFull)
try {
    # Cover-crop to the target aspect ratio, centered, then scale.
    $srcRatio = $src.Width / [double]$src.Height
    $dstRatio = $Width / [double]$Height
    if ($srcRatio -gt $dstRatio) {
        $cropH = [int][Math]::Round($src.Width / $dstRatio)
        $cropW = $src.Width
        $cropX = 0
        $cropY = [int](($src.Height - $cropH) / 2)
    }
    else {
        $cropW = [int][Math]::Round($src.Height * $dstRatio)
        $cropH = $src.Height
        $cropX = [int](($src.Width - $cropW) / 2)
        $cropY = 0
    }

    $canvas = New-Object System.Drawing.Bitmap($Width, $Height, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [System.Drawing.Graphics]::FromImage($canvas)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.DrawImage($src, (New-Object System.Drawing.Rectangle(0, 0, $Width, $Height)),
        (New-Object System.Drawing.Rectangle($cropX, $cropY, $cropW, $cropH)),
        [System.Drawing.GraphicsUnit]::Pixel)

    # Bottom title bar: also hides the watermark that sits in the bottom-right corner, so it must be
    # OPAQUE (a translucent gradient let the "AI generated" mark show through on the first render).
    $barRect = New-Object System.Drawing.Rectangle(0, ($Height - $BarHeight), $Width, $BarHeight)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $barRect,
        [System.Drawing.Color]::FromArgb(255, 6, 14, 24),
        [System.Drawing.Color]::FromArgb(255, 10, 20, 32),
        [System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
    $g.FillRectangle($brush, $barRect)
    $accent = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 120, 220, 210))
    $g.FillRectangle($accent, 0, ($Height - $BarHeight), $Width, 3)

    $family = "Arial"
    foreach ($probe in @("Segoe UI", "Verdana", "Arial")) {
        $f = New-Object System.Drawing.FontFamily($probe)
        if ($f -and -not ([string]::IsNullOrEmpty($f.Name))) { $family = $probe; break }
    }

    $titleFont = New-Object System.Drawing.Font($family, 46, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $subFont = New-Object System.Drawing.Font($family, 22, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 245, 250, 252))
    $dim = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(225, 176, 196, 206))

    $textY = ($Height - $BarHeight) + 24
    $g.DrawString($Title, $titleFont, $white, 44, $textY)
    $g.DrawString($Subtitle, $subFont, $dim, 46, ($textY + 62))

    # JPEG encoder with an explicit quality setting.
    $codec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() |
        Where-Object { $_.MimeType -eq "image/jpeg" } | Select-Object -First 1
    $params = New-Object System.Drawing.Imaging.EncoderParameters(1)
    $params.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter(
        [System.Drawing.Imaging.Encoder]::Quality, [long]$JpegQuality)
    $canvas.Save($dstFull, $codec, $params)

    $bytes = (Get-Item -LiteralPath $dstFull).Length
    Write-Output ("cover written: {0}  {1}x{2}  {3} bytes ({4:N2} MB)" -f `
            $dstFull, $Width, $Height, $bytes, ($bytes / 1MB))
    if ($bytes -gt 2100000) { throw "cover is over the 2.1 MB per-image server limit" }
}
finally {
    if ($g) { $g.Dispose() }
    if ($canvas) { $canvas.Dispose() }
    if ($src) { $src.Dispose() }
}
