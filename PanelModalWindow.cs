using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace MCPanel;

public class PanelModalWindow : Window
{
    public const double StandardCardWidth = 820d;
    public const double StandardCardHeight = 600d;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    public PanelModalWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = new SolidColorBrush(Color.FromArgb(0x78, 0x00, 0x00, 0x00));
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        SourceInitialized += (_, _) => FitOverlayToOwner();
        Loaded += (_, _) => FitOverlayToOwner();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        FitOverlayToOwner();
    }

    private void FitOverlayToOwner()
    {
        if (Owner is not null && TryGetWindowBounds(Owner, out var bounds))
        {
            Left = bounds.Left;
            Top = bounds.Top;
            Width = Math.Max(StandardCardWidth + 24d, bounds.Width);
            Height = Math.Max(StandardCardHeight + 24d, bounds.Height);
            return;
        }

        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(workArea.Width, Math.Max(StandardCardWidth + 40d, 1024d));
        Height = Math.Min(workArea.Height, Math.Max(StandardCardHeight + 40d, 700d));
        Left = workArea.Left + Math.Max(0d, (workArea.Width - Width) / 2d);
        Top = workArea.Top + Math.Max(0d, (workArea.Height - Height) / 2d);
    }

    private static bool TryGetWindowBounds(Window window, out Rect bounds)
    {
        bounds = Rect.Empty;
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle != IntPtr.Zero && GetWindowRect(handle, out var rect))
            {
                var source = PresentationSource.FromVisual(window);
                var transform = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
                var topLeft = transform.Transform(new Point(rect.Left, rect.Top));
                var bottomRight = transform.Transform(new Point(rect.Right, rect.Bottom));
                var width = bottomRight.X - topLeft.X;
                var height = bottomRight.Y - topLeft.Y;
                if (width > 0d && height > 0d)
                {
                    bounds = new Rect(topLeft, bottomRight);
                    return true;
                }
            }
        }
        catch
        {
            // Fall back to WPF dimensions below.
        }

        var widthFallback = window.ActualWidth > 0d ? window.ActualWidth : window.Width;
        var heightFallback = window.ActualHeight > 0d ? window.ActualHeight : window.Height;
        if (double.IsNaN(widthFallback) || double.IsNaN(heightFallback) || widthFallback <= 0d || heightFallback <= 0d)
        {
            return false;
        }

        bounds = new Rect(window.Left, window.Top, widthFallback, heightFallback);
        return true;
    }
}
