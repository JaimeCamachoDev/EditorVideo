# Editor de Vídeo

Editor de vídeo sencillo para Windows, pensado para las grabaciones de la **Xbox Game Bar** (`Win`+`Alt`+`R`): recortar, comprimir, quitar o cambiar el audio y exportar a MP4, WebM, GIF o MP3.

- Usa **FFmpeg nativo** y la **GPU NVIDIA (NVENC)** si la hay: un tramo de 10 s de una grabación de 2560×1352 se exporta en ~2 s.
- Lista tus grabaciones recientes (`Vídeos\Captures`) y abre automáticamente cada grabación nueva al terminar de grabar.
- Guarda en `Vídeos\Editados`, y con **Copiar archivo** lo pegas directamente en Teams, Slack, WhatsApp o el correo.
- Sin instalar nada más: se compila con el `csc.exe` que trae Windows (.NET Framework 4.x) y la interfaz se abre en una ventana propia de Edge/Chrome.

## Instalación

```powershell
git clone https://github.com/JaimeCamachoDev/EditorVideo.git
cd EditorVideo
powershell -ExecutionPolicy Bypass -File instalar.ps1
```

El instalador descarga FFmpeg 8.0.1 ([gyan.dev](https://www.gyan.dev/ffmpeg/builds/)), compila `EditorVideo.exe` y crea accesos directos en el Escritorio, el menú Inicio y **Enviar a**.

> Se usa FFmpeg 8.0.1 porque las versiones 8.1 y posteriores exigen el driver NVIDIA 610 o superior para NVENC.

## Uso

1. Deja el editor abierto mientras grabas con la Game Bar.
2. Al parar la grabación aparece en **Grabaciones recientes** y se abre sola (se puede desactivar en el panel Exportar).
3. Recorta con los tiradores azules o con `I` / `O` y pulsa **Exportar** (`Ctrl`+`E`).

También puedes hacer clic derecho en cualquier vídeo → **Enviar a → Editor de Vídeo**.

| Atajo | Acción |
|---|---|
| `Espacio` | Reproducir / pausa |
| `I` / `O` | Marcar inicio / fin |
| `←` `→` | Fotograma a fotograma |
| `Shift`+`←` `→` | Saltar 1 segundo |
| `Ctrl`+`E` | Exportar |
| `Ctrl`+`O` | Abrir vídeo |

## Cómo está hecho

| Archivo | Qué es |
|---|---|
| `src/EditorVideo.cs` | Servidor local (solo `localhost`, protegido con token) que ejecuta FFmpeg, sirve el vídeo, lista grabaciones y abre los diálogos de Windows. Se cierra solo al cerrar la ventana. |
| `app.html` | La interfaz. |
| `src/build.ps1` | Compila el `.exe` (`-Shortcuts` crea también los accesos directos). |
| `instalar.ps1` | Descarga FFmpeg y compila. |

Tras cambiar `app.html` basta con reabrir la app; tras cambiar `EditorVideo.cs`, vuelve a ejecutar `src\build.ps1`.

Registro de diagnóstico: `%LOCALAPPDATA%\EditorVideo\registro.txt`.

## Desinstalar

Borra la carpeta del repositorio, `%LOCALAPPDATA%\EditorVideo` y los accesos directos «Editor de Vídeo» del Escritorio, del menú Inicio y de la carpeta Enviar a (`Win`+`R` → `shell:sendto`).
