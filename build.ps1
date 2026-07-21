param(
    [switch]$Clean,
    [string]$OutputDirectory = 'PcTemp'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$source = Join-Path $root 'src\Program.cs'
$manifest = Join-Path $root 'src\app.manifest'
$appIcon = Join-Path $root 'assets\PcTemp.ico'
$vendor = Join-Path $root 'vendor'
$output = Join-Path $root $OutputDirectory
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'

if ($Clean -and (Test-Path -LiteralPath $output)) {
    Remove-Item -LiteralPath $output -Recurse -Force
}

if (-not (Test-Path -LiteralPath $compiler)) {
    throw 'No se encontró el compilador de .NET Framework incluido en Windows.'
}
if (-not (Test-Path -LiteralPath (Join-Path $vendor 'LibreHardwareMonitorLib.dll'))) {
    throw 'Falta vendor\LibreHardwareMonitorLib.dll. Restaura las dependencias incluidas en el repositorio.'
}

New-Item -ItemType Directory -Path $output -Force | Out-Null

& $compiler /nologo /target:winexe /platform:x64 /optimize+ `
    /out:"$output\PcTemp.exe" `
    /win32icon:"$appIcon" `
    /win32manifest:"$manifest" `
    /reference:Microsoft.CSharp.dll `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.Management.dll `
    /reference:System.Windows.Forms.dll `
    /reference:"$vendor\DiskInfoToolkit.dll" `
    /reference:"$vendor\LibreHardwareMonitorLib.dll" `
    "$source"

if ($LASTEXITCODE -ne 0) {
    throw "La compilación falló con código $LASTEXITCODE."
}

$dependencies = @(
    'LibreHardwareMonitorLib.dll',
    'BlackSharp.Core.dll',
    'DiskInfoToolkit.dll',
    'HidSharp.dll',
    'RAMSPDToolkit-NDD.dll',
    'System.Buffers.dll',
    'System.Memory.dll',
    'System.Numerics.Vectors.dll',
    'System.Runtime.CompilerServices.Unsafe.dll'
)

foreach ($dependency in $dependencies) {
    Copy-Item -LiteralPath (Join-Path $vendor $dependency) -Destination $output -Force
}

Copy-Item -LiteralPath (Join-Path $root 'src\PcTemp.exe.config') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $root 'assets\fonts\DSEG14Classic-Bold.ttf') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $root 'assets\fonts\DSEG-LICENSE.txt') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $root 'assets\PcTemp.png') -Destination $output -Force

Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $root 'THIRD_PARTY_NOTICES.txt') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $root 'assets\BOOTSTRAP-ICONS-LICENSE.txt') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $root 'drivers\PawnIO_setup.exe') -Destination $output -Force
Write-Host "Compilación completada: $output\PcTemp.exe"
