using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Alondra.Core.Config;

namespace Alondra.App.Shell;

/// <summary>
/// Workspace backgrounds: the bundled images, the user's own picture, and the opacity, blur, desaturation,
/// brightness, overlay, fit, and position adjustments. Everything is local; nothing is downloaded or uploaded.
/// Desaturation and brightness are applied once to a cached copy; blur runs on the GPU.
/// </summary>
public static class BackgroundService
{
    private const int DisplayWidth = 1920;
    private const int StoredLongEdge = 2560;
    private static readonly Dictionary<string, BitmapSource> Decoded = new(StringComparer.OrdinalIgnoreCase);
    private static (string Key, BitmapSource Image)? _processed;

    /// <summary>Folder for the app-managed copy of the user's picture.</summary>
    public static string ManagedFolder => System.IO.Path.Combine(App.Services.Home, "backgrounds");

    public static bool IsActive(BackgroundSettings settings)
    {
        var s = settings.Normalized();
        return s.Enabled && s.Image != BackgroundCatalog.None && Source(s) is not null;
    }

    public static void Apply(BackgroundSettings settings, Image image, Rectangle overlay)
    {
        var s = settings.Normalized();
        var source = s.Enabled ? Source(s) : null;
        if (source is null)
        {
            image.Source = null;
            image.Visibility = Visibility.Collapsed;
            overlay.Visibility = Visibility.Collapsed;
            return;
        }

        image.Source = Process(source, s.Desaturation, s.Brightness, ThemeService.IsDark, KeyFor(s) + "|" + ThemeService.IsDark);
        image.Opacity = s.Opacity;
        image.Stretch = s.Fit == BackgroundFit.Cover ? Stretch.UniformToFill : Stretch.Uniform;
        image.HorizontalAlignment = s.Position switch
        {
            BackgroundPosition.Left => HorizontalAlignment.Left,
            BackgroundPosition.Right => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Center,
        };
        image.VerticalAlignment = s.Position switch
        {
            BackgroundPosition.Top => VerticalAlignment.Top,
            BackgroundPosition.Bottom => VerticalAlignment.Bottom,
            _ => VerticalAlignment.Center,
        };
        image.Effect = s.Blur > 0.5 ? new BlurEffect { Radius = s.Blur, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance } : null;
        image.Visibility = Visibility.Visible;
        overlay.Opacity = s.Overlay;
        overlay.Visibility = s.Overlay > 0.001 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The unprocessed picture for these settings, or null for none, or when a custom file is missing or unreadable.</summary>
    public static BitmapSource? Source(BackgroundSettings s)
    {
        if (s.Image == BackgroundCatalog.None)
            return null;
        if (s.Image == BackgroundCatalog.Custom)
            return s.CustomPath is { } path && File.Exists(path) ? Decode(path, isFile: true, DisplayWidth) : null;
        return Decode(s.Image, isFile: false, DisplayWidth);
    }

    public static BitmapSource? Thumbnail(string id, string? customPath, int width = 152) =>
        id == BackgroundCatalog.Custom
            ? customPath is not null && File.Exists(customPath) ? Decode(customPath, isFile: true, width) : null
            : id == BackgroundCatalog.None ? null : Decode(id, isFile: false, width);

    /// <summary>
    /// Validates a picture and stores a re-encoded copy in Alondra's data folder, so the background survives the
    /// original being moved or deleted. Returns the stored path. Throws <see cref="InvalidDataException"/> with a message for the user.
    /// </summary>
    public static string ImportCustom(string sourcePath, string? previous)
    {
        if (BackgroundImageFile.Check(sourcePath) is { } problem)
            throw new InvalidDataException(problem);
        BitmapSource frame;
        try
        {
            using var stream = File.OpenRead(sourcePath);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            frame = decoder.Frames[0];
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException or InvalidOperationException or ArgumentException)
        {
            throw new InvalidDataException(System.IO.Path.GetExtension(sourcePath).Equals(".webp", StringComparison.OrdinalIgnoreCase)
                ? "Windows cannot read this WebP picture. Install “WebP Image Extensions” from Microsoft Store, or choose a PNG or JPEG."
                : "The picture could not be read: " + ex.Message);
        }

        if (frame.PixelWidth < 2 || frame.PixelHeight < 2)
            throw new InvalidDataException("The picture is too small.");
        var scale = Math.Min(1.0, (double)StoredLongEdge / Math.Max(frame.PixelWidth, frame.PixelHeight));
        BitmapSource scaled = scale < 1 ? new TransformedBitmap(frame, new ScaleTransform(scale, scale)) : frame;
        var opaque = new FormatConvertedBitmap(scaled, PixelFormats.Bgr24, null, 0);
        Directory.CreateDirectory(ManagedFolder);
        var target = System.IO.Path.Combine(ManagedFolder, "custom-" + Guid.NewGuid().ToString("n") + ".jpg");
        var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
        encoder.Frames.Add(BitmapFrame.Create(opaque));
        using (var output = File.Create(target))
            encoder.Save(output);
        DeleteCustom(previous);
        return target;
    }

    /// <summary>Deletes a stored custom picture. Only files inside the managed folder are ever deleted.</summary>
    public static void DeleteCustom(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return;
        try
        {
            var full = System.IO.Path.GetFullPath(path);
            var folder = System.IO.Path.GetFullPath(ManagedFolder).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            if (full.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && File.Exists(full))
                File.Delete(full);
            Decoded.Remove(full + "|" + DisplayWidth);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            System.Diagnostics.Debug.WriteLine("Background cleanup skipped: " + ex.Message);
        }
    }

    private static string KeyFor(BackgroundSettings s) => $"{(s.Image == BackgroundCatalog.Custom ? s.CustomPath : s.Image)}|{s.Desaturation:0.00}|{s.Brightness:0.00}";

    private static BitmapSource? Decode(string idOrPath, bool isFile, int width)
    {
        var key = idOrPath + "|" + width;
        if (Decoded.TryGetValue(key, out var cached))
            return cached;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.DecodePixelWidth = width;
            bitmap.UriSource = isFile ? new Uri(idOrPath, UriKind.Absolute) : new Uri($"pack://application:,,,/Assets/Backgrounds/{idOrPath}.jpg");
            bitmap.EndInit();
            bitmap.Freeze();
            Decoded[key] = bitmap;
            return bitmap;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException or UriFormatException or InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine("Background not loaded: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Desaturates, adapts the picture's brightness to the theme, then applies the user's brightness. The result is cached.
    /// Adapting keeps the picture recognizable while text over it stays readable: in the dark theme a light picture is
    /// darkened toward an average of 36%, in the light theme a dark picture is lifted toward 72%. Contrast and detail
    /// are kept because the whole tone range is scaled, not flattened.
    /// </summary>
    private static BitmapSource Process(BitmapSource source, double desaturation, double brightness, bool dark, string key)
    {
        if (_processed is { } hit && hit.Key == key)
            return hit.Image;
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        var keep = 1 - desaturation;
        var shift = brightness * 255;

        double total = 0;
        var samples = 0;
        for (var i = 0; i < pixels.Length; i += 4 * 16)
        {
            total += 0.2126 * pixels[i + 2] + 0.7152 * pixels[i + 1] + 0.0722 * pixels[i];
            samples++;
        }

        var mean = samples == 0 ? 128 : total / samples;
        const double darkTarget = 0.36 * 255, lightTarget = 0.72 * 255;
        var darken = dark && mean > darkTarget ? darkTarget / mean : 1.0;
        var lift = !dark && mean < lightTarget ? (255 - lightTarget) / Math.Max(1, 255 - mean) : 1.0;
        double Tone(double value) => lift < 1 ? 255 - (255 - value) * lift : value * darken;

        for (var i = 0; i < pixels.Length; i += 4)
        {
            double b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
            var gray = 0.2126 * r + 0.7152 * g + 0.0722 * b;
            pixels[i] = Byte(Tone(gray + (b - gray) * keep) + shift);
            pixels[i + 1] = Byte(Tone(gray + (g - gray) * keep) + shift);
            pixels[i + 2] = Byte(Tone(gray + (r - gray) * keep) + shift);
        }

        var result = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        _processed = (key, result);
        return result;
    }

    private static byte Byte(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);
}
