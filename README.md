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

### Espacios (estilo "edge dock")

Al pasar el mouse, la isla se expande y muestra **espacios**. Se cambian con la **rueda del mouse** (o deslizando de lado en el touchpad), con las pestañas, o con ←/→ y 1–6 si la abriste con el teclado:

| Espacio | Qué hace |
|---|---|
| 🔔 Notificaciones | Historial de las últimas notificaciones |
| 🎵 Música | Portada, **barra de avance** para saltar en la canción, anterior/pausa/siguiente y **volumen general** (también con la rueda sobre la barra) |
| 📋 Portapapeles | Historial de lo que copias — texto, enlaces, imágenes y archivos — con **búsqueda**, filtros y elementos **fijados** que se conservan. Clic en un elemento = copiarlo de nuevo. Respeta a los gestores de contraseñas que piden no registrar su contenido |
| 🚀 Apps | Lanzador de apps, archivos y carpetas **sin límite**. Arrastra cualquier cosa sobre la isla para agregarla; clic derecho para ordenar o quitar |
| ⏱️ Temporizador | Temporizador con atajos (1, 5, 10, 25, 60 min) y cronómetro con vueltas. Mientras corren, la isla compacta muestra la cuenta |
| 🪟 Zonas | Disposición de ventanas estilo **FancyZones** para esa pantalla, con vista previa para mandar la ventana activa a una zona con un clic |

### Zonas de ventanas (como FancyZones de PowerToys)

- **Arrastra una ventana con Shift** y aparecen las zonas del monitor; suéltala sobre una y se ajusta ahí.
  - Con **Ctrl** mientras arrastras, la ventana **abarca varias zonas** (pasa el cursor por las que quieras).
  - En el espacio *Zonas* puedes cambiarlo a **Siempre** (sin Shift) o **Apagadas**; también desde la bandeja.
- **Disposiciones:** Enfoque, Columnas, Tres columnas, Filas, Cuadrícula, Prioridad, Prioridad central, Ancha + lateral, y una **cuadrícula a tu medida** (hasta 6 × 4). **Cada monitor tiene la suya** y se recuerda.
- **Separación** entre zonas ajustable (0–40 px).
- **Teclado:** `Ctrl+Alt+←/→` mueve la ventana activa de zona en zona (al llegar al borde pasa al siguiente monitor) y `Ctrl+Alt+↑` la manda a la primera zona.
- **Clic:** en el espacio *Zonas*, la vista previa del monitor manda la última ventana activa a la zona que toques.
- Funciona con ventanas normales de cualquier app (respeta el marco invisible de Windows, así que quedan al ras) y no estorba: la capa de zonas no recibe clics.

**Abrir sin el mouse:** `Ctrl+Alt+Espacio` (o `Win+Alt+Espacio` si está ocupado), o **Magic Shake**: agita el mouse de lado a lado.

**Posición:** arriba al centro, o como una hoja delgada en el **borde izquierdo** o **derecho** de cada pantalla (bandeja → *Posición de la isla*).

Las animaciones usan física de resorte.

> Nota: Windows no permite que otras apps usen el botón "Responder" de una notificación, así que para contestar se abre la app al hacer clic.
- Ícono en la bandeja del sistema: pausar banners (No molestar), duración del banner, zonas de ventanas, iniciar con Windows, probar la isla, salir.

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
| `AppSettings.cs` | Ajustes guardados (OCR, posición, zonas por monitor, lanzador…) |
| `NotificationActivation.cs` | Al hacer clic, abre la notificación como Windows: pasa su orden original al activador de la app (p. ej. abre el chat exacto en WhatsApp Web). Registro en `%LOCALAPPDATA%\DynamicIsland\clicks.log` |
| `ZoneService.cs` | Zonas de ventanas: detecta el arrastre, ajusta la ventana a la zona y atajos de teclado |
| `ZoneLayouts.cs` | Disposiciones de zonas (predefinidas y cuadrículas a medida) |
| `ZoneOverlayWindow.cs` | Capa transparente que dibuja las zonas sobre el monitor mientras arrastras |
| `ZonesSpace` | El espacio *Zonas* de la isla: elegir disposición, separación y enviar ventanas |
| `MediaService.cs` | Música en curso, controles y línea de tiempo (GlobalSystemMediaTransportControls) |
| `MediaSpace` / `ClipboardSpace` / `LauncherSpace` / `TimerSpace` / `ZonesSpace` | Los espacios de la isla expandida |
| `ClipboardService.cs` | Historial del portapapeles y elementos fijados |
| `LauncherStore.cs` | Accesos rápidos del lanzador |
| `TimerTools.cs` | Temporizador y cronómetro compartidos |
| `VolumeService.cs` | Volumen general (Core Audio) |
| `IslandTriggers.cs` | Atajo global y Magic Shake |
| `SpringEase.cs` | Curva de animación de resorte |
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
