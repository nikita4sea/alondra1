using System.Net;
using System.Text.RegularExpressions;

namespace Alondra1.Core.Html;

public static partial class HtmlMailSanitizer
{
    public static string Sanitize(string? html, bool allowRemoteImages)
    {
        if (string.IsNullOrEmpty(html))
            return "";

        var output = CommentPattern().Replace(html, "");
        output = BlockPattern().Replace(output, "");
        output = EmptyBlockPattern().Replace(output, "");
        output = EventPattern().Replace(output, "");
        output = DangerousUrlPattern().Replace(output, "$1=\"#\"");
        if (!allowRemoteImages)
        {
            output = RemoteImagePattern().Replace(output, "src=\"\"");
            output = ProtocolRelativePattern().Replace(output, "src=\"\"");
        }

        output = CssExpressionPattern().Replace(output, "");
        return output;
    }

    public static string ToPlainText(string? html)
    {
        if (string.IsNullOrEmpty(html))
            return "";
        var text = BreakPattern().Replace(html, "\n");
        text = ParagraphPattern().Replace(text, "\n");
        text = TagPattern().Replace(text, "");
        return WebUtility.HtmlDecode(text).Trim();
    }

    public static string IsolatedDocument(string sanitizedBody, bool allowRemoteImages)
    {
        var img = allowRemoteImages ? "" : "img-src data: cid:;";
        return "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; " + img + "\"><style>body{font:13px/1.4 'Segoe UI',sans-serif;margin:0;color:#20252d;} a{color:#1556a3;}</style></head><body>" + sanitizedBody + "</body></html>";
    }

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex CommentPattern();

    [GeneratedRegex(@"<\s*(script|style|iframe|object|embed|link|meta|form|svg|base)\b[^>]*>.*?<\s*/\s*\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BlockPattern();

    [GeneratedRegex(@"<\s*(script|style|iframe|object|embed|link|meta|form|svg|base)\b[^>]*\/?\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex EmptyBlockPattern();

    [GeneratedRegex(@"\s+on[a-z]+\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)", RegexOptions.IgnoreCase)]
    private static partial Regex EventPattern();

    [GeneratedRegex(@"(href|src|xlink:href)\s*=\s*['""]?\s*(javascript|vbscript|data:text/html)[^'""\s>]*", RegexOptions.IgnoreCase)]
    private static partial Regex DangerousUrlPattern();

    [GeneratedRegex(@"src\s*=\s*(['""])\s*https?:[^'""]*\1", RegexOptions.IgnoreCase)]
    private static partial Regex RemoteImagePattern();

    [GeneratedRegex(@"src\s*=\s*(['""])\s*//[^'""]*\1", RegexOptions.IgnoreCase)]
    private static partial Regex ProtocolRelativePattern();

    [GeneratedRegex(@"expression\s*\([^)]*\)", RegexOptions.IgnoreCase)]
    private static partial Regex CssExpressionPattern();

    [GeneratedRegex(@"<\s*br\s*/?\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakPattern();

    [GeneratedRegex(@"<\s*/\s*p\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex ParagraphPattern();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex TagPattern();
}
