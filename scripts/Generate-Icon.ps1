param([string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'Assets'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$null = New-Item -ItemType Directory -Path $OutputDirectory -Force
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)

# Original Perivolt mark: an open P-shaped energy core with three peripheral nodes.
$svg = @'
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" width="256" height="256">
  <title>电栖 · Perivolt</title>
  <defs>
    <linearGradient id="mint" x1="0" y1="0" x2="1" y2="1"><stop stop-color="#173F49"/><stop offset="1" stop-color="#102530"/></linearGradient>
    <linearGradient id="battery" x1="0" y1="0" x2="1" y2="1"><stop stop-color="#C9FFE7"/><stop offset="1" stop-color="#4BD8BE"/></linearGradient>
  </defs>
  <rect x="8" y="8" width="240" height="240" rx="58" fill="url(#mint)"/>
  <path d="M81 195V92C81 59 104 45 136 45C174 45 197 67 197 101C197 135 173 158 138 158H110V130H138C157 130 169 119 169 101C169 83 157 73 138 73C119 73 110 80 110 96V195Z" fill="url(#battery)"/>
  <path d="M137 83 121 107H134L128 122 153 96H139L146 83Z" fill="#FFFFFF"/>
  <circle cx="45" cy="148" r="10" fill="#65DBCF"/><circle cx="210" cy="70" r="9" fill="#8ABDEB"/><circle cx="193" cy="207" r="13" fill="#F3C383"/>
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
    $body = [Drawing.Drawing2D.GraphicsPath]::new()
    $body.AddLine(81,195,81,92); $body.AddBezier(81,92,81,59,104,45,136,45)
    $body.AddBezier(136,45,174,45,197,67,197,101); $body.AddBezier(197,101,197,135,173,158,138,158)
    $body.AddLine(138,158,110,158); $body.AddLine(110,158,110,130); $body.AddLine(110,130,138,130)
    $body.AddBezier(138,130,157,130,169,119,169,101); $body.AddBezier(169,101,169,83,157,73,138,73)
    $body.AddBezier(138,73,119,73,110,80,110,96); $body.AddLine(110,96,110,195); $body.CloseFigure()
    $mint = [Drawing.Drawing2D.LinearGradientBrush]::new([Drawing.RectangleF]::new(8,8,240,240), [Drawing.ColorTranslator]::FromHtml('#173F49'), [Drawing.ColorTranslator]::FromHtml('#102530'), [single]45)
    $battery = [Drawing.Drawing2D.LinearGradientBrush]::new([Drawing.RectangleF]::new(81,45,116,150), [Drawing.ColorTranslator]::FromHtml('#C9FFE7'), [Drawing.ColorTranslator]::FromHtml('#4BD8BE'), [single]45)
    $charge = [Drawing.SolidBrush]::new([Drawing.Color]::White)
    $nodes = @('#65DBCF','#8ABDEB','#F3C383') | ForEach-Object { [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml($_)) }
    try {
        $graphics.Clear([Drawing.Color]::Transparent)
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.ScaleTransform([single]($rasterSize / 256.0), [single]($rasterSize / 256.0))
        $graphics.FillPath($mint, $tile)
        $graphics.FillPath($battery, $body)
        $points = [Drawing.PointF[]]@(
            [Drawing.PointF]::new(137,83), [Drawing.PointF]::new(121,107),
            [Drawing.PointF]::new(134,107), [Drawing.PointF]::new(128,122),
            [Drawing.PointF]::new(153,96), [Drawing.PointF]::new(139,96),
            [Drawing.PointF]::new(146,83))
        $graphics.FillPolygon($charge, $points)
        $graphics.FillEllipse($nodes[0],35,138,20,20)
        $graphics.FillEllipse($nodes[1],201,61,18,18)
        $graphics.FillEllipse($nodes[2],180,194,26,26)
        $resampler.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
        $resampler.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $resampler.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $resampler.DrawImage($canvas, [Drawing.Rectangle]::new(0, 0, $size, $size), 0, 0, $rasterSize, $rasterSize, [Drawing.GraphicsUnit]::Pixel)
        $result.Save((Join-Path $OutputDirectory "powerhub-$size.png"), [Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        foreach ($node in $nodes) { $node.Dispose() }
        $charge.Dispose(); $battery.Dispose(); $mint.Dispose()
        $body.Dispose(); $tile.Dispose()
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
Write-Output "Generated Perivolt SVG, 9 PNG sizes and multi-resolution ICO in $OutputDirectory"
