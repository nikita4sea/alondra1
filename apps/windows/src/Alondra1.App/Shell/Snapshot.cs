using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Alondra1.App.Shell;

/// <summary>
/// Diagnostics for visual review (demo runs only): renders the main window, or opens a dialog and captures it from
/// the screen including its title bar, and writes measured geometry next to the image.
/// </summary>
public static class Snapshot
{
    /// <param name="overlays">Pictures of elements WPF cannot render itself (the WebView2 message body), drawn at their place.</param>
    public static void RenderElement(FrameworkElement root, string path, IEnumerable<(FrameworkElement Element, BitmapSource Picture)>? overlays = null)
    {
        var scale = VisualTreeHelper.GetDpi(root);
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(root.ActualWidth * scale.DpiScaleX), (int)Math.Ceiling(root.ActualHeight * scale.DpiScaleY),
            scale.PixelsPerInchX, scale.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(root);
        BitmapSource result = bitmap;
        if (overlays is not null)
        {
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.DrawImage(bitmap, new System.Windows.Rect(0, 0, root.ActualWidth, root.ActualHeight));
                foreach (var (element, picture) in overlays)
                {
                    if (!element.IsVisible)
                        continue;
                    var origin = element.TranslatePoint(new Point(0, 0), root);
                    context.DrawImage(picture, new System.Windows.Rect(origin, new Size(element.ActualWidth, element.ActualHeight)));
                }
            }

            var composed = new RenderTargetBitmap(bitmap.PixelWidth, bitmap.PixelHeight, scale.PixelsPerInchX, scale.PixelsPerInchY, PixelFormats.Pbgra32);
            composed.Render(visual);
            result = composed;
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(result));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    /// <summary>Takes an element's area from the screen (for content WPF cannot render itself). The window must be in front.</summary>
    public static BitmapSource? CaptureScreen(FrameworkElement element)
    {
        try
        {
            var window = Window.GetWindow(element);
            window?.Activate();
            var topLeft = element.PointToScreen(new Point(0, 0));
            var bottomRight = element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight));
            var width = (int)Math.Round(bottomRight.X - topLeft.X);
            var height = (int)Math.Round(bottomRight.Y - topLeft.Y);
            if (width <= 0 || height <= 0)
                return null;
            using var bitmap = new System.Drawing.Bitmap(width, height);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                graphics.CopyFromScreen((int)Math.Round(topLeft.X), (int)Math.Round(topLeft.Y), 0, 0, bitmap.Size);
            using var memory = new MemoryStream();
            bitmap.Save(memory, System.Drawing.Imaging.ImageFormat.Png);
            memory.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = memory;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Opens a dialog with <paramref name="show"/>, captures it once it has rendered, then closes it.</summary>
    public static void CaptureDialog(Window owner, Action show, string path)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var dialog = Application.Current.Windows.OfType<Window>().LastOrDefault(window => window != owner && window.IsVisible);
            if (dialog is null)
                return;
            dialog.Activate();
            var report = new StringBuilder();
            try
            {
                var handle = new WindowInteropHelper(dialog).Handle;
                GetWindowRect(handle, out var rect);
                using (var bitmap = new System.Drawing.Bitmap(rect.Right - rect.Left, rect.Bottom - rect.Top))
                {
                    using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                        graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, bitmap.Size);
                    bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                }

                report.Append($"window {dialog.ActualWidth:0}x{dialog.ActualHeight:0}; ");
                report.Append(DwmGetWindowAttribute(handle, 35, out var caption, 4) == 0 ? $"caption color #{caption & 0xFF:X2}{(caption >> 8) & 0xFF:X2}{(caption >> 16) & 0xFF:X2}; " : "caption color not available; ");
                report.Append(DwmGetWindowAttribute(handle, 20, out var dark, 4) == 0 ? $"dark title bar {dark != 0}; " : "");
                report.Append(Measure(dialog));
            }
            catch (Exception ex)
            {
                report.Append("capture failed: " + ex.Message);
            }

            File.WriteAllText(path + ".txt", report.ToString());
            dialog.Close();
        };
        timer.Start();
        show();
    }

    /// <summary>Heights of buttons, text boxes, and selectors in a window, to check them against the size tokens.</summary>
    public static string Measure(DependencyObject root)
    {
        var heights = new SortedDictionary<string, SortedSet<int>>();
        void Walk(DependencyObject node)
        {
            if (node is FrameworkElement { IsVisible: true } element && element.ActualHeight > 0)
            {
                var kind = element switch
                {
                    Button => "Button",
                    ComboBox => "ComboBox",
                    TextBox box when !box.AcceptsReturn => "TextBox",
                    PasswordBox => "PasswordBox",
                    Slider => "Slider",
                    _ => null,
                };
                if (kind is not null)
                {
                    if (!heights.TryGetValue(kind, out var set))
                        heights[kind] = set = [];
                    set.Add((int)Math.Round(element.ActualHeight));
                }
            }

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                Walk(VisualTreeHelper.GetChild(node, i));
        }

        Walk(root);
        return string.Join("; ", heights.Select(pair => $"{pair.Key} heights {string.Join("/", pair.Value)}"));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
