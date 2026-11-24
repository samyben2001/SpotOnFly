using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SpotOnFly.Interop;

/// <summary>
/// Retourne la zone de travail de l'écran qui contient l'overlay, exprimée en DIP.
/// On interroge directement l'API Win32 pour obtenir des valeurs fraîches après
/// un changement de résolution, de barre des tâches ou de mise à l'échelle.
/// </summary>
internal static class DisplayInterop
{
    private const uint MonitorDefaultToPrimary = 1;
    private const uint MonitorDefaultToNearest = 2;

    public static Rect GetWorkArea(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;

        var monitor = handle != IntPtr.Zero
            ? MonitorFromWindow(handle, MonitorDefaultToNearest)
            : MonitorFromPoint(default, MonitorDefaultToPrimary);

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return SystemParameters.WorkArea;
        }

        var dpi = handle != IntPtr.Zero ? GetDpiForWindow(handle) : 96;
        if (dpi == 0)
        {
            dpi = 96;
        }

        var scale = dpi / 96.0;
        return new Rect(
            info.Work.Left / scale,
            info.Work.Top / scale,
            (info.Work.Right - info.Work.Left) / scale,
            (info.Work.Bottom - info.Work.Top) / scale);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
