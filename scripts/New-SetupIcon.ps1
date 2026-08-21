$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path -Parent $PSScriptRoot
$sourceSvg = Join-Path $repoRoot 'src\src\BackupMonitor.Api\wwwroot\assets\brand\backupmonitor-mark.svg'
$outputDirectory = Join-Path $repoRoot 'src\assets'
$outputIcon = Join-Path $outputDirectory 'backupmonitor.ico'
$temporaryDirectory = Join-Path $repoRoot ('artifacts\setup-icon-' + [Guid]::NewGuid().ToString('N'))

if (-not (Test-Path -LiteralPath $sourceSvg -PathType Leaf)) {
    throw "BackupMonitor brand mark was not found: $sourceSvg"
}

$resolvedRepo = [IO.Path]::GetFullPath($repoRoot).TrimEnd('\') + '\'
$resolvedTemporary = [IO.Path]::GetFullPath($temporaryDirectory).TrimEnd('\') + '\'
if (-not $resolvedTemporary.StartsWith($resolvedRepo, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to use a temporary directory outside the workspace: $temporaryDirectory"
}

function New-RoundedRectanglePath {
    param(
        [Parameter(Mandatory = $true)][Drawing.RectangleF] $Rectangle,
        [Parameter(Mandatory = $true)][float] $Radius
    )

    $path = [Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = $Radius * 2
    $path.AddArc($Rectangle.X, $Rectangle.Y, $diameter, $diameter, 180, 90)
    $path.AddArc($Rectangle.Right - $diameter, $Rectangle.Y, $diameter, $diameter, 270, 90)
    $path.AddArc($Rectangle.Right - $diameter, $Rectangle.Bottom - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($Rectangle.X, $Rectangle.Bottom - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-BrandMarkPng {
    param(
        [Parameter(Mandatory = $true)][int] $Size,
        [Parameter(Mandatory = $true)][string] $Path
    )

    $bitmap = [Drawing.Bitmap]::new($Size, $Size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear([Drawing.Color]::Transparent)
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $scale = $Size / 128.0
        $graphics.ScaleTransform($scale, $scale)

        $outer = [Drawing.Drawing2D.GraphicsPath]::new()
        $outer.StartFigure()
        $outer.AddLine(64, 7, 111, 27)
        $outer.AddLine(111, 27, 111, 59)
        $outer.AddBezier(111, 59, 111, 90, 93, 110, 64, 121)
        $outer.AddBezier(64, 121, 35, 110, 17, 90, 17, 59)
        $outer.AddLine(17, 59, 17, 27)
        $outer.CloseFigure()
        $outerBrush = [Drawing.Drawing2D.LinearGradientBrush]::new(
            [Drawing.RectangleF]::new(17, 7, 94, 114),
            [Drawing.ColorTranslator]::FromHtml('#143b68'),
            [Drawing.ColorTranslator]::FromHtml('#09233f'),
            52.0)
        $graphics.FillPath($outerBrush, $outer)

        $inner = [Drawing.Drawing2D.GraphicsPath]::new()
        $inner.StartFigure()
        $inner.AddLine(64, 18, 99, 33)
        $inner.AddLine(99, 33, 99, 59)
        $inner.AddBezier(99, 59, 99, 83, 86, 99, 64, 109)
        $inner.AddBezier(64, 109, 42, 99, 29, 83, 29, 59)
        $inner.AddLine(29, 59, 29, 33)
        $inner.CloseFigure()
        $innerBrush = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#f5fbff'))
        $graphics.FillPath($innerBrush, $inner)

        $layerColors = @('#a8ecf7', '#48a9e8', '#2468c4')
        foreach ($index in 0..2) {
            $layerPath = New-RoundedRectanglePath ([Drawing.RectangleF]::new(36, (38 + 13 * $index), 50, 14)) 7
            $layerBrush = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml($layerColors[$index]))
            $graphics.FillPath($layerBrush, $layerPath)
            $layerBrush.Dispose()
            $layerPath.Dispose()
        }

        $healthBrush = [Drawing.SolidBrush]::new([Drawing.Color]::White)
        $healthPen = [Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml('#16a05a'), 6)
        $graphics.FillEllipse($healthBrush, 67, 65, 42, 42)
        $graphics.DrawEllipse($healthPen, 67, 65, 42, 42)

        $pulse = [Drawing.Drawing2D.GraphicsPath]::new()
        $pulse.AddLines([Drawing.PointF[]]@(
            [Drawing.PointF]::new(76, 86),
            [Drawing.PointF]::new(83, 93),
            [Drawing.PointF]::new(90, 77),
            [Drawing.PointF]::new(95, 88),
            [Drawing.PointF]::new(103, 88)))
        $pulsePen = [Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml('#16a05a'), 4)
        $pulsePen.StartCap = [Drawing.Drawing2D.LineCap]::Round
        $pulsePen.EndCap = [Drawing.Drawing2D.LineCap]::Round
        $pulsePen.LineJoin = [Drawing.Drawing2D.LineJoin]::Round
        $graphics.DrawPath($pulsePen, $pulse)

        $sparkPen = [Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml('#f3b53f'), 3)
        $sparkPen.StartCap = [Drawing.Drawing2D.LineCap]::Round
        $sparkPen.EndCap = [Drawing.Drawing2D.LineCap]::Round
        $graphics.DrawLine($sparkPen, 101, 42, 105, 46)
        $graphics.DrawLine($sparkPen, 104, 38, 109, 37)

        $bitmap.Save($Path, [Drawing.Imaging.ImageFormat]::Png)

        $sparkPen.Dispose()
        $pulsePen.Dispose()
        $pulse.Dispose()
        $healthPen.Dispose()
        $healthBrush.Dispose()
        $innerBrush.Dispose()
        $inner.Dispose()
        $outerBrush.Dispose()
        $outer.Dispose()
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

New-Item -ItemType Directory -Path $outputDirectory, $temporaryDirectory -Force | Out-Null
try {
    $images = foreach ($size in @(16, 24, 32, 48, 64, 128, 256)) {
        $pngPath = Join-Path $temporaryDirectory ("backupmonitor-{0}.png" -f $size)
        New-BrandMarkPng -Size $size -Path $pngPath
        [pscustomobject]@{
            Size = $size
            Bytes = [IO.File]::ReadAllBytes($pngPath)
        }
    }

    $stream = [IO.File]::Open($outputIcon, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $writer = [IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([UInt16]0)
        $writer.Write([UInt16]1)
        $writer.Write([UInt16]$images.Count)

        $offset = 6 + (16 * $images.Count)
        foreach ($image in $images) {
            $dimension = if ($image.Size -eq 256) { 0 } else { $image.Size }
            $writer.Write([Byte]$dimension)
            $writer.Write([Byte]$dimension)
            $writer.Write([Byte]0)
            $writer.Write([Byte]0)
            $writer.Write([UInt16]1)
            $writer.Write([UInt16]32)
            $writer.Write([UInt32]$image.Bytes.Length)
            $writer.Write([UInt32]$offset)
            $offset += $image.Bytes.Length
        }
        foreach ($image in $images) {
            $writer.Write($image.Bytes)
        }
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryDirectory) {
        Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force
    }
}

Get-Item -LiteralPath $outputIcon | Select-Object FullName, Length
