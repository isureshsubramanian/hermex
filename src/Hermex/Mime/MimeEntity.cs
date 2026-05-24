namespace Hermex.Mime;

/// <summary>
/// A single MIME entity: either a leaf part (with content) or a multipart container
/// (with <see cref="Children"/>). Produced by <see cref="MimeParser"/>.
/// </summary>
public sealed class MimeEntity
{
    internal MimeEntity(MimeHeaderCollection headers, int depth)
    {
        Headers = headers;
        Depth = depth;
        ContentType = MimeContentType.Parse(headers.Get("Content-Type"));
        ContentDisposition = MimeContentDisposition.Parse(headers.Get("Content-Disposition"));
        ContentTransferEncoding = (headers.Get("Content-Transfer-Encoding") ?? "7bit")
            .Trim().Trim('"').ToLowerInvariant();
        ContentId = NormalizeContentId(headers.Get("Content-ID"));
    }

    /// <summary>All header fields of this entity.</summary>
    public MimeHeaderCollection Headers { get; }

    /// <summary>Nesting depth (0 for the message root).</summary>
    public int Depth { get; }

    /// <summary>The parsed content type (defaults to <c>text/plain</c> when absent).</summary>
    public MimeContentType ContentType { get; }

    /// <summary>The parsed content disposition, if any.</summary>
    public MimeContentDisposition? ContentDisposition { get; }

    /// <summary>The transfer encoding token (<c>7bit</c>, <c>base64</c>, <c>quoted-printable</c>, ...).</summary>
    public string ContentTransferEncoding { get; }

    /// <summary>The <c>Content-ID</c> with surrounding angle brackets stripped, if present.</summary>
    public string? ContentId { get; }

    /// <summary>Child entities of a multipart container (empty for leaf parts).</summary>
    public IReadOnlyList<MimeEntity> Children { get; internal set; } = Array.Empty<MimeEntity>();

    /// <summary>Raw content bytes exactly as transmitted, before transfer-decoding (leaf parts only).</summary>
    public byte[] RawContent { get; internal set; } = Array.Empty<byte>();

    /// <summary>Content bytes after transfer-decoding (base64 / quoted-printable resolved).</summary>
    public byte[] Content { get; internal set; } = Array.Empty<byte>();

    /// <summary>True for a <c>multipart/*</c> container.</summary>
    public bool IsMultipart => ContentType.IsMultipart && Children.Count > 0;

    /// <summary>True for a leaf part that carries content.</summary>
    public bool IsLeaf => !IsMultipart;

    /// <summary>True for a leaf <c>text/*</c> part.</summary>
    public bool IsText => ContentType.IsText && IsLeaf;

    /// <summary>The media type string, e.g. <c>image/png</c>.</summary>
    public string MediaType => ContentType.MediaType;

    /// <summary>The file name from Content-Disposition or the legacy Content-Type <c>name</c>, RFC 2047 decoded.</summary>
    public string? FileName
    {
        get
        {
            var name = ContentDisposition?.FileName ?? ContentType.Name;
            return string.IsNullOrWhiteSpace(name) ? null : Rfc2047Decoder.Decode(name).Trim();
        }
    }

    /// <summary>True when this part should be treated as a downloadable attachment.</summary>
    public bool IsAttachment
    {
        get
        {
            if (IsMultipart)
                return false;
            if (ContentDisposition?.IsAttachment == true)
                return true;
            if (ContentDisposition?.IsInline == true)
                return false;
            if (FileName is not null)
                return true;
            // A non-text leaf with no disposition and no Content-ID is an attachment.
            return !IsText && ContentId is null;
        }
    }

    /// <summary>True when this part is an inline resource (e.g. a <c>cid:</c> image).</summary>
    public bool IsInline
    {
        get
        {
            if (IsMultipart)
                return false;
            if (ContentDisposition?.IsInline == true)
                return true;
            return ContentId is not null && !IsAttachment;
        }
    }

    /// <summary>Decodes <see cref="Content"/> to text using the declared charset (leaf text parts).</summary>
    public string GetTextContent()
    {
        if (Content.Length == 0)
            return string.Empty;

        var text = MimeEncoding.GetEncoding(ContentType.Charset).GetString(Content);

        // Strip a leading byte-order mark if the encoding produced one.
        if (text.Length > 0 && text[0] == '﻿')
            text = text[1..];

        return text;
    }

    /// <summary>Depth-first enumeration of this entity and all descendants.</summary>
    public IEnumerable<MimeEntity> Descendants()
    {
        yield return this;
        foreach (var child in Children)
        {
            foreach (var nested in child.Descendants())
                yield return nested;
        }
    }

    private static string? NormalizeContentId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return value.Trim().Trim('<', '>').Trim();
    }
}
