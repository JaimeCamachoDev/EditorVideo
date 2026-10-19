# Genera la version portable (descomprimir y usar) en dist\EditorVideo-v<version>-portable-win64.zip
# Uso:  powershell -ExecutionPolicy Bypass -File src\empaquetar.ps1 -Version 2.0.0
param([string]$Version = '2.0.0')
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = Split-Path $PSScriptRoot -Parent

# FFmpeg (se descarga si falta) y compilacion del .exe
if (-not (Test-Path (Join-Path $root 'ffmpeg\ffmpeg.exe'))) {
  & (Join-Path $root 'instalar.ps1') -SinAccesos
} else {
  & (Join-Path $PSScriptRoot 'build.ps1')
}

$dist = Join-Path $root 'dist'
$stage = Join-Path $dist 'EditorVideo'
if (Test-Path $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $stage 'ffmpeg') | Out-Null

Copy-Item (Join-Path $root 'EditorVideo.exe'), (Join-Path $root 'app.html'), (Join-Path $root 'LICENSE') -Destination $stage
Copy-Item (Join-Path $root 'portable\*') -Destination $stage
Copy-Item (Join-Path $root 'ffmpeg\ffmpeg.exe'), (Join-Path $root 'ffmpeg\ffprobe.exe'), (Join-Path $root 'ffmpeg\LICENSE.txt') -Destination (Join-Path $stage 'ffmpeg')

$zip = Join-Path $dist "EditorVideo-v$Version-portable-win64.zip"
if (Test-Path $zip) { Remove-Item -LiteralPath $zip -Force }
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
# Rutas con "/" (estandar ZIP): CreateFromDirectory de .NET Framework usa "\"
$archive = [IO.Compression.ZipFile]::Open($zip, 'Create')
try {
  foreach ($f in Get-ChildItem $stage -Recurse -File) {
    $entry = 'EditorVideo/' + $f.FullName.Substring($stage.Length + 1).Replace('\', '/')
    [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $f.FullName, $entry, 'Optimal')
  }
} finally { $archive.Dispose() }
Remove-Item -LiteralPath $stage -Recurse -Force
Write-Host ("Paquete: {0} ({1:N1} MB)" -f $zip, ((Get-Item $zip).Length / 1MB))
