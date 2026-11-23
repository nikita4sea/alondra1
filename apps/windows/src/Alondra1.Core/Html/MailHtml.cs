using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Ganss.Xss;

namespace Alondra1.Core.Html;

/// <summary>Options for turning a message into a reader document.</summary>
public sealed record MailRenderOptions
{
    /// <summary>Load remote (http/https) images. False keeps them blocked: no request leaves the app.</summary>
    public bool AllowRemoteImages { get; init; }

    /// <summary>Largest embedded (data:) image kept, in bytes of the data URI.</summary>
    public int MaxDataImageBytes { get; init; } = 7_000_000;
}

/// <summary>A reader document and what was removed while making it.</summary>
public sealed record MailRenderResult(string Document, int BlockedRemoteImages, int RemovedTrackers, bool Failed, string? FailureReason);

/// <summary>
/// Renders untrusted message HTML for the reader. The HTML is parsed (AngleSharp) and cleaned with an allow-list
/// sanitizer: scripts, event handlers, frames, forms, objects, meta refresh, @import, and javascript:/vbscript: URLs
/// are removed; links keep only http, https, and mailto; images keep data: (image types only) and, when allowed,
/// http/https. The result is wrapped in a document whose Content Security Policy forbids scripts, frames, forms, and
/// any request the image policy does not allow, so the sanitizer is never the only defense.
/// </summary>
public static class MailHtml
{
    public const string BlockedAttribute = "data-a1-blocked";

    private const string DropWithContent = "script, noscript, template, iframe, frame, frameset, object, embed, applet, svg, math, title, textarea, select, option, button, video, audio, canvas, dialog";

    private static readonly string[] ImageTypes = ["image/png", "image/jpeg", "image/jpg", "image/gif", "image/webp", "image/bmp"];

    public static MailRenderResult Render(string? html, MailRenderOptions options)
    {
        if (string.IsNullOrWhiteSpace(html))
            return new MailRenderResult(Wrap("", options.AllowRemoteImages), 0, 0, true, "The message has no HTML.");
        try
        {
            var (body, blocked, trackers) = Clean(html, options);
            if (!HasContent(body))
                return new MailRenderResult(Wrap("", options.AllowRemoteImages), blocked, trackers, true, "Nothing displayable was left after cleaning.");
            return new MailRenderResult(Wrap(body, options.AllowRemoteImages), blocked, trackers, false, null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Only the exception type is reported: messages may quote message content.
            return new MailRenderResult(Wrap("", options.AllowRemoteImages), 0, 0, true, "HTML could not be read (" + ex.GetType().Name + ").");
        }
    }

    /// <summary>A plain-text message as a reader document: escaped, wrapped, with http(s) links and addresses linked.</summary>
    public static string PlainDocument(string? text, PlainColors colors)
    {
        var escaped = Linkify(WebUtility.HtmlEncode(text ?? ""));
        var css = $$"""
            html,body{margin:0;background:{{colors.Background}};color:{{colors.Text}}}
            body{padding:4px 2px 16px;font:14px/1.55 "Segoe UI",system-ui,sans-serif}
            pre{margin:0;white-space:pre-wrap;overflow-wrap:anywhere;word-break:break-word;font:inherit}
            a{color:{{colors.Link}}}
            ::-webkit-scrollbar{width:8px;height:8px}::-webkit-scrollbar-button{display:none}::-webkit-scrollbar-thumb{background:rgba(128,128,128,.55);border-radius:3px}::-webkit-scrollbar-track{background:transparent}
            """;
        return Document($"<pre>{escaped}</pre>", allowRemote: false, css);
    }

    /// <summary>Colors for plain-text documents, so plain text follows the app theme.</summary>
    public sealed record PlainColors(string Background, string Text, string Link);

    // ---------- Cleaning ----------

    private static (string Body, int Blocked, int Trackers) Clean(string html, MailRenderOptions options)
    {
        // Keep the message's own <style> sheets and body attributes (background color), then sanitize them with the body.
        var parser = new HtmlParser();
        var source = parser.ParseDocument(html);
        // Elements whose content is code or embedded documents go with everything inside them; the sanitizer keeps
        // the children of other removed tags (so text inside unknown wrappers such as <o:p> stays readable).
        foreach (var element in source.QuerySelectorAll(DropWithContent).ToList())
            element.Remove();
        var styles = string.Concat(source.QuerySelectorAll("style").Select(style => style.OuterHtml));
        var bodyElement = source.Body;
        var bodyStyle = bodyElement?.GetAttribute("style") ?? "";
        var bodyColor = bodyElement?.GetAttribute("bgcolor");
        if (!string.IsNullOrWhiteSpace(bodyColor) && Regex.IsMatch(bodyColor, "^#?[0-9A-Za-z]{3,20}$"))
            bodyStyle = $"background-color:{bodyColor};" + bodyStyle;
        foreach (var style in bodyElement?.QuerySelectorAll("style").ToList() ?? [])
            style.Remove();
        var fragment = $"{styles}<div class=\"a1-body\" style=\"{WebUtility.HtmlEncode(bodyStyle)}\">{bodyElement?.InnerHtml ?? html}</div>";

        var blocked = 0;
        var sanitizer = CreateSanitizer();
        sanitizer.FilterUrl += (_, e) =>
        {
            var url = e.OriginalUrl.Trim();
            var tag = e.Tag?.LocalName ?? "";
            var isLink = tag is "a" or "area";
            if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                e.SanitizedUrl = !isLink && IsAllowedDataImage(url, options.MaxDataImageBytes) ? url : null;
                return;
            }

            if (url.StartsWith("cid:", StringComparison.OrdinalIgnoreCase))
            {
                e.SanitizedUrl = null; // unresolved inline part: nothing to load
                return;
            }

            if (isLink)
            {
                e.SanitizedUrl = IsSafeLink(url) ? e.SanitizedUrl ?? url : null;
                return;
            }

            // A resource (image, background, CSS url()): remote only when allowed, never another scheme.
            var remote = url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("//", StringComparison.Ordinal);
            if (!remote)
            {
                e.SanitizedUrl = null;
                return;
            }

            if (!options.AllowRemoteImages)
            {
                e.SanitizedUrl = null;
                blocked++;
                if (tag == "img")
                    e.Tag!.SetAttribute(BlockedAttribute, "remote");
            }
        };

        var trackers = 0;
        sanitizer.PostProcessDom += (_, e) =>
        {
            foreach (var image in e.Document.QuerySelectorAll("img").ToList())
            {
                if (IsTracker(image))
                {
                    if (image.HasAttribute(BlockedAttribute))
                        blocked--;
                    image.Remove();
                    trackers++;
                    continue;
                }

                if (!image.HasAttribute("src") && !image.HasAttribute(BlockedAttribute))
                    image.SetAttribute(BlockedAttribute, "missing");
                // Reserve the declared size (scaled down with the page) so nothing jumps when an image loads or stays blocked.
                if (int.TryParse(image.GetAttribute("width"), out var width) && int.TryParse(image.GetAttribute("height"), out var height) && width > 0 && height > 0)
                    image.SetAttribute("style", $"aspect-ratio:{width}/{height};width:{width}px;" + (image.GetAttribute("style") ?? ""));
                // Keep the alt text visible and the reserved size when an image cannot load.
                if (!image.HasAttribute("alt"))
                    image.SetAttribute("alt", "");
            }

            foreach (var link in e.Document.QuerySelectorAll("a[href]"))
                link.SetAttribute("rel", "noopener noreferrer");
        };

        var body = sanitizer.Sanitize(fragment);
        return (body, Math.Max(0, blocked), trackers);
    }

    private static HtmlSanitizer CreateSanitizer()
    {
        var sanitizer = new HtmlSanitizer();
        sanitizer.AllowedTags.Add("style");
        sanitizer.AllowedTags.Add("center");
        sanitizer.AllowedTags.Add("font");
        foreach (var tag in new[] { "form", "input", "button", "select", "option", "textarea", "iframe", "frame", "frameset", "object", "embed", "applet", "script", "noscript", "meta", "link", "base", "svg", "math", "video", "audio", "source", "track", "picture" })
            sanitizer.AllowedTags.Remove(tag);
        foreach (var attribute in new[] { "bgcolor", "background", "align", "valign", "width", "height", "border", "cellpadding", "cellspacing", "color", "face", "size", "class", "id", "role", "dir", "lang" })
            sanitizer.AllowedAttributes.Add(attribute);
        sanitizer.AllowedAttributes.Remove("srcset");
        sanitizer.AllowedAttributes.Remove("formaction");
        sanitizer.AllowedAttributes.Remove("action");
        sanitizer.AllowedAtRules.Add(AngleSharp.Css.Dom.CssRuleType.Media);
        sanitizer.AllowedSchemes.Clear();
        sanitizer.AllowedSchemes.Add("http");
        sanitizer.AllowedSchemes.Add("https");
        sanitizer.AllowedSchemes.Add("mailto");
        sanitizer.AllowedCssProperties.Remove("behavior");
        sanitizer.AllowedCssProperties.Remove("-moz-binding");
        sanitizer.AllowDataAttributes = false;
        sanitizer.AllowedAttributes.Add(BlockedAttribute);
        sanitizer.KeepChildNodes = true;
        return sanitizer;
    }

    private static bool IsSafeLink(string url) =>
        url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith('#');

    /// <summary>data: URIs are kept only for raster image types within the size limit (no SVG, no HTML, no scripts).</summary>
    public static bool IsAllowedDataImage(string url, int maxBytes)
    {
        if (url.Length > maxBytes)
            return false;
        var comma = url.IndexOf(',');
        if (comma < 0)
            return false;
        var header = url[5..comma].ToLowerInvariant();
        var type = header.Split(';')[0].Trim();
        return ImageTypes.Contains(type);
    }

    /// <summary>Obvious tracking pixels: images declared 0-2 px wide or high, or hidden by inline style.</summary>
    public static bool IsTracker(IElement image)
    {
        static bool Tiny(string? value) => value is not null && int.TryParse(value.Trim().TrimEnd('x', 'p'), out var number) && number <= 2;
        var style = (image.GetAttribute("style") ?? "").Replace(" ", "").ToLowerInvariant();
        return (Tiny(image.GetAttribute("width")) && Tiny(image.GetAttribute("height")))
            || Regex.IsMatch(style, @"(^|;)(width:[0-2]px;?.*height:[0-2]px|height:[0-2]px;?.*width:[0-2]px)")
            || style.Contains("display:none") || style.Contains("visibility:hidden");
    }

    private static bool HasContent(string html)
    {
        if (Regex.IsMatch(html, "<img\\b", RegexOptions.IgnoreCase))
            return true;
        var text = Regex.Replace(Regex.Replace(html, "<(style|script|head)[^>]*>.*?</\\1>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline), "<[^>]+>", " ");
        return !string.IsNullOrWhiteSpace(WebUtility.HtmlDecode(text));
    }

    // ---------- Documents ----------

    /// <summary>The Content Security Policy of every reader document.</summary>
    public static string ContentSecurityPolicy(bool allowRemote) =>
        "default-src 'none'; script-src 'none'; object-src 'none'; frame-src 'none'; child-src 'none'; worker-src 'none'; "
        + "connect-src 'none'; media-src 'none'; manifest-src 'none'; form-action 'none'; base-uri 'none'; "
        + "style-src 'unsafe-inline'; font-src data:; img-src data:" + (allowRemote ? " https: http:" : "") + ";";

    private static string Wrap(string body, bool allowRemote)
    {
        const string css = """
            html{background:#ffffff}
            body{margin:0;padding:16px;color:#1f1f1f;font:14px/1.5 "Segoe UI",system-ui,sans-serif;overflow-wrap:anywhere}
            .a1-mail{max-width:760px;margin:0 auto;overflow-x:auto}
            .a1-mail img{max-width:100%;height:auto}
            .a1-mail table{max-width:none}
            .a1-mail pre{white-space:pre-wrap}
            ::-webkit-scrollbar{width:8px;height:8px}::-webkit-scrollbar-button{display:none}::-webkit-scrollbar-thumb{background:rgba(128,128,128,.55);border-radius:3px}::-webkit-scrollbar-track{background:transparent}
            img[data-a1-blocked]{display:inline-block;max-width:100%;background:#f1f0f5;outline:1px dashed #b9b6c8;min-width:16px;min-height:16px;color:#5f5e7a;font-size:12px;overflow:hidden}
            """;
        return Document($"<div class=\"a1-mail\">{body}</div>", allowRemote, css);
    }

    private static string Document(string body, bool allowRemote, string css)
    {
        var builder = new StringBuilder();
        builder.Append("<!doctype html><html><head><meta charset=\"utf-8\">");
        builder.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"").Append(ContentSecurityPolicy(allowRemote)).Append("\">");
        builder.Append("<meta name=\"referrer\" content=\"no-referrer\">");
        builder.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        builder.Append("<style>").Append(css).Append("</style></head><body>");
        builder.Append(body);
        builder.Append("</body></html>");
        return builder.ToString();
    }

    private static string Linkify(string escaped)
    {
        var linked = Regex.Replace(escaped, @"\bhttps?://[^\s<>""']+", match =>
        {
            var url = match.Value.TrimEnd('.', ',', ')', ';', ':', '!', '?');
            var rest = match.Value[url.Length..];
            return $"<a href=\"{url}\">{url}</a>{rest}";
        }, RegexOptions.IgnoreCase);
        return Regex.Replace(linked, @"(?<![\w.:/="">-])[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b", match => $"<a href=\"mailto:{match.Value}\">{match.Value}</a>");
    }
}
