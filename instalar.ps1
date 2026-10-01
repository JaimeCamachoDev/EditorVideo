# Instala el Editor de Vídeo en este equipo: descarga FFmpeg, compila la app y crea los accesos directos.
# Uso (desde la carpeta del repositorio):
#   powershell -ExecutionPolicy Bypass -File instalar.ps1
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$app = $PSScriptRoot

# FFmpeg 8.0.1: las versiones 8.1+ exigen el driver NVIDIA 610+ para codificar con la GPU (NVENC)
$ver = '8.0.1'
$dest = Join-Path $app 'ffmpeg'
if (-not (Test-Path (Join-Path $dest 'ffmpeg.exe'))) {
  Write-Host "Descargando FFmpeg $ver (unos 100 MB)..."
  $zip = Join-Path $env:TEMP "ffmpeg-$ver.zip"
  $tmp = Join-Path $env:TEMP "ffmpeg-$ver"
  Invoke-WebRequest "https://github.com/GyanD/codexffmpeg/releases/download/$ver/ffmpeg-$ver-essentials_build.zip" -OutFile $zip -UseBasicParsing
  Expand-Archive $zip -DestinationPath $tmp -Force
  New-Item -ItemType Directory -Force $dest | Out-Null
  Get-ChildItem $tmp -Recurse -Include ffmpeg.exe, ffprobe.exe | Copy-Item -Destination $dest -Force
  Get-ChildItem $tmp -Recurse -Filter 'LICENSE*' | Select-Object -First 1 | Copy-Item -Destination (Join-Path $dest 'LICENSE.txt') -Force
  Remove-Item -LiteralPath $zip -Force
  Remove-Item -LiteralPath $tmp -Recurse -Force
} else {
  Write-Host 'FFmpeg ya esta instalado.'
}

& (Join-Path $app 'src\build.ps1') -Shortcuts
Write-Host ''
Write-Host "Listo. Abre 'Editor de Video' desde el Escritorio o el menu Inicio."
