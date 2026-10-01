# Compila EditorVideo.exe con el compilador de C# que trae Windows (.NET Framework 4.x).
# Uso:  powershell -ExecutionPolicy Bypass -File build.ps1 [-Shortcuts]
param([switch]$Shortcuts)
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$app = Split-Path $src -Parent
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

# Icono (claqueta) generado con System.Drawing
$ico = Join-Path $src 'app.ico'
if (-not (Test-Path $ico)) {
  Add-Type -AssemblyName System.Drawing
  $bmp = New-Object System.Drawing.Bitmap 64, 64
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'
  $g.Clear([System.Drawing.Color]::Transparent)
  $blue = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(79, 140, 255))
  $dark = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(22, 25, 32))
  $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
  $g.FillRectangle($blue, 4, 14, 56, 44)
  $g.FillRectangle($dark, 4, 6, 56, 10)
  function P([int]$a, [int]$b) { New-Object System.Drawing.Point -ArgumentList $a, $b }
  for ($x = 8; $x -lt 60; $x += 14) { $g.FillPolygon($white, [System.Drawing.Point[]]@((P $x 6), (P ($x + 7) 6), (P ($x + 3) 16), (P ($x - 4) 16))) }
  $g.FillPolygon($white, [System.Drawing.Point[]]@((P 26 25), (P 26 49), (P 44 37)))
  $g.Dispose()
  $icon = [System.Drawing.Icon]::FromHandle($bmp.GetHicon())
  $fs = [System.IO.File]::Create($ico); $icon.Save($fs); $fs.Close()
}

& $csc /nologo /codepage:65001 /optimize+ /target:winexe /platform:anycpu "/out:$app\EditorVideo.exe" "/win32icon:$ico" `
  /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll `
  (Join-Path $src 'EditorVideo.cs')
if ($LASTEXITCODE -ne 0) { throw "Error de compilación" }
Write-Host "Compilado: $app\EditorVideo.exe"

if ($Shortcuts) {
  $sh = New-Object -ComObject WScript.Shell
  $targets = @(
    (Join-Path ([Environment]::GetFolderPath('Desktop')) "Editor de V$([char]0xED)deo.lnk"),
    (Join-Path ([Environment]::GetFolderPath('Programs')) "Editor de V$([char]0xED)deo.lnk"),
    (Join-Path ([Environment]::GetFolderPath('SendTo')) "Editor de V$([char]0xED)deo.lnk")
  )
  foreach ($t in $targets) {
    $l = $sh.CreateShortcut($t)
    $l.TargetPath = "$app\EditorVideo.exe"
    $l.WorkingDirectory = $app
    $l.IconLocation = "$app\EditorVideo.exe,0"
    $l.Description = "Recortar, comprimir y editar el audio de v$([char]0xED)deos"
    $l.Save()
    Write-Host "Acceso directo: $t"
  }
}
