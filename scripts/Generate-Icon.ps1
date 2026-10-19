param([string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'Assets'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$null = New-Item -ItemType Directory -Path $OutputDirectory -Force
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)

# Original PowerHub artwork; the SVG and raster drawings use the same 256-unit geometry.
$svg = @'
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" width="256" height="256">
  <title>PowerHub</title>
  <defs>
    <linearGradient id="mint" x1="0" y1="0" x2="1" y2="1"><stop stop-color="#B7F5DE"/><stop offset="1" stop-color="#61D4AE"/></linearGradient>
    <linearGradient id="battery" x1="0" y1="0" x2="0" y2="1"><stop stop-color="#174D40"/><stop offset="1" stop-color="#0D332B"/></linearGradient>
  </defs>
  <rect x="8" y="8" width="240" height="240" rx="58" fill="url(#mint)"/>
  <rect x="9" y="9" width="238" height="238" rx="57" fill="none" stroke="#FFFFFF" stroke-opacity=".42" stroke-width="2"/>
  <rect x="108" y="47" width="40" height="24" rx="6" fill="#174D40"/>
  <rect x="72" y="64" width="112" height="151" rx="23" fill="url(#battery)"/>
  <path d="M149 89 111 143H132L119 184 160 126H136L150 89Z" fill="#C6FFE6"/>
</svg>
'@
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'powerhub.svg'), $svg, [Text.UTF8Encoding]::new($false))

function New-RoundedPath([single]$X, [single]$Y, [single]$Width, [single]$Height, [single]$Radius) {
    $path = [Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = $Radius * 2
    $path.AddArc($X, $Y, $diameter, $diameter, 180, 90)
    $path.AddArc($X + $Width - $diameter, $Y, $diameter, $diameter, 270, 90)
    $path.AddArc($X + $Width - $diameter, $Y + $Height - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($X, $Y + $Height - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

foreach ($size in $sizes) {
    $rasterSize = $size * 4
    $canvas = [Drawing.Bitmap]::new($rasterSize, $rasterSize, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($canvas)
    $result = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $resampler = [Drawing.Graphics]::FromImage($result)
    $tile = New-RoundedPath 8 8 240 240 58
    $edge = New-RoundedPath 9 9 238 238 57
    $terminal = New-RoundedPath 108 47 40 24 6
    $body = New-RoundedPath 72 64 112 151 23
    $mint = [Drawing.Drawing2D.LinearGradientBrush]::new([Drawing.RectangleF]::new(8, 8, 240, 240), [Drawing.ColorTranslator]::FromHtml('#B7F5DE'), [Drawing.ColorTranslator]::FromHtml('#61D4AE'), [single]45)
    $battery = [Drawing.Drawing2D.LinearGradientBrush]::new([Drawing.RectangleF]::new(72, 64, 112, 151), [Drawing.ColorTranslator]::FromHtml('#174D40'), [Drawing.ColorTranslator]::FromHtml('#0D332B'), [single]90)
    $dark = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#174D40'))
    $charge = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#C6FFE6'))
    $outline = [Drawing.Pen]::new([Drawing.Color]::FromArgb(107, 255, 255, 255), 2)
    try {
        $graphics.Clear([Drawing.Color]::Transparent)
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.ScaleTransform([single]($rasterSize / 256.0), [single]($rasterSize / 256.0))
        $graphics.FillPath($mint, $tile)
        $graphics.DrawPath($outline, $edge)
        $graphics.FillPath($dark, $terminal)
        $graphics.FillPath($battery, $body)
        $points = [Drawing.PointF[]]@(
            [Drawing.PointF]::new(149, 89), [Drawing.PointF]::new(111, 143),
            [Drawing.PointF]::new(132, 143), [Drawing.PointF]::new(119, 184),
            [Drawing.PointF]::new(160, 126), [Drawing.PointF]::new(136, 126),
            [Drawing.PointF]::new(150, 89))
        $graphics.FillPolygon($charge, $points)
        $resampler.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
        $resampler.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $resampler.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $resampler.DrawImage($canvas, [Drawing.Rectangle]::new(0, 0, $size, $size), 0, 0, $rasterSize, $rasterSize, [Drawing.GraphicsUnit]::Pixel)
        $result.Save((Join-Path $OutputDirectory "powerhub-$size.png"), [Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $outline.Dispose(); $charge.Dispose(); $dark.Dispose(); $battery.Dispose(); $mint.Dispose()
        $body.Dispose(); $terminal.Dispose(); $edge.Dispose(); $tile.Dispose()
        $resampler.Dispose(); $result.Dispose(); $graphics.Dispose(); $canvas.Dispose()
    }
}

$entries = foreach ($size in $sizes) {
    [pscustomobject]@{ Size = $size; Data = [IO.File]::ReadAllBytes((Join-Path $OutputDirectory "powerhub-$size.png")) }
}
$stream = [IO.File]::Create((Join-Path $OutputDirectory 'powerhub.ico'))
$writer = [IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$entries.Count)
    $offset = 6 + 16 * $entries.Count
    foreach ($entry in $entries) {
        $dimension = if ($entry.Size -eq 256) { [byte]0 } else { [byte]$entry.Size }
        $writer.Write($dimension); $writer.Write($dimension); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$entry.Data.Length); $writer.Write([uint32]$offset)
        $offset += $entry.Data.Length
    }
    foreach ($entry in $entries) { $writer.Write([byte[]]$entry.Data) }
}
finally { $writer.Dispose(); $stream.Dispose() }
Write-Output "Generated PowerHub SVG, 9 PNG sizes and multi-resolution ICO in $OutputDirectory"
