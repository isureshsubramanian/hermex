using System.Globalization;

namespace Hermex.Mime;

/// <summary>A decoded attachment or inline resource extracted from a message.</summary>
public sealed class MimeAttachment
{
    public MimeAttachment(string fileName, string contentType, string? contentId, bool isInline, byte[] content)
    {
        FileName = fileName;
        ContentType = contentType;
        ContentId = contentId;
        IsInline = isInline;
        Content = content;
    }

    /// <summary>The attachment file name (synthesised when the message did not supply one).</summary>
    public string FileName { get; }

    /// <summary>The media type, e.g. <c>application/pdf</c>.</summary>
    public string ContentType { get; }

    /// <summary>The <c>Content-ID</c> used by <c>cid:</c> references, if any.</summary>
    public string? ContentId { get; }

    /// <summary>True for inline resources (e.g. images embedded in the HTML body).</summary>
    public bool IsInline { get; }

    /// <summary>The decoded attachment bytes.</summary>
    public byte[] Content { get; }

    /// <summary>The attachment size in bytes.</summary>
    public int Size => Content.Length;
}

/// <summary>
/// A fully parsed email message: a tree of <see cref="MimeEntity"/> parts plus convenient
/// access to the common fields (subject, addresses, bodies and attachments).
/// </summary>
public sealed class MimeMessage
{
    private static readonly IReadOnlyList<MimeAddress> NoAddresses = Array.Empty<MimeAddress>();

    internal MimeMessage(MimeEntity root, int rawSize, IReadOnlyList<string> warnings)
    {
        Root = root;
        RawSize = rawSize;
        Warnings = warnings;

        Subject = DecodeHeader(Headers.Get("Subject"));
        From = MimeAddress.ParseSingle(Headers.Get("From"));
        Sender = MimeAddress.ParseSingle(Headers.Get("Sender"));
        To = MimeAddress.ParseList(Headers.Get("To"));
        Cc = MimeAddress.ParseList(Headers.Get("Cc"));
        Bcc = MimeAddress.ParseList(Headers.Get("Bcc"));
        ReplyTo = MimeAddress.ParseList(Headers.Get("Reply-To"));
        MessageId = NormalizeMessageId(Headers.Get("Message-ID"));
        Date = ParseDate(Headers.Get("Date"));

        TextBody = FindBody("plain");
        HtmlBody = FindBody("html");
        Attachments = CollectAttachments();
    }

    /// <summary>The root MIME entity (the whole message).</summary>
    public MimeEntity Root { get; }

    /// <summary>Size of the original raw message in bytes.</summary>
    public int RawSize { get; }

    /// <summary>Non-fatal issues encountered while parsing (malformed structure, etc.).</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>The root entity's header fields.</summary>
    public MimeHeaderCollection Headers => Root.Headers;

    /// <summary>The decoded <c>Subject</c>.</summary>
    public string? Subject { get; }

    /// <summary>The <c>From</c> address.</summary>
    public MimeAddress? From { get; }

    /// <summary>The <c>Sender</c> address, if distinct from <see cref="From"/>.</summary>
    public MimeAddress? Sender { get; }

    /// <summary>The <c>To</c> recipients.</summary>
    public IReadOnlyList<MimeAddress> To { get; }

    /// <summary>The <c>Cc</c> recipients.</summary>
    public IReadOnlyList<MimeAddress> Cc { get; }

    /// <summary>The <c>Bcc</c> recipients.</summary>
    public IReadOnlyList<MimeAddress> Bcc { get; }

    /// <summary>The <c>Reply-To</c> addresses.</summary>
    public IReadOnlyList<MimeAddress> ReplyTo { get; }

    /// <summary>The <c>Message-ID</c> with angle brackets stripped.</summary>
    public string? MessageId { get; }

    /// <summary>The parsed <c>Date</c> header.</summary>
    public DateTimeOffset? Date { get; }

    /// <summary>The plain-text body, if the message has one.</summary>
    public string? TextBody { get; }

    /// <summary>The HTML body, if the message has one.</summary>
    public string? HtmlBody { get; }

    /// <summary>All attachments and inline resources.</summary>
    public IReadOnlyList<MimeAttachment> Attachments { get; }

    /// <summary>True when the message carries an HTML body.</summary>
    public bool HasHtmlBody => !string.IsNullOrEmpty(HtmlBody);

    /// <summary>True when the message carries a plain-text body.</summary>
    public bool HasTextBody => !string.IsNullOrEmpty(TextBody);

    private string? FindBody(string subType)
    {
        foreach (var entity in Root.Descendants())
        {
            if (entity.IsText &&
                !entity.IsAttachment &&
                string.Equals(entity.ContentType.SubType, subType, StringComparison.OrdinalIgnoreCase))
            {
                return entity.GetTextContent();
            }
        }
        return null;
    }

    private IReadOnlyList<MimeAttachment> CollectAttachments()
    {
        var list = new List<MimeAttachment>();
        var index = 0;

        foreach (var entity in Root.Descendants())
        {
            if (!entity.IsLeaf)
                continue;
            if (!entity.IsAttachment && !entity.IsInline)
                continue;

            index++;
            var fileName = entity.FileName ?? SynthesiseFileName(entity, index);
            list.Add(new MimeAttachment(
                fileName,
                entity.MediaType,
                entity.ContentId,
                entity.IsInline,
                entity.Content));
        }

        return list;
    }

    private static string SynthesiseFileName(MimeEntity entity, int index)
    {
        var extension = entity.ContentType.SubType switch
        {
            "plain" => ".txt",
            "html" => ".html",
            "jpeg" or "jpg" => ".jpg",
            "png" => ".png",
            "gif" => ".gif",
            "pdf" => ".pdf",
            "zip" => ".zip",
            "json" => ".json",
            "csv" => ".csv",
            "calendar" => ".ics",
            "rfc822" => ".eml",
            _ => ".bin",
        };
        var prefix = entity.IsInline ? "inline" : "attachment";
        return $"{prefix}-{index}{extension}";
    }

    private static string? DecodeHeader(string? value) =>
        string.IsNullOrEmpty(value) ? null : Rfc2047Decoder.Decode(value);

    private static string? NormalizeMessageId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return value.Trim().Trim('<', '>').Trim();
    }

    private static DateTimeOffset? ParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var value = raw.Trim();

        // Drop trailing comments such as "(UTC)".
        var paren = value.IndexOf('(');
        if (paren > 0)
            value = value[..paren].Trim();

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out var parsed))
        {
            return parsed;
        }

        string[] formats =
        {
            "ddd, d MMM yyyy H:mm:ss zzz",
            "ddd, d MMM yyyy H:mm:ss zz",
            "ddd, d MMM yyyy H:mm:ss",
            "d MMM yyyy H:mm:ss zzz",
            "d MMM yyyy H:mm:ss",
            "ddd, d MMM yyyy H:mm zzz",
        };

        if (DateTimeOffset.TryParseExact(value, formats, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out parsed))
        {
            return parsed;
        }

        return null;
    }
}
