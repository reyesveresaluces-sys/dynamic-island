using System;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace DynamicIsland
{
    /// <summary>
    /// Arranque con Windows, sin permisos de administrador. Para que no falle usa tres cosas:
    /// 1) la entrada clásica en HKCU\...\Run;
    /// 2) quita la marca de "desactivada" que pone el Administrador de tareas (StartupApproved);
    /// 3) un acceso directo en la carpeta Inicio, por si una directiva bloquea el registro.
    /// Además corrige sola la ruta si el .exe cambió de carpeta.
    /// </summary>
    public static class StartupManager
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        private const string ValueName = "DynamicIsland";

        /// <summary>Ruta del ejecutable en marcha.</summary>
        public static string ExePath => Environment.ProcessPath ?? Assembly.GetEntryAssembly()?.Location ?? "";

        private static string LnkPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Dynamic Island.lnk");

        /// <summary>Copia fija del .exe, para que el arranque no dependa de la carpeta de descargas.</summary>
        public static string AppCopy => Path.Combine(SearchIndex.DataDir, "app", "DynamicIsland.exe");

        public static bool IsEnabled => RegisteredPath() != null && !IsBlocked();

        public static string? RegisteredPath()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) as string;
            }
            catch { return null; }
        }

        /// <summary>Windows marca así las entradas que se apagan en el Administrador de tareas.</summary>
        public static bool IsBlocked()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(ApprovedKey);
                // 0x02 = activada, 0x03 = desactivada (los demás bytes son la fecha)
                return key?.GetValue(ValueName) is byte[] b && b.Length > 0 && (b[0] & 1) != 0;
            }
            catch { return false; }
        }

        public static void SetEnabled(bool enabled)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey);
                if (enabled) key?.SetValue(ValueName, $"\"{ExePath}\"");
                else key?.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            catch { }

            if (enabled) Unblock();
            SetShortcut(enabled, ExePath);
        }

        private static void Unblock()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
                key?.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            catch { }
        }

        /// <summary>Si la entrada existe pero apunta a otra carpeta (bajaste una versión nueva), la corrige.</summary>
        public static void Refresh()
        {
            try
            {
                string exe = ExePath;
                var registered = RegisteredPath()?.Trim('"');
                if (exe.Length == 0 || registered == null) return;
                if (string.Equals(registered, exe, StringComparison.OrdinalIgnoreCase)) return;
                // Si apunta a la copia fija y esa copia existe, se respeta
                if (string.Equals(registered, AppCopy, StringComparison.OrdinalIgnoreCase) && File.Exists(AppCopy)) return;

                using var key = Registry.CurrentUser.CreateSubKey(RunKey);
                key?.SetValue(ValueName, $"\"{exe}\"");
                SetShortcut(true, exe);
            }
            catch { }
        }

        /// <summary>Copia el .exe a %LOCALAPPDATA%\DynamicIsland\app y registra esa copia. Devuelve la ruta o null.</summary>
        public static string? InstallCopy()
        {
            try
            {
                string exe = ExePath, target = AppCopy;
                if (exe.Length == 0) return null;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (!string.Equals(exe, target, StringComparison.OrdinalIgnoreCase))
                    File.Copy(exe, target, overwrite: true);

                using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                    key?.SetValue(ValueName, $"\"{target}\"");
                Unblock();
                SetShortcut(true, target);
                return target;
            }
            catch { return null; }
        }

        /// <summary>Resumen legible para saber por qué arranca o no.</summary>
        public static string Status()
        {
            string registered = RegisteredPath() ?? "(no hay entrada)";
            string lnk = File.Exists(LnkPath) ? "sí" : "no";
            string copy = File.Exists(AppCopy) ? AppCopy : "no";
            string blocked = IsBlocked()
                ? "\n\n⚠ Windows la tiene DESACTIVADA. Abre el Administrador de tareas → Aplicaciones de inicio → Dynamic Island → Habilitar.\n(El botón \"Iniciar con Windows\" de este menú también la vuelve a habilitar.)"
                : "";

            return $"Ejecutable en marcha:\n{ExePath}\n\n"
                 + $"Entrada en el registro de inicio:\n{registered}\n\n"
                 + $"Acceso directo en la carpeta Inicio: {lnk}\n"
                 + $"Copia fija en AppData: {copy}{blocked}";
        }

        private static void SetShortcut(bool create, string target)
        {
            try
            {
                if (!create)
                {
                    if (File.Exists(LnkPath)) File.Delete(LnkPath);
                    return;
                }
                if (target.Length == 0) return;

                var type = Type.GetTypeFromProgID("WScript.Shell");
                if (type == null) return;
                object? shell = Activator.CreateInstance(type);
                if (shell == null) return;

                object? lnk = shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { LnkPath });
                if (lnk == null) return;

                var lt = lnk.GetType();
                lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { target });
                lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { Path.GetDirectoryName(target) ?? "" });
                lt.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { "Dynamic Island" });
                lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
            }
            catch { /* una directiva puede impedirlo; queda la entrada del registro */ }
        }
    }
}
