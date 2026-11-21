using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Alondra1.App.Shell;

public static class ShellWindow
{
    private const int DwmWindowCornerPreference = 33;
    private const int DwmRound = 2;
    private const int DwmDoNotRound = 1;
    private const int MonitorDefaultToNearest = 2;
    private const int WmGetMinMaxInfo = 0x0024;

    public static void Attach(Window window)
    {
        var chrome = new System.Windows.Shell.WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        };
        System.Windows.Shell.WindowChrome.SetWindowChrome(window, chrome);
        window.SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(window) is HwndSource source)
                source.AddHook((IntPtr _, int message, IntPtr _, IntPtr lParam, ref bool handled) =>
                {
                    handled = false;
                    return Hook(window, message, lParam);
                });
            ApplyCorners(window);
        };
        window.StateChanged += (_, _) => ApplyCorners(window);
        window.SizeChanged += (_, _) => ApplyCorners(window);
    }

    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmBorderColor = 34;
    private const int DwmCaptionColor = 35;
    private const int DwmTextColor = 36;

    /// <summary>
    /// Makes a standard (native) title bar follow the Alondra1 theme: dark mode on Windows 10 2004 and later, and on
    /// Windows 11 the exact caption, title text, and border colors of the current palette. The title bar stays native,
    /// so snapping, resizing, keyboard, and accessibility behave as usual. Unsupported attributes are ignored by Windows.
    /// </summary>
    public static void ApplyCaption(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return;
        var dark = ThemeService.IsDark ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref dark, sizeof(int));
        if (Color("ChromeBrush") is { } caption)
            _ = DwmSetWindowAttribute(handle, DwmCaptionColor, ref caption, sizeof(int));
        if (Color("TextBrush") is { } text)
            _ = DwmSetWindowAttribute(handle, DwmTextColor, ref text, sizeof(int));
        if (Color("LineBrush") is { } border)
            _ = DwmSetWindowAttribute(handle, DwmBorderColor, ref border, sizeof(int));
        var round = DwmRound;
        _ = DwmSetWindowAttribute(handle, DwmWindowCornerPreference, ref round, sizeof(int));
    }

    /// <summary>Re-applies title-bar colors to every open window after a theme change.</summary>
    public static void ApplyCaptionToAll()
    {
        foreach (Window window in Application.Current.Windows)
        {
            if (window.WindowStyle != WindowStyle.None)
                ApplyCaption(window);
        }
    }

    /// <summary>Hooks a secondary window so its title bar follows the theme from the moment it appears.</summary>
    public static void ThemeCaption(Window window)
    {
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
            ApplyCaption(window);
        else
            window.SourceInitialized += (_, _) => ApplyCaption(window);
    }

    /// <summary>A palette color as a Win32 COLORREF (0x00BBGGRR).</summary>
    private static int? Color(string key) =>
        Application.Current.TryFindResource(key) is System.Windows.Media.SolidColorBrush { Color: var c } ? c.R | (c.G << 8) | (c.B << 16) : null;

    public static void ApplyCorners(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return;
        var preference = window.WindowState == WindowState.Maximized ? DwmDoNotRound : DwmRound;
        _ = DwmSetWindowAttribute(handle, DwmWindowCornerPreference, ref preference, sizeof(int));
        if (window.WindowState == WindowState.Maximized)
        {
            SetWindowRgn(handle, IntPtr.Zero, true);
            return;
        }

        var source = PresentationSource.FromVisual(window);
        var scaleX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1;
        var scaleY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1;
        var width = Math.Max(1, (int)(window.ActualWidth * scaleX));
        var height = Math.Max(1, (int)(window.ActualHeight * scaleY));
        var radius = (int)Math.Round(14 * Math.Max(scaleX, scaleY));
        var region = CreateRoundRectRgn(0, 0, width + 1, height + 1, radius, radius);
        SetWindowRgn(handle, region, true);
    }

    private static IntPtr Hook(Window window, int message, IntPtr lParam)
    {
        if (message != WmGetMinMaxInfo)
            return IntPtr.Zero;
        var handle = new WindowInteropHelper(window).Handle;
        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
            return IntPtr.Zero;
        var minMax = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        var work = info.rcWork;
        var monitorRect = info.rcMonitor;
        minMax.ptMaxPosition.x = work.Left - monitorRect.Left;
        minMax.ptMaxPosition.y = work.Top - monitorRect.Top;
        minMax.ptMaxSize.x = work.Right - work.Left;
        minMax.ptMaxSize.y = work.Bottom - work.Top;
        Marshal.StructureToPtr(minMax, lParam, true);
        return IntPtr.Zero;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct PointNative
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public PointNative ptReserved;
        public PointNative ptMaxSize;
        public PointNative ptMaxPosition;
        public PointNative ptMinTrackSize;
        public PointNative ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectNative
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int cbSize;
        public RectNative rcMonitor;
        public RectNative rcWork;
        public int dwFlags;
    }
}
