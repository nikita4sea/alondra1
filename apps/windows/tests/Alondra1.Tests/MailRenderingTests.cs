using System.Text.RegularExpressions;
using Alondra1.Core.Config;
using Alondra1.Core.Html;
using Alondra1.Core.Mail;
using Alondra1.Mail;
using MimeKit;

namespace Alondra1.Tests;

/// <summary>Client spec M2 (§4, fixtures M01-M12): MIME body selection, inline images, sanitizing, image policy, attachments.</summary>
public class MailRenderingTests
{
    private static MimeMessage Load(string name) => MimeMessage.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Mail", name));

    private static StoredBody Body(string name) => MimeBody.Extract(Load(name), "acct", "INBOX", 7);

    private static MailRenderResult Html(string name, bool remote = false) => MailHtml.Render(Body(name).Html, new MailRenderOptions { AllowRemoteImages = remote });

    private static string BodyOf(MailRenderResult result) => Regex.Match(result.Document, "<body>(.*)</body>", RegexOptions.Singleline).Groups[1].Value;

    [Fact]
    public void M01_plain_only_has_no_html_and_links_safely()
    {
        var body = Body("M01-plain-long-url.eml");
        Assert.Null(body.Html);
        Assert.False(ReaderPolicy.OpenAsHtml(new ReadingSettings(), body.Html is not null, null));
        var document = MailHtml.PlainDocument(body.Text, new MailHtml.PlainColors("#000", "#fff", "#88f"));
        Assert.Contains("<a href=\"https://example.com/reports/2026/11/", document);
        Assert.Contains("<a href=\"mailto:support@example.com\">", document);
        Assert.Contains("overflow-wrap:anywhere", document);
        Assert.DoesNotContain("<script", document, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void M02_alternative_prefers_html_and_keeps_text()
    {
        var body = Body("M02-alternative.eml");
        Assert.Contains("HTML VERSION", body.Html);
        Assert.Contains("PLAIN VERSION", body.Text);
        Assert.True(ReaderPolicy.OpenAsHtml(new ReadingSettings(), hasHtml: true, chosenForMessage: null));
        Assert.False(ReaderPolicy.OpenAsHtml(new ReadingSettings(), hasHtml: true, chosenForMessage: false));
        Assert.False(ReaderPolicy.OpenAsHtml(new ReadingSettings { PreferHtml = false }, hasHtml: true, chosenForMessage: null));
        Assert.Empty(body.Attachments);
    }

    [Fact]
    public void M03_newsletter_keeps_structure_styles_and_safe_links()
    {
        var result = Html("M03-newsletter.eml");
        Assert.False(result.Failed);
        var body = BodyOf(result);
        Assert.Contains("<table", body);
        Assert.Contains("Autumn news", body);
        Assert.Contains("background-color", body); // body bgcolor kept on the wrapper
        Assert.Contains("<style>", body);
        Assert.Contains("@media", body);
        Assert.Contains("href=\"https://example.com/offer\"", body);
        Assert.Contains("rel=\"noopener noreferrer\"", body);
    }

    [Fact]
    public void M04_cid_images_are_embedded_and_need_no_network()
    {
        var body = Body("M04-cid-images.eml");
        Assert.Equal(2, Regex.Matches(body.Html!, "src=\"data:image/png;base64,").Count);
        Assert.DoesNotContain("cid:", body.Html);
        Assert.Empty(body.Attachments); // inline pictures are not attachments
        var result = MailHtml.Render(body.Html, new MailRenderOptions { AllowRemoteImages = false });
        Assert.Equal(2, Regex.Matches(result.Document, "src=\"data:image/png;base64,").Count);
        Assert.Equal(0, result.BlockedRemoteImages);
        Assert.Contains("img-src data:;", result.Document); // offline: nothing but embedded data is allowed
    }

    [Fact]
    public void M05_remote_images_are_blocked_until_allowed_and_trackers_always_go()
    {
        var blocked = Html("M05-remote-images.eml", remote: false);
        var body = BodyOf(blocked);
        Assert.DoesNotContain("https://img.example.com", body);
        Assert.DoesNotContain("track.example.com", body);
        Assert.Equal(3, blocked.BlockedRemoteImages); // logo, hero, CSS background
        Assert.Equal(1, blocked.RemovedTrackers);
        Assert.Contains("alt=\"Example logo\"", body);
        Assert.Contains("width=\"600\" height=\"300\"", body);
        Assert.Contains("aspect-ratio:600/300;width:600px", body); // space is reserved even without the picture
        Assert.Contains(MailHtml.BlockedAttribute, body);
        Assert.DoesNotContain("https:", MailHtml.ContentSecurityPolicy(allowRemote: false));

        var allowed = Html("M05-remote-images.eml", remote: true);
        Assert.Contains("src=\"https://img.example.com/logo.png\"", allowed.Document);
        Assert.DoesNotContain("track.example.com", allowed.Document);
        Assert.Contains("img-src data: https: http:", allowed.Document);
    }

    [Fact]
    public void Image_policy_follows_settings_trust_and_the_per_message_choice()
    {
        var settings = new ReadingSettings();
        Assert.False(ReaderPolicy.AllowRemoteImages(settings, "news@example.com", loadedForMessage: false));
        Assert.True(ReaderPolicy.AllowRemoteImages(settings, "news@example.com", loadedForMessage: true));
        Assert.True(ReaderPolicy.Trust(settings, "News@Example.com"));
        Assert.False(ReaderPolicy.Trust(settings, "news@example.com"));
        Assert.True(ReaderPolicy.AllowRemoteImages(settings, "NEWS@example.com", loadedForMessage: false));
        Assert.False(ReaderPolicy.AllowRemoteImages(settings, "other@example.com", loadedForMessage: false));
        settings.RemoteImages = RemoteImagePolicy.Always;
        Assert.True(ReaderPolicy.AllowRemoteImages(settings, "other@example.com", loadedForMessage: false));
        Assert.False(ReaderPolicy.Trust(settings, "not an address"));
    }

    [Fact]
    public void M06_attachments_are_listed_and_inline_images_are_not()
    {
        var message = Load("M06-attachments.eml");
        var body = MimeBody.Extract(message, "acct", "INBOX", 7);
        Assert.Equal(["photo.png", "report.pdf"], body.Attachments.Select(item => item.FileName));
        Assert.Equal(["image/png", "application/pdf"], body.Attachments.Select(item => item.ContentType));
        Assert.All(body.Attachments, item => Assert.True(item.Size > 0));
        Assert.Equal("acct|INBOX|7|1", body.Attachments[1].Id);
        Assert.Contains("data:image/png;base64,", body.Html);

        var pdf = MimeBody.FindAttachment(message, "report.pdf");
        Assert.NotNull(pdf);
        using var copy = new MemoryStream();
        MimeBody.WriteAsync(pdf, copy, CancellationToken.None).GetAwaiter().GetResult();
        Assert.StartsWith("%PDF-1.4", System.Text.Encoding.ASCII.GetString(copy.ToArray()));
        Assert.Equal(copy.Length, body.Attachments[1].Size);
    }

    [Fact]
    public void M07_quoted_printable_and_utf8_survive()
    {
        var message = Load("M07-quoted-printable-utf8.eml");
        Assert.Equal("M07 Привет 👋", message.Subject);
        var body = BodyOf(MailHtml.Render(MimeBody.Extract(message).Html, new MailRenderOptions()));
        Assert.Contains("Привет! Zażółć gęślą jaźń. Ünïcödé — “quotes” 👋", body);
        Assert.DoesNotContain("=D0", body);
    }

    [Fact]
    public void M08_nested_multiparts_with_mixed_case_resolve()
    {
        var body = Body("M08-nested.eml");
        Assert.Contains("Nested HTML", body.Html);
        Assert.Contains("data:image/png;base64,", body.Html);
        Assert.Equal("Nested plain.", body.Text.Trim());
        Assert.Empty(body.Attachments);
    }

    [Fact]
    public void M09_wide_content_is_contained_by_the_reader()
    {
        var result = Html("M09-overflow.eml");
        Assert.Contains(".a1-mail{max-width:760px;margin:0 auto;overflow-x:auto}", result.Document);
        Assert.Contains("overflow-wrap:anywhere", result.Document);
        Assert.Contains("width=\"1200\"", result.Document); // the table keeps its width and scrolls inside the body only
    }

    [Fact]
    public void M10_active_content_is_removed()
    {
        var result = Html("M10-malicious.eml", remote: true);
        var body = BodyOf(result);
        Assert.Contains("Safe text", body);
        foreach (var forbidden in new[] { "<script", "onload", "onerror", "javascript:", "vbscript:", "<form", "<input", "<button", "<iframe", "<object", "<embed", "<svg", "<meta", "<base", "<link", "@import", "evil.example", "data:text/html", "data:image/svg", "document.title", "pwned", "msgbox" })
            Assert.DoesNotContain(forbidden, body, StringComparison.OrdinalIgnoreCase);
        var policy = MailHtml.ContentSecurityPolicy(allowRemote: true);
        Assert.Contains("script-src 'none'", policy);
        Assert.Contains("form-action 'none'", policy);
        Assert.Contains("frame-src 'none'", policy);
        Assert.Contains("base-uri 'none'", policy);
    }

    [Fact]
    public void M11_unreachable_images_keep_alt_text_and_size()
    {
        var body = BodyOf(Html("M11-broken-remote.eml", remote: true));
        Assert.Contains("alt=\"Missing picture\"", body);
        Assert.Contains("width=\"300\" height=\"100\"", body);
        Assert.Contains("Before", body);
        Assert.Contains("After", body);
        Assert.Contains("img[data-a1-blocked]", Html("M11-broken-remote.eml").Document);
    }

    [Fact]
    public void M12_html_without_displayable_content_falls_back_to_text()
    {
        var body = Body("M12-malformed.eml");
        var result = MailHtml.Render(body.Html, new MailRenderOptions());
        Assert.True(result.Failed);
        Assert.NotNull(result.FailureReason);
        Assert.DoesNotContain("only script content", result.FailureReason);
        Assert.Contains("plain alternative is readable", body.Text);
    }

    [Fact]
    public void Data_images_are_limited_to_raster_types_and_size()
    {
        Assert.True(MailHtml.IsAllowedDataImage("data:image/png;base64,AAAA", 100));
        Assert.True(MailHtml.IsAllowedDataImage("data:image/jpeg;base64,AAAA", 100));
        Assert.False(MailHtml.IsAllowedDataImage("data:image/svg+xml;base64,AAAA", 100));
        Assert.False(MailHtml.IsAllowedDataImage("data:text/html,<b>", 100));
        Assert.False(MailHtml.IsAllowedDataImage("data:image/png;base64," + new string('A', 200), 100));
    }

    [Fact]
    public void Risky_attachments_are_recognized()
    {
        Assert.True(ReaderPolicy.IsRisky("invoice.pdf.exe"));
        Assert.True(ReaderPolicy.IsRisky("setup.MSI"));
        Assert.True(ReaderPolicy.IsRisky("macro.docm"));
        Assert.False(ReaderPolicy.IsRisky("report.pdf"));
        Assert.False(ReaderPolicy.IsRisky("photo.png"));
    }
}
