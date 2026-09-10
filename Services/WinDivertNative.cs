using System;
using System.Runtime.InteropServices;

namespace Win11HotspotManager.Services
{
    /// <summary>WinDivert 2.2 P/Invoke (imzalar windivert.h dosyasından alındı).</summary>
    internal static class WinDivertNative
    {
        public const uint LAYER_NETWORK_FORWARD = 1;
        public const uint SHUTDOWN_BOTH = 3;
        public const uint PARAM_QUEUE_LENGTH = 0;
        public const uint PARAM_QUEUE_TIME = 1;

        [DllImport("WinDivert.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        public static extern IntPtr WinDivertOpen(string filter, uint layer, short priority, ulong flags);

        [DllImport("WinDivert.dll", SetLastError = true)]
        public static extern bool WinDivertRecv(IntPtr handle, byte[] packet, uint packetLen, out uint recvLen, [Out] byte[] addr);

        [DllImport("WinDivert.dll", SetLastError = true)]
        public static extern bool WinDivertSend(IntPtr handle, byte[] packet, uint sendLen, out uint sendLenOut, [In] byte[] addr);

        [DllImport("WinDivert.dll", SetLastError = true)]
        public static extern bool WinDivertShutdown(IntPtr handle, uint how);

        [DllImport("WinDivert.dll", SetLastError = true)]
        public static extern bool WinDivertClose(IntPtr handle);

        [DllImport("WinDivert.dll", SetLastError = true)]
        public static extern bool WinDivertSetParam(IntPtr handle, uint param, ulong value);

        public static readonly IntPtr INVALID_HANDLE = new IntPtr(-1);
    }
}
