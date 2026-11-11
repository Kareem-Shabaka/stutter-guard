param(
    # Optional: mirror the generated icons into this folder (for design preview).
    [string]$VaultDir = ""
)
# generate-icons.ps1: Generates app.png and multi-size app.ico for Stutter Guard
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

function Render-StutterGuard([int]$targetSize) {
    $dv = New-Object System.Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()

    $scale = $targetSize / 256.0
    $dc.PushTransform((New-Object System.Windows.Media.ScaleTransform($scale, $scale)))

    # 1. Base squircle
    $bgGrad = New-Object System.Windows.Media.LinearGradientBrush
    $bgGrad.StartPoint = New-Object System.Windows.Point(0.11, 0.08)
    $bgGrad.EndPoint = New-Object System.Windows.Point(0.89, 0.92)
    $bgGrad.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.Color]::FromRgb(0x1E, 0x26, 0x38), 0.0)))
    $bgGrad.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.Color]::FromRgb(0x0E, 0x12, 0x1B), 1.0)))
    $dc.DrawRoundedRectangle($bgGrad, $null, (New-Object System.Windows.Rect(16, 16, 224, 224)), 52, 52)

    # 1b. Specular rim highlight
    $rimGrad = New-Object System.Windows.Media.LinearGradientBrush
    $rimGrad.StartPoint = New-Object System.Windows.Point(0.5, 0.0)
    $rimGrad.EndPoint = New-Object System.Windows.Point(0.5, 1.0)
    $rimGrad.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.Color]::FromArgb(64, 255, 255, 255), 0.0)))
    $rimGrad.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.Color]::FromArgb(12, 255, 255, 255), 0.35)))
    $rimGrad.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.Color]::FromArgb(128, 0, 0, 0), 1.0)))
    $rimPen = New-Object System.Windows.Media.Pen($rimGrad, 1.5)
    $dc.DrawRoundedRectangle($null, $rimPen, (New-Object System.Windows.Rect(16.75, 16.75, 222.5, 222.5)), 51.25, 51.25)

    # 2. Ambient Under-glow
    $glowBrush = New-Object System.Windows.Media.RadialGradientBrush
    $glowBrush.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.Color]::FromArgb(60, 0, 145, 255), 0.0)))
    $glowBrush.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.Color]::FromArgb(0, 0, 145, 255), 1.0)))
    $dc.DrawEllipse($glowBrush, $null, (New-Object System.Windows.Point(128, 130)), 64, 52)

    # 3. Shield Main Body
    $shieldGeo = [System.Windows.Media.Geometry]::Parse("M 128,48 C 168,48 196,58 196,58 C 196,130 168,178 128,202 C 88,178 60,130 60,58 C 60,58 88,48 128,48 Z")
    $shieldGrad = New-Object System.Windows.Media.LinearGradientBrush
    $shieldGrad.StartPoint = New-Object System.Windows.Point(0.5, 0.0)
    $shieldGrad.EndPoint = New-Object System.Windows.Point(0.5, 1.0)
    $shieldGrad.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.Color]::FromRgb(0x00, 0x91, 0xFF), 0.0)))
    $shieldGrad.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.Color]::FromRgb(0x00, 0x47, 0xAB), 1.0)))
    $dc.DrawGeometry($shieldGrad, $null, $shieldGeo)

    # 3b. Left Facet
    $leftGeo = [System.Windows.Media.Geometry]::Parse("M 128,50 C 94,50 64,59 64,59 C 64,128 90,172 128,196 V 50 Z")
    $leftGrad = New-Object System.Windows.Media.LinearGradientBrush
    $leftGrad.StartPoint = New-Object System.Windows.Point(0.0, 0.0)
    $leftGrad.EndPoint = New-Object System.Windows.Point(1.0, 1.0)
    $leftGrad.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.Color]::FromArgb(95, 56, 189, 248), 0.0)))
    $leftGrad.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.Color]::FromArgb(15, 2, 132, 199), 1.0)))
    $dc.DrawGeometry($leftGrad, $null, $leftGeo)

    # 3c. Right Facet
    $rightGeo = [System.Windows.Media.Geometry]::Parse("M 128,50 C 162,50 192,59 192,59 C 192,128 166,172 128,196 V 50 Z")
    $rightGrad = New-Object System.Windows.Media.LinearGradientBrush
    $rightGrad.StartPoint = New-Object System.Windows.Point(1.0, 0.0)
    $rightGrad.EndPoint = New-Object System.Windows.Point(0.0, 1.0)
    $rightGrad.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.Color]::FromArgb(135, 0, 245, 255), 0.0)))
    $rightGrad.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.Color]::FromArgb(35, 2, 132, 199), 1.0)))
    $dc.DrawGeometry($rightGrad, $null, $rightGeo)

    # 3d. Inner Shield Rim Stroke
    $innerRimGeo = [System.Windows.Media.Geometry]::Parse("M 128,52 C 164,52 190,61 190,61 C 190,126 164,168 128,192 C 92,168 66,126 66,61 C 66,61 92,52 128,52 Z")
    $innerRimPen = New-Object System.Windows.Media.Pen(
        (New-Object System.Windows.Media.SolidColorBrush([System.Windows.Media.Color]::FromArgb(65, 255, 255, 255))),
        1.5
    )
    $dc.DrawGeometry($null, $innerRimPen, $innerRimGeo)

    # 4. Latency waveform
    $trenchGeo = [System.Windows.Media.Geometry]::Parse("M 36,128 L 80,128 L 92,90 L 104,158 L 116,128 L 220,128")
    $trenchPen = New-Object System.Windows.Media.Pen(
        (New-Object System.Windows.Media.SolidColorBrush([System.Windows.Media.Color]::FromArgb(195, 6, 9, 16))),
        9
    )
    $trenchPen.StartLineCap = [System.Windows.Media.PenLineCap]::Round
    $trenchPen.EndLineCap = [System.Windows.Media.PenLineCap]::Round
    $trenchPen.LineJoin = [System.Windows.Media.PenLineJoin]::Round
    $dc.DrawGeometry($null, $trenchPen, $trenchGeo)

    # Signal pulse stroke
    $pulseGrad = New-Object System.Windows.Media.LinearGradientBrush
    $pulseGrad.StartPoint = New-Object System.Windows.Point(0.0, 0.5)
    $pulseGrad.EndPoint = New-Object System.Windows.Point(1.0, 0.5)
    $pulseGrad.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.Color]::FromRgb(0xFF, 0x52, 0x52), 0.0)))
    $pulseGrad.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.Color]::FromRgb(0xFF, 0x52, 0x52), 0.36)))
    $pulseGrad.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.Color]::FromRgb(0x00, 0xF5, 0xFF), 0.46)))
    $pulseGrad.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.Color]::FromRgb(0x00, 0xF5, 0xFF), 1.0)))
    $pulsePen = New-Object System.Windows.Media.Pen($pulseGrad, 5)
    $pulsePen.StartLineCap = [System.Windows.Media.PenLineCap]::Round
    $pulsePen.EndLineCap = [System.Windows.Media.PenLineCap]::Round
    $pulsePen.LineJoin = [System.Windows.Media.PenLineJoin]::Round
    $dc.DrawGeometry($null, $pulsePen, $trenchGeo)

    # 5. Lock Node
    $nodeGlow = New-Object System.Windows.Media.RadialGradientBrush
    $nodeGlow.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.Color]::FromArgb(255, 255, 255, 255), 0.0)))
    $nodeGlow.GradientStops.Add((New-Object System.Windows.Media.GradientStop([System.Windows.Media.Color]::FromArgb(0, 0, 229, 255), 1.0)))
    $dc.DrawEllipse($nodeGlow, $null, (New-Object System.Windows.Point(116, 128)), 7, 7)
    $nodeCenter = New-Object System.Windows.Media.SolidColorBrush([System.Windows.Media.Color]::FromRgb(0x00, 0xE5, 0xFF))
    $dc.DrawEllipse($nodeCenter, $null, (New-Object System.Windows.Point(116, 128)), 3, 3)

    # 6. Microsecond calibration ticks
    $tickPen = New-Object System.Windows.Media.Pen(
        (New-Object System.Windows.Media.SolidColorBrush([System.Windows.Media.Color]::FromArgb(120, 255, 255, 255))),
        2
    )
    $tickPen.StartLineCap = [System.Windows.Media.PenLineCap]::Round
    $tickPen.EndLineCap = [System.Windows.Media.PenLineCap]::Round
    $dc.DrawLine($tickPen, (New-Object System.Windows.Point(144, 123)), (New-Object System.Windows.Point(144, 133)))
    $dc.DrawLine($tickPen, (New-Object System.Windows.Point(172, 123)), (New-Object System.Windows.Point(172, 133)))
    $dc.DrawLine($tickPen, (New-Object System.Windows.Point(200, 123)), (New-Object System.Windows.Point(200, 133)))

    $dc.Pop()
    $dc.Close()

    $rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($targetSize, $targetSize, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $rtb.Render($dv)

    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $frame = [System.Windows.Media.Imaging.BitmapFrame]::Create($rtb)
    $encoder.Frames.Add($frame)

    $ms = New-Object System.IO.MemoryStream
    $encoder.Save($ms)
    return $ms.ToArray()
}

$assetsDir = Join-Path $PSScriptRoot "src\DpcTweaker.App\Assets"
if (-not (Test-Path $assetsDir)) { New-Item -ItemType Directory -Path $assetsDir -Force | Out-Null }

# 1. Save 256x256 PNG
$png256 = Render-StutterGuard 256
[System.IO.File]::WriteAllBytes("$assetsDir\app.png", $png256)
Write-Host "app.png written ($($png256.Length) bytes)"

# 2. Build multi-resolution ICO
$sizes = @(256, 128, 64, 48, 32, 24, 16)
$images = @()
foreach ($sz in $sizes) {
    [byte[]]$bytes = Render-StutterGuard $sz
    Write-Host "Rendered $sz x $sz : $($bytes.Length) bytes"
    $images += [PSCustomObject]@{ Size = $sz; Bytes = $bytes }
}

$icoStream = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($icoStream)

# Header
$bw.Write([int16]0) # Reserved
$bw.Write([int16]1) # Type = 1 (Icon)
$bw.Write([int16]$images.Count) # Count

# Entries
$offset = 6 + (16 * $images.Count)
foreach ($img in $images) {
    $w = if ($img.Size -eq 256) { [byte]0 } else { [byte]$img.Size }
    $h = if ($img.Size -eq 256) { [byte]0 } else { [byte]$img.Size }
    $bw.Write([byte]$w)
    $bw.Write([byte]$h)
    $bw.Write([byte]0) # Color count
    $bw.Write([byte]0) # Reserved
    $bw.Write([int16]1) # Planes
    $bw.Write([int16]32) # Bit count
    $bw.Write([int32]$img.Bytes.Length)
    $bw.Write([int32]$offset)
    $offset += $img.Bytes.Length
}

# Image Data
foreach ($img in $images) {
    $icoStream.Write($img.Bytes, 0, $img.Bytes.Length)
}

$icoBytes = $icoStream.ToArray()
[System.IO.File]::WriteAllBytes("$assetsDir\app.ico", $icoBytes)
Write-Host "app.ico written ($($icoBytes.Length) bytes)"

$vaultDir = if ($VaultDir) { $VaultDir } else { "" }   # pass -VaultDir to mirror the icons into a folder
# Optional: copy the generated set into a design vault. Pass -VaultDir to use it.
if (Test-Path $vaultDir) {
    Copy-Item "$assetsDir\app.png" "$vaultDir\stutter-guard-icon.png" -Force
    Copy-Item "$assetsDir\app.ico" "$vaultDir\stutter-guard-icon.ico" -Force
    Write-Host "Copied to Obsidian Vault folder"
}
