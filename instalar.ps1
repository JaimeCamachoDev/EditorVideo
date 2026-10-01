# Instala el Editor de Video en este equipo: descarga FFmpeg, compila la app y crea los accesos directos.
# Uso: doble clic en Instalar.bat, o desde la carpeta del repositorio:
#   powershell -ExecutionPolicy Bypass -File instalar.ps1 [-SinAccesos]
param([switch]$SinAccesos)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$app = $PSScriptRoot

try {
  # Los archivos descargados de internet (ZIP de GitHub) llegan bloqueados por Windows
  Get-ChildItem $app -Recurse -File | Unblock-File -ErrorAction SilentlyContinue

  $csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
  if (-not (Test-Path $csc)) { throw "No se encuentra .NET Framework 4 ($csc). Viene con Windows 10/11; activalo en 'Caracteristicas de Windows'." }

  # FFmpeg 8.0.1: las versiones 8.1+ exigen el driver NVIDIA 610+ para codificar con la GPU (NVENC)
  $ver = '8.0.1'
  $dest = Join-Path $app 'ffmpeg'
  if (-not (Test-Path (Join-Path $dest 'ffmpeg.exe'))) {
    Write-Host "Descargando FFmpeg $ver (unos 100 MB, puede tardar un poco)..."
    $zip = Join-Path $env:TEMP "ffmpeg-$ver.zip"
    $tmp = Join-Path $env:TEMP "ffmpeg-$ver"
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest "https://github.com/GyanD/codexffmpeg/releases/download/$ver/ffmpeg-$ver-essentials_build.zip" -OutFile $zip -UseBasicParsing
    Write-Host 'Descomprimiendo...'
    Expand-Archive $zip -DestinationPath $tmp -Force
    New-Item -ItemType Directory -Force $dest | Out-Null
    Get-ChildItem $tmp -Recurse -Include ffmpeg.exe, ffprobe.exe | Copy-Item -Destination $dest -Force
    Get-ChildItem $tmp -Recurse -Filter 'LICENSE*' | Select-Object -First 1 | Copy-Item -Destination (Join-Path $dest 'LICENSE.txt') -Force
    Remove-Item -LiteralPath $zip -Force
    Remove-Item -LiteralPath $tmp -Recurse -Force
  } else {
    Write-Host 'FFmpeg ya esta instalado.'
  }

  Write-Host 'Compilando la aplicacion...'
  if ($SinAccesos) { & (Join-Path $app 'src\build.ps1') } else { & (Join-Path $app 'src\build.ps1') -Shortcuts }

  Write-Host ''
  Write-Host 'Listo. Abre "Editor de Video" desde el Escritorio o el menu Inicio.' -ForegroundColor Green
  Write-Host "(La app se ejecuta desde esta carpeta: $app  -- no la borres ni la muevas;"
  Write-Host ' si la mueves, vuelve a ejecutar Instalar.bat para actualizar los accesos directos.)'
} catch {
  Write-Host ''
  Write-Host "Error: $($_.Exception.Message)" -ForegroundColor Red
  exit 1
}
