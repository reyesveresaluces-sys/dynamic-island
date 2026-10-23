namespace DynamicIsland
{
    /// <summary>Herramientas compartidas por todas las islas (una por monitor).</summary>
    public static class IslandTools
    {
        public static ClipboardService Clipboard { get; } = new();
        public static LauncherStore Launcher { get; } = new();
        public static TimerTools Timers { get; } = new();
        public static VolumeService Volume { get; } = new();
        public static MediaService? Media { get; set; }
        public static ZoneService Zones { get; } = new();
    }

    /// <summary>Espacios de la isla expandida (se cambian con la rueda del mouse).</summary>
    public enum IslandSpace { Notifications, Media, Clipboard, Launcher, Timer, Zones }

    /// <summary>Borde de la pantalla donde se ancla la isla.</summary>
    public enum IslandDock { Top, Left, Right }
}
