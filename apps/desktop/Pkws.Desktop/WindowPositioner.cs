using System.Runtime.InteropServices;
using System.Windows;

namespace Pkws.Desktop;

public static class WindowPositioner
{
    private const uint MonitorDefaultToNearest = 2;

    public static void PlacePanelAtCurrentScreenRight(Window window)
    {
        if (!TryGetCurrentWorkArea(window, out var work))
        {
            return;
        }

        var width = Math.Clamp(double.IsFinite(window.Width) ? window.Width : work.Width * 0.28, 420, Math.Min(620, work.Width));
        window.Width = width;
        window.Height = Math.Max(520, work.Height - 24);
        window.Left = work.Right - width - 12;
        window.Top = work.Top + 12;
    }

    public static void PlaceLauncherAtCurrentScreenRight(Window window, double width)
    {
        if (!TryGetCurrentWorkArea(window, out var work))
        {
            return;
        }

        window.Width = width;
        window.Height = Math.Min(150, work.Height * 0.24);
        window.Left = work.Right - width;
        window.Top = work.Top + (work.Height - window.Height) / 2;
    }

    private static bool TryGetCurrentWorkArea(Window window, out WorkArea workArea)
    {
        workArea = default;
        if (!GetCursorPos(out var cursor))
        {
            return false;
        }

        var monitor = MonitorFromPoint(cursor, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        var source = PresentationSource.FromVisual(window);
        var transform = source?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
        var topLeft = transform.Transform(new System.Windows.Point(info.Work.Left, info.Work.Top));
        var bottomRight = transform.Transform(new System.Windows.Point(info.Work.Right, info.Work.Bottom));
        workArea = new WorkArea(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y);
        return true;
    }

    private readonly record struct WorkArea(double Left, double Top, double Right, double Bottom)
    {
        public double Width => Right - Left;
        public double Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointNative
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
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
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out PointNative point);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(PointNative point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
