# Dynamic Island para Windows

Una "Dynamic Island" estilo iPhone en la parte superior de la pantalla que **refleja todas las notificaciones de Windows**: WhatsApp, Outlook, Teams, Chrome, Gmail, correos del sistema, etc.

- **Compacta:** píldora negra con la hora y un contador de no leídas.
- **Notificación entrante:** la isla se expande con animación y muestra ícono, app, título y mensaje.
- **Al pasar el mouse:** se abre el historial de las últimas notificaciones.
- **Música:** con Spotify, YouTube en el navegador, Apple Music, VLC o cualquier reproductor que se reporte a Windows:
  - la píldora muestra la portada y un ecualizador animado;
  - al pasar el mouse aparece una tarjeta con canción, artista y botones ⏮ ⏯ ⏭;
  - al cambiar de canción, la isla lo anuncia.
- **Varios monitores:** aparece una isla en cada pantalla, sincronizadas entre sí (misma notificación, misma música, descartar/limpiar en una se refleja en todas). Si conectas o desconectas un monitor, se ajusta sola. Se puede limitar a la pantalla principal desde la bandeja.
- **Buscador estilo Spotlight (Alt+Espacio):** busca apps, archivos por nombre y **texto dentro de los documentos** (como AnyTXT Searcher) en todos los discos:
  - formatos de contenido: txt, md, csv, json, xml, html, código fuente, Word (docx), Excel (xlsx), PowerPoint (pptx), OpenDocument, PDF, RTF y EPUB;
  - resultados al instante mientras escribes, con el fragmento encontrado resaltado;
  - ↵ abre, Ctrl+↵ muestra el archivo en su carpeta, Esc cierra;
  - el índice (SQLite FTS5) se guarda en `%LOCALAPPDATA%\DynamicIsland`, se actualiza solo con los cambios del disco y omite carpetas del sistema (Windows, Program Files, AppData, node_modules…);
  - la primera indexación de los discos completos puede tardar; los nombres quedan listos primero y el contenido se lee en segundo plano (lo más reciente primero).
  - Si Alt+Espacio está ocupado (p. ej. PowerToys Run), usa Win+Shift+Espacio.
  - **OCR:** lee el texto dentro de imágenes (JPG, PNG, capturas, fotos de documentos…) y de las páginas escaneadas de los PDF, con el motor de reconocimiento integrado de Windows (sin internet). Va al final de la cola porque es más lento; se puede apagar desde la bandeja.
  - **Vista previa:** al moverte por los resultados se muestra el archivo a la derecha — imágenes, la página del PDF donde está la coincidencia, el texto de Word/Excel/código con las palabras resaltadas, o la miniatura de Windows para lo demás (videos, etc.). Clic en la vista previa para abrir.
- Clic en una notificación → abre la app. Clic derecho → descartar.

> Nota: Windows no permite que otras apps usen el botón "Responder" de una notificación, así que para contestar se abre la app al hacer clic.
- Ícono en la bandeja del sistema: pausar banners (No molestar), duración del banner, iniciar con Windows, probar la isla, salir.

## Descargar

1. Ve a la pestaña **Actions** del repositorio → la última ejecución verde → descarga el artefacto **DynamicIsland-win-x64**.
2. O, si hay un tag publicado (`v1.0.0`), descárgalo desde **Releases**.

Es un solo `.exe`; no necesitas instalar .NET.

## Primer uso

1. Ejecuta `DynamicIsland.exe`. Aparecerá la isla arriba al centro.
2. Windows pedirá permiso para **leer tus notificaciones**: acéptalo.
   Si no aparece, ve a *Configuración → Privacidad y seguridad → Notificaciones* y activa el acceso.
3. Si la API de Windows no está disponible, la app entra en **modo compatibilidad** (lee la base de notificaciones de Windows directamente). El menú de la bandeja indica qué modo está activo.
4. Activa **Iniciar con Windows** desde el ícono de la bandeja.

**Tip:** para que no salgan duplicadas, puedes apagar los banners nativos de Windows (*Configuración → Sistema → Notificaciones →* cada app *→ Mostrar banners de notificación*). Las notificaciones siguen llegando al centro de notificaciones y la isla las seguirá mostrando.

## Cómo funciona

| Archivo | Qué hace |
|---|---|
| `NotificationService.cs` | Sondea cada segundo las notificaciones con `UserNotificationListener`; respaldo leyendo `wpndatabase.db` |
| `SearchIndex.cs` | Índice de texto completo de los discos (rastreo, vigilancia de cambios, búsqueda) |
| `TextExtractor.cs` | Lee el texto de PDF, Office, OpenDocument, RTF, HTML y texto plano |
| `SearchWindow.xaml(.cs)` | Barra tipo Spotlight, vista previa y atajo global |
| `OcrService.cs` | OCR de imágenes y PDF escaneados (Windows.Media.Ocr) |
| `PreviewService.cs` | Prepara la vista previa: imagen, página de PDF, texto o miniatura |
| `AppSettings.cs` | Ajustes guardados (OCR activado) |
| `NotificationActivation.cs` | Al hacer clic, abre la notificación como Windows: pasa su orden original al activador de la app (p. ej. abre el chat exacto en WhatsApp Web). Registro en `%LOCALAPPDATA%\DynamicIsland\clicks.log` |
| `MediaService.cs` | Música en curso y controles (GlobalSystemMediaTransportControls) |
| `IslandWindow.xaml(.cs)` | La isla: estados compacta / banner / historial y animaciones |
| `App.xaml.cs` | Arranque, instancia única y menú de la bandeja |
| `StartupManager.cs` | Inicio con Windows (registro `HKCU\...\Run`) |

## Compilar localmente

Requiere .NET 8 SDK en Windows:

```powershell
dotnet publish DynamicIsland.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

## Publicar una versión

```powershell
git tag v1.0.0
git push origin v1.0.0
```

GitHub Actions compila y crea el Release con el `.exe`.

## Licencia

MIT
