@echo off
rem Opcional: crea accesos directos a EditorVideo.exe en el Escritorio, el menu Inicio y "Enviar a".
rem Si mueves la carpeta, vuelve a ejecutarlo.
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$app = (Get-Location).Path; $sh = New-Object -ComObject WScript.Shell; $n = 'Editor de V' + [char]0xED + 'deo.lnk';" ^
  "foreach ($d in [Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs'), [Environment]::GetFolderPath('SendTo')) {" ^
  "  $l = $sh.CreateShortcut((Join-Path $d $n)); $l.TargetPath = Join-Path $app 'EditorVideo.exe'; $l.WorkingDirectory = $app;" ^
  "  $l.IconLocation = (Join-Path $app 'EditorVideo.exe') + ',0'; $l.Save(); Write-Host ('Creado: ' + (Join-Path $d $n)) }"
echo.
echo Listo. Ya puedes abrir el editor desde el Escritorio, el menu Inicio o con clic derecho en un video ^> Enviar a.
pause
