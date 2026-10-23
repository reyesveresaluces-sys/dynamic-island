using System;
using System.Runtime.InteropServices;

namespace DynamicIsland
{
    /// <summary>Volumen general de Windows (altavoz predeterminado) con Core Audio.</summary>
    public sealed class VolumeService
    {
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorCom { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
                [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        }

        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioEndpointVolume
        {
            [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int GetChannelCount(out uint count);
            [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid context);
            [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
            [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
            [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
            [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
            [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
            [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
            [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }

        private IMMDeviceEnumerator? _enumerator;
        private Guid _context = Guid.NewGuid();

        /// <summary>El altavoz predeterminado puede cambiar (audífonos, Bluetooth…): se pide cada vez.</summary>
        private IAudioEndpointVolume? Endpoint()
        {
            try
            {
                _enumerator ??= (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                if (_enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out var device) != 0 || device == null)
                    return null;
                var iid = typeof(IAudioEndpointVolume).GUID;
                if (device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out object o) != 0) return null;
                return o as IAudioEndpointVolume;
            }
            catch { return null; }
        }

        /// <summary>Nivel de 0 a 1 y si está silenciado; null si no hay salida de audio.</summary>
        public (double level, bool muted)? Read()
        {
            var ep = Endpoint();
            if (ep == null) return null;
            try
            {
                ep.GetMasterVolumeLevelScalar(out float level);
                ep.GetMute(out bool muted);
                return (level, muted);
            }
            catch { return null; }
        }

        public void SetLevel(double level)
        {
            var ep = Endpoint();
            if (ep == null) return;
            try
            {
                ep.SetMasterVolumeLevelScalar((float)Math.Clamp(level, 0, 1), ref _context);
                if (level > 0) ep.SetMute(false, ref _context);
            }
            catch { }
        }

        public void ToggleMute()
        {
            var ep = Endpoint();
            if (ep == null) return;
            try
            {
                ep.GetMute(out bool muted);
                ep.SetMute(!muted, ref _context);
            }
            catch { }
        }
    }
}
