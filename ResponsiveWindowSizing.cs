using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace MCPanel;

/// <summary>
/// Default main-window bounds built on the golden ratio, so the panel looks
/// the same on every monitor: a φ:1 window covering 1/φ of the work area,
/// placed on the upper golden section. All values are in DIPs, so Windows
/// display scaling is already accounted for.
/// </summary>
internal static class ResponsiveWindowSizing
{
    /// <summary>φ, the golden ratio.</summary>
    public const double GoldenRatio = 1.6180339887498949;

    /// <summary>Share of the work area the window covers: 1/φ ≈ 61.8%.</summary>
    public const double GoldenAreaShare = 1d / GoldenRatio;

    /// <summary>Top margin share of the free vertical space: 1/φ² ≈ 38.2%.</summary>
    public const double GoldenTopShare = 1d - GoldenAreaShare;

    /// <summary>Below this width the panel switches to its compact layouts, so small screens get at least this.</summary>
    public const double ComfortableMinimumWidth = 1024d;

    /// <summary>The window never touches the work-area edges.</summary>
    public const double MaximumWorkAreaFill = 0.94d;

    public const double MinimumWindowWidth = 600d;
    public const double MinimumWindowHeight = 360d;

    /// <summary>
    /// Golden bounds inside <paramref name="workArea"/> (DIPs): width/height = φ,
    /// area = workArea / φ, horizontally centred, top at the 38.2% point of the
    /// remaining height. Small screens are raised to a comfortable width and
    /// very wide or tall screens are capped, always keeping the φ aspect.
    /// </summary>
    public static Rect GoldenBounds(Rect workArea)
    {
        var workWidth = Math.Max(1d, workArea.Width);
        var workHeight = Math.Max(1d, workArea.Height);

        // Area share 1/φ with aspect φ: w·h = W·H/φ and w = φ·h  =>  h = sqrt(W·H) / φ.
        var height = Math.Sqrt(workWidth * workHeight) / GoldenRatio;
        var width = height * GoldenRatio;

        if (width < ComfortableMinimumWidth)
        {
            width = ComfortableMinimumWidth;
            height = width / GoldenRatio;
        }

        // Keep the aspect while fitting both dimensions.
        var fit = Math.Min(1d, Math.Min(workWidth * MaximumWorkAreaFill / width, workHeight * MaximumWorkAreaFill / height));
        width *= fit;
        height *= fit;

        // A screen too short for a comfortable golden rectangle (for example
        // 1366x768 at 125%) gets a usable width rather than a strict φ aspect.
        var comfortableWidth = Math.Min(ComfortableMinimumWidth, workWidth * MaximumWorkAreaFill);
        if (width < comfortableWidth)
        {
            width = comfortableWidth;
            height = workHeight * MaximumWorkAreaFill;
        }

        width = Math.Max(Math.Min(MinimumWindowWidth, workWidth), Math.Floor(width));
        height = Math.Max(Math.Min(MinimumWindowHeight, workHeight), Math.Floor(height));

        var left = workArea.Left + Math.Max(0d, Math.Round((workWidth - width) / 2d));
        var top = workArea.Top + Math.Max(0d, Math.Round((workHeight - height) * GoldenTopShare));
        return new Rect(left, top, width, height);
    }

    /// <summary>
    /// The work area (DIPs) the interface is designed for: a 1080p screen at
    /// 100% scaling. Its golden window shows the interface at scale 1.
    /// </summary>
    public static readonly Size ReferenceWorkArea = new(1920d, 1040d);

    /// <summary>Largest interface scale, reached on a 4K screen at 100% scaling.</summary>
    public const double MaximumUiScale = 2d;

    /// <summary>Scales are rounded to this step so similar screens look identical.</summary>
    public const double UiScaleStep = 0.05d;

    /// <summary>
    /// How much larger the whole interface (text, buttons, cards, spacing) is
    /// drawn on this screen. The golden window grows with the screen's
    /// linear size, so scaling the content by the same factor keeps every
    /// component in the same proportion to the window on every monitor.
    /// Screens smaller than the reference stay at 1: Windows display scaling
    /// already sized them for reading, and shrinking text would hurt it;
    /// those screens use the compact layouts instead.
    /// </summary>
    public static double UiScale(Rect workArea)
    {
        var width = Math.Max(1d, workArea.Width);
        var height = Math.Max(1d, workArea.Height);
        var linear = Math.Sqrt(width * height / (ReferenceWorkArea.Width * ReferenceWorkArea.Height));
        var stepped = Math.Floor(linear / UiScaleStep + 1e-6) * UiScaleStep;
        return Math.Round(Math.Max(1d, Math.Min(MaximumUiScale, stepped)), 2);
    }

    /// <summary>The interface scale for the monitor the window is on, or 1 when unknown.</summary>
    public static double UiScaleForCurrentMonitor(Window window) =>
        TryGetWorkArea(window, out var workArea) ? UiScale(workArea) : 1d;

    /// <summary>
    /// Applies <see cref="GoldenBounds"/> for the monitor the window is on.
    /// <paramref name="uiScale"/> raises the minimum size so a scaled
    /// interface keeps the same minimum logical size.
    /// </summary>
    public static void FitToCurrentMonitor(Window window, double uiScale = 1d)
    {
        if (!TryGetWorkArea(window, out var workArea))
        {
            return;
        }

        var bounds = GoldenBounds(workArea);
        window.MinWidth = Math.Min(MinimumWindowWidth * uiScale, Math.Floor(workArea.Width * MaximumWorkAreaFill));
        window.MinHeight = Math.Min(MinimumWindowHeight * uiScale, Math.Floor(workArea.Height * MaximumWorkAreaFill));
        window.Width = bounds.Width;
        window.Height = bounds.Height;
        window.Left = bounds.Left;
        window.Top = bounds.Top;
    }

    /// <summary>The work area (screen minus taskbar) of the window's monitor, in DIPs.</summary>
    private static bool TryGetWorkArea(Window window, out Rect workArea)
    {
        workArea = Rect.Empty;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        var fromDevice = Matrix.Identity;
        if (PresentationSource.FromVisual(window)?.CompositionTarget is { } compositionTarget)
        {
            fromDevice = compositionTarget.TransformFromDevice;
        }

        var topLeft = fromDevice.Transform(new Point(info.WorkArea.Left, info.WorkArea.Top));
        var bottomRight = fromDevice.Transform(new Point(info.WorkArea.Right, info.WorkArea.Bottom));
        workArea = new Rect(topLeft, bottomRight);
        return workArea.Width >= 1d && workArea.Height >= 1d;
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
