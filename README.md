# Dynamic Island para Windows

Una "Dynamic Island" estilo iPhone en la parte superior de la pantalla que **refleja todas las notificaciones de Windows**: WhatsApp, Outlook, Teams, Chrome, Gmail, correos del sistema, etc.

- **Compacta:** píldora negra con la hora y un contador de no leídas.
- **Notificación entrante:** la isla se expande con animación y muestra ícono, app, título y mensaje.
- **Al pasar el mouse:** se abre el historial de las últimas notificaciones.
- **Música:** con Spotify, YouTube en el navegador, Apple Music, VLC o cualquier reproductor que se reporte a Windows:
  - la píldora muestra la portada y un ecualizador animado;
  - al pasar el mouse aparece una tarjeta con canción, artista y botones ⏮ ⏯ ⏭;
  - al cambiar de canción, la isla lo anuncia.
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
