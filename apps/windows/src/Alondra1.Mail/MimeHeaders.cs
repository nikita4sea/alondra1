using System.Text;
using System.Text.RegularExpressions;
using MimeKit;
using MimeKit.Utils;

namespace Alondra1.Mail;

/// <summary>
/// Decodes header text for messages read as raw headers (POP3). ASCII headers, including RFC 2047 encoded words, use
/// MimeKit's decoding. Raw 8-bit headers declare no charset, so they are read as UTF-8 when that is valid, otherwise
/// in the message's own charset (Content-Type), otherwise Windows-1252. Without this, Cyrillic subjects in
/// Windows-1251 or KOI8-R showed as "áëàãîïàâ…".
/// </summary>
public static class MimeHeaders
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    static MimeHeaders() => EnsureCharsets();

    /// <summary>Registers the Windows code pages with .NET. Call before any MimeKit parsing (safe to call repeatedly).</summary>
    public static void EnsureCharsets() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static string Text(HeaderList headers, HeaderId id)
    {
        var header = headers.FirstOrDefault(item => item.Id == id);
        if (header is null)
            return "";
        var raw = header.RawValue;
        if (raw.All(value => value < 0x80))
            return header.Value.Trim();
        // Decode the 8-bit text in its charset; encoded words ("=?utf-8?B?…?=") mixed into it are decoded afterwards.
        var text = EncodingFor(raw, headers).GetString(raw);
        if (text.Contains("=?", StringComparison.Ordinal))
            text = Rfc2047.DecodeText(Encoding.UTF8.GetBytes(text));
        return Regex.Replace(text, @"\r?\n[ \t]+", " ").Trim();
    }

    public static Encoding EncodingFor(byte[] raw, HeaderList headers)
    {
        try
        {
            StrictUtf8.GetString(raw);
            return Encoding.UTF8;
        }
        catch (DecoderFallbackException)
        {
        }

        var contentType = headers[HeaderId.ContentType];
        if (!string.IsNullOrEmpty(contentType) && ContentType.TryParse(contentType, out var parsed)
            && parsed.Charset is { Length: > 0 } charset && !charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase)
            && Lookup(charset) is { } declared)
            return declared;
        return Lookup("windows-1252") ?? Encoding.Latin1;
    }

    private static Encoding? Lookup(string charset)
    {
        try
        {
            return Encoding.GetEncoding(charset);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
