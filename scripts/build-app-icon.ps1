param(
    [string]$OutputDirectory = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path -Parent $PSScriptRoot
$assetRoot = Join-Path $repoRoot 'app\frontend-winui\src\Comic.WinUI\Assets'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = $assetRoot }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

# Render the committed outline directly at each size; no installed font is needed.
[xml]$svg = Get-Content -LiteralPath (Join-Path $assetRoot 'AppIcon.svg') -Raw -Encoding utf8
$namespaces = [Xml.XmlNamespaceManager]::new($svg.NameTable)
$namespaces.AddNamespace('svg', 'http://www.w3.org/2000/svg')
$glyphNode = $svg.SelectSingleNode('//svg:path[@id="glyph"]', $namespaces)
$stops = $svg.SelectNodes('//svg:linearGradient[@id="background"]/svg:stop', $namespaces)
$rectangle = $svg.SelectSingleNode('/svg:svg/svg:rect', $namespaces)
if ($null -eq $glyphNode -or $stops.Count -ne 2 -or $null -eq $rectangle) {
    throw 'Expected the app icon outline, background rectangle and two gradient stops.'
}
$culture = [Globalization.CultureInfo]::InvariantCulture
$tokens = @([regex]::Matches($glyphNode.GetAttribute('d'), '[MLCZ]|[-+]?\d+(?:\.\d+)?') | ForEach-Object { $_.Value })
$glyph = [Drawing.Drawing2D.GraphicsPath]::new([Drawing.Drawing2D.FillMode]::Alternate)
$background = [Drawing.Drawing2D.GraphicsPath]::new()
try {
    $current = [Drawing.PointF]::new(0, 0)
    $start = $current
    $index = 0
    while ($index -lt $tokens.Count) {
        $command = $tokens[$index++]
        switch ($command) {
            'M' {
                $current = [Drawing.PointF]::new([single]::Parse($tokens[$index++], $culture), [single]::Parse($tokens[$index++], $culture))
                $start = $current
                $glyph.StartFigure()
            }
            'L' {
                $point = [Drawing.PointF]::new([single]::Parse($tokens[$index++], $culture), [single]::Parse($tokens[$index++], $culture))
                $glyph.AddLine($current, $point)
                $current = $point
            }
            'C' {
                $control1 = [Drawing.PointF]::new([single]::Parse($tokens[$index++], $culture), [single]::Parse($tokens[$index++], $culture))
                $control2 = [Drawing.PointF]::new([single]::Parse($tokens[$index++], $culture), [single]::Parse($tokens[$index++], $culture))
                $point = [Drawing.PointF]::new([single]::Parse($tokens[$index++], $culture), [single]::Parse($tokens[$index++], $culture))
                $glyph.AddBezier($current, $control1, $control2, $point)
                $current = $point
            }
            'Z' { $glyph.CloseFigure(); $current = $start }
            default { throw "Unsupported icon outline command: $command" }
        }
    }

    $radius = [single]::Parse($rectangle.GetAttribute('rx'), $culture)
    $diameter = 2 * $radius
    $background.AddArc(0, 0, $diameter, $diameter, 180, 90)
    $background.AddArc(1024 - $diameter, 0, $diameter, $diameter, 270, 90)
    $background.AddArc(1024 - $diameter, 1024 - $diameter, $diameter, $diameter, 0, 90)
    $background.AddArc(0, 1024 - $diameter, $diameter, $diameter, 90, 90)
    $background.CloseFigure()
    $topColor = [Drawing.ColorTranslator]::FromHtml($stops[0].GetAttribute('stop-color'))
    $bottomColor = [Drawing.ColorTranslator]::FromHtml($stops[1].GetAttribute('stop-color'))

    function Render-Icon([int]$Size) {
        $bitmap = [Drawing.Bitmap]::new($Size, $Size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $gradient = [Drawing.Drawing2D.LinearGradientBrush]::new(
            [Drawing.RectangleF]::new(0, 0, 1024, 1024), $topColor, $bottomColor, [single]90)
        $shadowBrush = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(64, 0, 0, 0))
        try {
            $graphics.Clear([Drawing.Color]::Transparent)
            $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
            $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.ScaleTransform([single]($Size / 1024.0), [single]($Size / 1024.0))
            $graphics.FillPath($gradient, $background)
            $saved = $graphics.Save()
            try {
                $graphics.TranslateTransform(0, 4)
                $graphics.FillPath($shadowBrush, $glyph)
            } finally { $graphics.Restore($saved) }
            $graphics.FillPath([Drawing.Brushes]::White, $glyph)
            $buffer = [IO.MemoryStream]::new()
            try {
                $bitmap.Save($buffer, [Drawing.Imaging.ImageFormat]::Png)
                return ,$buffer.ToArray()
            } finally { $buffer.Dispose() }
        } finally {
            $shadowBrush.Dispose()
            $gradient.Dispose()
            $graphics.Dispose()
            $bitmap.Dispose()
        }
    }

    [IO.File]::WriteAllBytes((Join-Path $OutputDirectory 'AppIcon.png'), (Render-Icon 1024))
    $sizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
    $frames = @($sizes | ForEach-Object { [pscustomobject]@{Size=$_;Data=(Render-Icon $_)} })
    $stream = [IO.File]::Open((Join-Path $OutputDirectory 'AppIcon.ico'), [IO.FileMode]::Create)
    $writer = [IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([UInt16]0)
        $writer.Write([UInt16]1)
        $writer.Write([UInt16]$frames.Count)
        $offset = 6 + 16 * $frames.Count
        foreach ($frame in $frames) {
            $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
            $writer.Write([byte]$dimension)
            $writer.Write([byte]$dimension)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([UInt16]1)
            $writer.Write([UInt16]32)
            $writer.Write([UInt32]$frame.Data.Length)
            $writer.Write([UInt32]$offset)
            $offset += $frame.Data.Length
        }
        foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Data) }
    } finally { $writer.Dispose(); $stream.Dispose() }
    Write-Output "Generated 1024px PNG and ICO sizes $($sizes -join ', ') in $OutputDirectory"
} finally { $background.Dispose(); $glyph.Dispose() }
