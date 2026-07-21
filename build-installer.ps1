param(
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$appOutput = Join-Path $root 'PcTemp-Release'
$installerDir = Join-Path $root 'installer'
$payload = Join-Path $installerDir 'payload.zip'
$dist = Join-Path $root 'dist'
$installerSource = Get-Content -LiteralPath (Join-Path $installerDir 'Installer.cs') -Raw
$versionMatch = [regex]::Match($installerSource, 'internal const string Version\s*=\s*"([^"]+)"')
if (-not $versionMatch.Success) { throw 'No se pudo determinar la versión del instalador.' }
$version = $versionMatch.Groups[1].Value
$setup = Join-Path $dist ("PcTemp-Setup-{0}.exe" -f $version)
$stableSetup = Join-Path $dist 'PcTemp-Setup.exe'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'

if ($Clean) {
    if (Test-Path -LiteralPath $appOutput) { Remove-Item -LiteralPath $appOutput -Recurse -Force }
    if (Test-Path -LiteralPath $dist) { Remove-Item -LiteralPath $dist -Recurse -Force }
}

& (Join-Path $root 'build.ps1') -Clean -OutputDirectory 'PcTemp-Release'
if ($LASTEXITCODE -ne 0) { throw 'No se pudo compilar PcTemp.' }

if (Test-Path -LiteralPath $payload) { Remove-Item -LiteralPath $payload -Force }
Compress-Archive -Path (Join-Path $appOutput '*') -DestinationPath $payload -CompressionLevel Optimal
New-Item -ItemType Directory -Path $dist -Force | Out-Null
$resourceArgument = "/resource:$payload,PcTemp.Payload.zip"

& $compiler /nologo /target:winexe /platform:x64 /optimize+ `
    /out:"$setup" `
    /win32icon:"$(Join-Path $root 'assets\PcTemp.ico')" `
    /win32manifest:"$(Join-Path $installerDir 'installer.manifest')" `
    $resourceArgument `
    /reference:Microsoft.CSharp.dll `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.IO.Compression.dll `
    /reference:System.IO.Compression.FileSystem.dll `
    /reference:System.Windows.Forms.dll `
    "$(Join-Path $installerDir 'Installer.cs')"

if ($LASTEXITCODE -ne 0) { throw "La compilación del instalador falló con código $LASTEXITCODE." }
Copy-Item -LiteralPath $setup -Destination $stableSetup -Force
Remove-Item -LiteralPath $payload -Force
Write-Host "Instalador completado: $setup"
