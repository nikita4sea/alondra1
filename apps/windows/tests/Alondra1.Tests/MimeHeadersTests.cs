using System.Text;
using Alondra1.Mail;
using MimeKit;

namespace Alondra1.Tests;

/// <summary>M07: non-ASCII headers read over POP3 decode correctly, whatever charset the sender used.</summary>
public class MimeHeadersTests
{
    static MimeHeadersTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static HeaderList Headers(Encoding encoding, string subject, string charset)
    {
        var text = $"From: Ivan <ivan@example.com>\r\nSubject: {subject}\r\nContent-Type: text/plain; charset={charset}\r\n\r\nbody\r\n";
        // POP3 fetches the raw header block (Content-Type included), like HeaderList.Load.
        using var stream = new MemoryStream(encoding.GetBytes(text));
        return HeaderList.Load(stream);
    }

    [Theory]
    [InlineData("windows-1251")]
    [InlineData("koi8-r")]
    [InlineData("utf-8")]
    public void Raw_eight_bit_subject_uses_the_message_charset(string charset)
    {
        const string subject = "NDA, соглашение о неразглашении";
        var headers = Headers(Encoding.GetEncoding(charset), subject, charset);
        Assert.Equal(subject, MimeHeaders.Text(headers, HeaderId.Subject));
    }

    [Theory]
    // Real headers seen on the test mailbox: encoded words in legacy charsets.
    [InlineData("=?windows-1251?B?Rnc6IE5EQSwg4evg4+7t4OTl5u3u8fL8?=", "Fw: NDA, благонадежность")]
    [InlineData("=?windows-1251?B?Rnc6IPHg6fI=?=", "Fw: сайт")]
    [InlineData("=?iso-8859-2?Q?Fw:_Dokumenty_dotycz=B1ce_wynajmu_samochodu?=", "Fw: Dokumenty dotyczące wynajmu samochodu")]
    public void Encoded_words_in_legacy_charsets_decode(string raw, string expected)
    {
        Alondra1.Mail.MimeHeaders.EnsureCharsets();
        Assert.Equal(expected, MimeHeaders.Text(Headers(Encoding.ASCII, raw, "us-ascii"), HeaderId.Subject));
    }

    [Fact]
    public void Encoded_words_and_plain_ascii_are_unchanged()
    {
        var headers = Headers(Encoding.ASCII, "=?UTF-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes("Привет 👋")) + "?=", "us-ascii");
        Assert.Equal("Привет 👋", MimeHeaders.Text(headers, HeaderId.Subject));
        Assert.Equal("Ivan <ivan@example.com>", MimeHeaders.Text(headers, HeaderId.From));
        Assert.Equal("", MimeHeaders.Text(headers, HeaderId.Cc));
    }
}
