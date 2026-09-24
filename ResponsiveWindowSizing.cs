using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace MCPanel;

internal static class ResponsiveWindowSizing
{
    public const double MainDesignWidth = 1280;
    public const double MainDesignHeight = 820;
    public const double MainDefaultScale = 1.0;

    public static void FitToCurrentMonitor(
        Window window,
        double designWidth,
        double designHeight,
        double workAreaFill = 0.96,
        double maximumScale = 1.35)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        var fromDevice = Matrix.Identity;
        if (PresentationSource.FromVisual(window)?.CompositionTarget is { } compositionTarget)
        {
            fromDevice = compositionTarget.TransformFromDevice;
        }

        var topLeft = fromDevice.Transform(new Point(info.WorkArea.Left, info.WorkArea.Top));
        var bottomRight = fromDevice.Transform(new Point(info.WorkArea.Right, info.WorkArea.Bottom));
        var workWidth = Math.Max(1, bottomRight.X - topLeft.X);
        var workHeight = Math.Max(1, bottomRight.Y - topLeft.Y);
        var scale = Math.Min(
            workWidth * workAreaFill / designWidth,
            workHeight * workAreaFill / designHeight);
        scale = Math.Max(0.1, Math.Min(maximumScale, scale));

        window.MinWidth = Math.Min(600d, Math.Floor(workWidth * workAreaFill));
        window.MinHeight = Math.Min(360d, Math.Floor(workHeight * workAreaFill));
        var width = Math.Max(window.MinWidth, Math.Min(workWidth, Math.Floor(designWidth * scale)));
        var height = Math.Max(window.MinHeight, Math.Min(workHeight, Math.Floor(designHeight * scale)));
        window.Width = width;
        window.Height = height;
        window.Left = topLeft.X + Math.Max(0, (workWidth - width) / 2);
        window.Top = topLeft.Y + Math.Max(0, (workHeight - height) / 2);
    }

    private const uint MonitorDefaultToNearest = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitorHandle, ref MonitorInfo monitorInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }
}
