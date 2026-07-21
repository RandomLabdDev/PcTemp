param(
    [Parameter(Mandatory = $true)]
    [string]$PngOutput
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase

$visual = New-Object System.Windows.Media.DrawingVisual
$drawing = $visual.RenderOpen()
$background = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(24, 24, 27))
$cyan = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(0, 191, 255))
$orange = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(255, 169, 32))

$drawing.DrawRoundedRectangle($background, $null, (New-Object Windows.Rect(0, 0, 256, 256)), 52, 52)
$drawing.PushTransform((New-Object System.Windows.Media.TranslateTransform(32, 32)))
$drawing.PushTransform((New-Object System.Windows.Media.ScaleTransform(12, 12)))
$outer = [System.Windows.Media.Geometry]::Parse('M5.5,2.5 A2.5,2.5 0 0 1 10.5,2.5 V10.05 A3.5,3.5 0 1 1 5.5,10.05 Z M8,1 A1.5,1.5 0 0 0 6.5,2.5 V10.487 L6.333,10.637 A2.5,2.5 0 1 0 9.666,10.637 L9.5,10.487 V2.5 A1.5,1.5 0 0 0 8,1 Z')
$inner = [System.Windows.Media.Geometry]::Parse('M9.5,12.5 A1.5,1.5 0 1 1 7.5,11.085 V6.5 A0.5,0.5 0 0 1 8.5,6.5 V11.085 A1.5,1.5 0 0 1 9.5,12.5')
$drawing.DrawGeometry($cyan, $null, $outer)
$drawing.DrawGeometry($orange, $null, $inner)
$drawing.Pop()
$drawing.Pop()
$drawing.Close()

$bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap(256, 256, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32))
$bitmap.Render($visual)
$encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
$encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
$stream = [System.IO.File]::Open($PngOutput, [System.IO.FileMode]::Create)
try { $encoder.Save($stream) } finally { $stream.Dispose() }
