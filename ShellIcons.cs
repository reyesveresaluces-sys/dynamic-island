using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DynamicIsland
{
    /// <summary>Íconos de Windows para archivos y apps (con caché por extensión).</summary>
    internal static class ShellIcons
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        private const uint SHGFI_ICON = 0x100;
        private const uint SHGFI_LARGEICON = 0x0;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

        // Estos tipos tienen ícono propio por archivo; el resto se comparte por extensión
        private static bool PerFile(string ext) => ext is ".exe" or ".lnk" or ".ico" or ".url" or ".appref-ms";

        /// <summary>Debe llamarse en el hilo de la interfaz.</summary>
        public static ImageSource? Get(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            string key = PerFile(ext) ? path : (string.IsNullOrEmpty(ext) ? "<none>" : ext);
            return Cache.GetOrAdd(key, _ => Load(path, PerFile(ext)));
        }

        private static ImageSource? Load(string path, bool perFile)
        {
            var info = new SHFILEINFO();
            uint flags = SHGFI_ICON | SHGFI_LARGEICON;
            if (!perFile) flags |= SHGFI_USEFILEATTRIBUTES;
            IntPtr res = SHGetFileInfo(path, FILE_ATTRIBUTE_NORMAL, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
            if (res == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
            try
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            catch { return null; }
            finally { DestroyIcon(info.hIcon); }
        }
    }
}
