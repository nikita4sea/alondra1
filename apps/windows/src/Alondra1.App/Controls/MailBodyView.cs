using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Alondra1.App.Controls;

/// <summary>
/// Shows a reader document (from <see cref="Alondra1.Core.Html.MailHtml"/>) in an isolated WebView2:
/// scripts, dev tools, host objects, web messages, downloads, permissions, and new windows are off; the only page is
/// served from memory at a private address; every other request is refused unless remote images are allowed, and then
/// only image requests pass, without cookies or referrer. Links never navigate the reader: http, https, and mailto open
/// in the default app. Without the WebView2 runtime, the plain text is shown instead (<see cref="HtmlAvailable"/>).
/// </summary>
public sealed class MailBodyView : Grid
{
    private const string Host = "https://alondra1-reader.invalid/";

    public static readonly DependencyProperty DocumentProperty = DependencyProperty.Register(
        nameof(Document), typeof(string), typeof(MailBodyView), new PropertyMetadata("", (d, _) => ((MailBodyView)d).Show()));

    public static readonly DependencyProperty AllowRemoteProperty = DependencyProperty.Register(
        nameof(AllowRemote), typeof(bool), typeof(MailBodyView), new PropertyMetadata(false));

    public static readonly DependencyProperty FallbackTextProperty = DependencyProperty.Register(
        nameof(FallbackText), typeof(string), typeof(MailBodyView), new PropertyMetadata("", (d, e) => ((MailBodyView)d)._fallback.Text = e.NewValue as string ?? ""));

    /// <summary>The WebView2 user data folder (cache, profile); set once at start, under the app's data folder.</summary>
    public static string UserDataFolder { get; set; } = Path.Combine(Path.GetTempPath(), "alondra1-webview");

    private static Task<CoreWebView2Environment?>? _environment;
    private readonly TextBlock _fallback;
    private readonly ScrollViewer _fallbackScroll;
    private WebView2CompositionControl? _web;
    private int _sequence;
    private string _currentUrl = "";
    private string _currentDocument = "";
    private bool _ready;

    public MailBodyView()
    {
        _fallback = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 20, Margin = new Thickness(0, 12, 0, 0) };
        _fallbackScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _fallback, Visibility = Visibility.Collapsed };
        Children.Add(_fallbackScroll);
        Loaded += async (_, _) => await EnsureAsync();
        // The composition control must not be laid out at 0 x 0 (one-pane mode gives the reader no room); it is
        // collapsed instead and comes back when the reader has space again.
        SizeChanged += (_, e) =>
        {
            if (_web is not null)
                _web.Visibility = e.NewSize.Width < 2 || e.NewSize.Height < 2 ? Visibility.Collapsed : Visibility.Visible;
        };
    }

    public string Document
    {
        get => (string)GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    /// <summary>Whether image requests may leave the app for the current document (follows the image policy).</summary>
    public bool AllowRemote
    {
        get => (bool)GetValue(AllowRemoteProperty);
        set => SetValue(AllowRemoteProperty, value);
    }

    public string FallbackText
    {
        get => (string)GetValue(FallbackTextProperty);
        set => SetValue(FallbackTextProperty, value);
    }

    /// <summary>False when WebView2 could not start; the reader then shows plain text only.</summary>
    public bool HtmlAvailable { get; private set; } = true;

    /// <summary>Raised when HTML cannot be shown (no runtime, or the page failed); the reader offers plain text.</summary>
    public event Action<string>? HtmlFailed;

    /// <summary>Raised after a document finished loading.</summary>
    public event Action? DocumentShown;

    // Diagnostics for the reader check (--open-eml): what the page tried to do.
    public int BlockedRequests { get; private set; }
    public int RemoteRequests { get; private set; }
    public int BlockedNavigations { get; private set; }
    public string DocumentTitle => _web?.CoreWebView2?.DocumentTitle ?? "";

    private static Task<CoreWebView2Environment?> Environment() => _environment ??= CreateEnvironmentAsync();

    private static async Task<CoreWebView2Environment?> CreateEnvironmentAsync()
    {
        try
        {
            Directory.CreateDirectory(UserDataFolder);
            // No sync of browser data, no extensions, no autofill; the reader profile is used for nothing else.
            var options = new CoreWebView2EnvironmentOptions("--disable-features=msSmartScreenProtection --disable-sync --no-first-run")
            {
                AreBrowserExtensionsEnabled = false,
            };
            return await CoreWebView2Environment.CreateAsync(null, UserDataFolder, options);
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            Debug.WriteLine("WebView2 unavailable: " + ex.GetType().Name);
            return null;
        }
    }

    private async Task EnsureAsync()
    {
        if (_web is not null || !HtmlAvailable)
            return;
        var environment = await Environment();
        if (environment is null)
        {
            Fail("Unable to display HTML on this PC (the Microsoft Edge WebView2 Runtime is missing).");
            return;
        }

        var web = new WebView2CompositionControl { DefaultBackgroundColor = System.Drawing.Color.Transparent, Focusable = true };
        System.Windows.Automation.AutomationProperties.SetName(web, "Message body");
        Children.Add(web);
        _web = web;
        try
        {
            await web.EnsureCoreWebView2Async(environment);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
        {
            Children.Remove(web);
            _web = null;
            Fail("Unable to display HTML (" + ex.GetType().Name + ").");
            return;
        }

        var core = web.CoreWebView2;
        var settings = core.Settings;
        settings.IsScriptEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.IsWebMessageEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.AreDevToolsEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false; // no reload, print, find, or other browser shortcuts in mail
        settings.IsBuiltInErrorPageEnabled = false;
        settings.IsZoomControlEnabled = true; // Ctrl + wheel zoom stays available for reading
        settings.IsSwipeNavigationEnabled = false;
        settings.IsPinchZoomEnabled = true;
        settings.HiddenPdfToolbarItems = CoreWebView2PdfToolbarItems.None;

        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += Core_WebResourceRequested;
        core.NavigationStarting += Core_NavigationStarting;
        core.FrameNavigationStarting += (_, e) => { e.Cancel = true; BlockedNavigations++; };
        core.NewWindowRequested += Core_NewWindowRequested;
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.ContextMenuRequested += Core_ContextMenuRequested;
        core.NavigationCompleted += (_, e) =>
        {
            if (!e.IsSuccess && core.Source.StartsWith(Host, StringComparison.Ordinal))
                HtmlFailed?.Invoke("Unable to display HTML (" + e.WebErrorStatus + ").");
            else
                DocumentShown?.Invoke();
        };
        _ready = true;
        Show();
    }

    private void Fail(string reason)
    {
        HtmlAvailable = false;
        _fallbackScroll.Visibility = Visibility.Visible;
        HtmlFailed?.Invoke(reason);
    }

    private void Show()
    {
        if (!HtmlAvailable)
        {
            _fallbackScroll.Visibility = Visibility.Visible;
            return;
        }

        if (!_ready || _web?.CoreWebView2 is null)
            return;
        _currentDocument = Document ?? "";
        _currentUrl = $"{Host}message/{++_sequence}";
        // NavigateToString is limited to 2 MB; serving from memory also lets the page carry real HTTP headers.
        _web.CoreWebView2.Navigate(_currentUrl);
    }

    private void Core_WebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var core = _web!.CoreWebView2;
        var uri = e.Request.Uri;
        if (uri == _currentUrl)
        {
            var bytes = Encoding.UTF8.GetBytes(_currentDocument);
            var headers = "Content-Type: text/html; charset=utf-8\r\n"
                + "Content-Security-Policy: " + Alondra1.Core.Html.MailHtml.ContentSecurityPolicy(AllowRemote) + "\r\n"
                + "Referrer-Policy: no-referrer\r\nX-Content-Type-Options: nosniff\r\nCache-Control: no-store";
            e.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(bytes), 200, "OK", headers);
            return;
        }

        var remote = uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
        if (remote && AllowRemote && e.ResourceContext == CoreWebView2WebResourceContext.Image && !uri.StartsWith(Host, StringComparison.Ordinal))
        {
            // The image goes out, but nothing that identifies the reader beyond the address it comes from.
            e.Request.Headers.RemoveHeader("Cookie");
            e.Request.Headers.RemoveHeader("Referer");
            RemoteRequests++;
            return;
        }

        BlockedRequests++;
        e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
    }

    private void Core_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (e.Uri == _currentUrl)
            return;
        e.Cancel = true;
        BlockedNavigations++;
        if (e.IsUserInitiated)
            OpenExternally(e.Uri);
    }

    private void Core_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        BlockedNavigations++;
        if (e.IsUserInitiated)
            OpenExternally(e.Uri);
    }

    /// <summary>Only copying and selecting are offered; no "open in new window", "save as", "print", or "inspect".</summary>
    private static void Core_ContextMenuRequested(object? sender, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        var keep = new[] { "copy", "copyLinkLocation", "selectAll", "copyImage" };
        for (var i = e.MenuItems.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(e.MenuItems[i].Name))
                e.MenuItems.RemoveAt(i);
        }
    }

    /// <summary>Opens http, https, and mailto links in the default browser or mail app; nothing else.</summary>
    public static bool OpenExternally(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https" or "mailto"))
            return false;
        try
        {
            Process.Start(new ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>A picture of the rendered body, for snapshots (the composition control is not part of a WPF render).</summary>
    public async Task<BitmapSource?> CaptureAsync()
    {
        if (_web?.CoreWebView2 is null || !HtmlAvailable)
            return null;
        using var stream = new MemoryStream();
        var capture = _web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
        if (await Task.WhenAny(capture, Task.Delay(3000)) != capture)
            return null;
        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>Waits until the current document finished loading (diagnostics).</summary>
    public async Task<bool> WaitForDocumentAsync(TimeSpan timeout)
    {
        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < timeout)
        {
            if (!HtmlAvailable)
                return false;
            if (_web?.CoreWebView2 is { } core && core.Source == _currentUrl)
            {
                await Task.Delay(400);
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }

    public UIElement? Surface => (UIElement?)_web ?? _fallbackScroll;
}
