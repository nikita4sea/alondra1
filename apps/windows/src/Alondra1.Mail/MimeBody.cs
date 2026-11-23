using System.Text.RegularExpressions;
using Alondra1.Core.Html;
using Alondra1.Core.Mail;
using MimeKit;

namespace Alondra1.Mail;

/// <summary>
/// Turns a full MIME message into what the reader stores: the preferred HTML (MimeKit picks it through
/// multipart/alternative, multipart/related, and nested multiparts, decoding charsets, quoted-printable, and Base64),
/// the plain text, and the attachments. Inline images referenced as cid: are embedded into the HTML as data: URIs, so
/// they show without a network request and offline; they are not listed as attachments.
/// </summary>
public static partial class MimeBody
{
    /// <summary>Largest inline image embedded, and the total for one message (decoded bytes).</summary>
    public const long MaxInlineImage = 5 * 1024 * 1024;
    public const long MaxInlineTotal = 20 * 1024 * 1024;

    private static readonly string[] InlineTypes = ["png", "jpeg", "jpg", "gif", "webp", "bmp"];

    public static StoredBody Extract(MimeMessage message, string accountId = "", string folderId = "", long uid = 0)
    {
        var html = message.HtmlBody;
        var text = message.TextBody;
        var used = new HashSet<MimeEntity>();
        if (!string.IsNullOrWhiteSpace(html))
            html = EmbedInlineImages(message, html, used);
        if (string.IsNullOrWhiteSpace(text) && !string.IsNullOrWhiteSpace(html))
            text = HtmlMailSanitizer.ToPlainText(html);

        var body = new StoredBody { Text = text ?? "", Html = string.IsNullOrWhiteSpace(html) ? null : html };
        var index = 0;
        foreach (var (entity, name) in Downloadable(message, used))
        {
            body.Attachments.Add(new StoredAttachment
            {
                Id = $"{accountId}|{folderId}|{uid}|{index}",
                AccountId = accountId,
                FolderId = folderId,
                Uid = uid,
                FileName = name,
                ContentType = entity.ContentType.MimeType.ToLowerInvariant(),
                Size = DecodedSize(entity),
                ContentId = entity.ContentId,
            });
            index++;
        }

        return body;
    }

    /// <summary>Finds an attachment by the name <see cref="Extract"/> gave it.</summary>
    public static MimeEntity? FindAttachment(MimeMessage message, string fileName) =>
        Downloadable(message, Inline(message)).FirstOrDefault(item => string.Equals(item.Name, fileName, StringComparison.OrdinalIgnoreCase)).Entity;

    /// <summary>Writes an attachment's decoded content.</summary>
    public static async Task WriteAsync(MimeEntity entity, Stream destination, CancellationToken cancellationToken)
    {
        switch (entity)
        {
            case MimePart { Content: not null } part:
                await part.Content.DecodeToAsync(destination, cancellationToken).ConfigureAwait(false);
                break;
            case MessagePart { Message: not null } attached:
                await attached.Message.WriteToAsync(destination, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private static HashSet<MimeEntity> Inline(MimeMessage message)
    {
        var used = new HashSet<MimeEntity>();
        if (message.HtmlBody is { Length: > 0 } html)
            EmbedInlineImages(message, html, used);
        return used;
    }

    private static string EmbedInlineImages(MimeMessage message, string html, HashSet<MimeEntity> used)
    {
        var byId = new Dictionary<string, MimePart>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in message.BodyParts.OfType<MimePart>())
        {
            if (!string.IsNullOrEmpty(part.ContentId))
                byId.TryAdd(part.ContentId.Trim('<', '>'), part);
            // Some senders reference images by Content-Location instead of Content-ID.
            if (part.ContentLocation is { } location && location.ToString() is { Length: > 0 } text)
                byId.TryAdd(text, part);
        }

        if (byId.Count == 0)
            return html;
        long total = 0;
        var cache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        return CidPattern().Replace(html, match =>
        {
            var id = Uri.UnescapeDataString(match.Groups["id"].Value).Trim('<', '>');
            if (!cache.TryGetValue(id, out var data))
            {
                data = null;
                if (byId.TryGetValue(id, out var part) && part.ContentType.MediaType.Equals("image", StringComparison.OrdinalIgnoreCase)
                    && InlineTypes.Contains(part.ContentType.MediaSubtype.ToLowerInvariant()) && part.Content is not null)
                {
                    using var memory = new MemoryStream();
                    part.Content.DecodeTo(memory);
                    if (memory.Length <= MaxInlineImage && total + memory.Length <= MaxInlineTotal)
                    {
                        total += memory.Length;
                        data = $"data:{part.ContentType.MimeType.ToLowerInvariant()};base64,{Convert.ToBase64String(memory.GetBuffer(), 0, (int)memory.Length)}";
                        used.Add(part);
                    }
                }

                cache[id] = data;
            }

            return data ?? match.Value;
        });
    }

    /// <summary>Downloadable parts with display names: explicit attachments, attached messages, and non-text parts not shown inline.</summary>
    private static IEnumerable<(MimeEntity Entity, string Name)> Downloadable(MimeMessage message, HashSet<MimeEntity> inline)
    {
        var textBody = message.TextBody is null ? null : message.BodyParts.OfType<TextPart>().FirstOrDefault(part => part.IsPlain && !part.IsAttachment);
        var htmlBody = message.HtmlBody is null ? null : message.BodyParts.OfType<TextPart>().FirstOrDefault(part => part.IsHtml && !part.IsAttachment);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var number = 0;
        foreach (var entity in message.BodyParts)
        {
            if (inline.Contains(entity) || ReferenceEquals(entity, textBody) || ReferenceEquals(entity, htmlBody))
                continue;
            var isAttachment = entity.IsAttachment;
            // Unreferenced text parts without a file name are alternative bodies, not files.
            if (!isAttachment && entity is TextPart && string.IsNullOrEmpty((entity as MimePart)?.FileName))
                continue;
            number++;
            var name = entity switch
            {
                MimePart part when !string.IsNullOrWhiteSpace(part.FileName) => Path.GetFileName(part.FileName),
                MessagePart attached => (attached.Message?.Subject is { Length: > 0 } subject ? Safe(subject) : "message") + ".eml",
                _ => $"attachment-{number}{Extension(entity.ContentType)}",
            };
            var unique = name;
            for (var copy = 2; !names.Add(unique); copy++)
                unique = $"{Path.GetFileNameWithoutExtension(name)} ({copy}){Path.GetExtension(name)}";
            yield return (entity, unique);
        }
    }

    private static long DecodedSize(MimeEntity entity)
    {
        using var counter = new CountingStream();
        try
        {
            // Synchronous on purpose: this runs on the UI thread too, where blocking on an async write deadlocks.
            switch (entity)
            {
                case MimePart { Content: not null } part:
                    part.Content.DecodeTo(counter);
                    break;
                case MessagePart { Message: not null } attached:
                    attached.Message.WriteTo(counter);
                    break;
            }
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or IOException)
        {
        }

        return counter.Length;
    }

    private static string Extension(ContentType type) => MimeTypes.TryGetExtension(type.MimeType, out var extension) ? extension : ".bin";

    private static string Safe(string text)
    {
        var cleaned = new string(text.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch).ToArray()).Trim();
        return cleaned.Length > 80 ? cleaned[..80] : cleaned;
    }

    [GeneratedRegex(@"cid:(?<id>[^""'\s)>]+)", RegexOptions.IgnoreCase)]
    private static partial Regex CidPattern();

    private sealed class CountingStream : Stream
    {
        private long _length;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _length += count;
        public override void Write(ReadOnlySpan<byte> buffer) => _length += buffer.Length;
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            _length += count;
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _length += buffer.Length;
            return ValueTask.CompletedTask;
        }
    }
}
