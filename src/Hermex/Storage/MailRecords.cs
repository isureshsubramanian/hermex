namespace Hermex.Storage;

/// <summary>A message about to be persisted: list/summary metadata plus the raw payload.</summary>
public sealed class MessageRecord
{
    /// <summary>Database identity, assigned after insertion.</summary>
    public long Id { get; set; }

    /// <summary>The SMTP session that delivered the message.</summary>
    public string SessionId { get; init; } = string.Empty;

    /// <summary>When the message was received (UTC).</summary>
    public DateTimeOffset ReceivedAtUtc { get; init; }

    /// <summary>The SMTP envelope sender.</summary>
    public string EnvelopeFrom { get; init; } = string.Empty;

    /// <summary>The SMTP envelope recipients, comma-joined.</summary>
    public string EnvelopeTo { get; init; } = string.Empty;

    /// <summary>The <c>From</c> header email address.</summary>
    public string FromAddress { get; init; } = string.Empty;

    /// <summary>The <c>From</c> header display name (falls back to the address).</summary>
    public string FromDisplay { get; init; } = string.Empty;

    /// <summary>The <c>To</c> header recipients, formatted for display.</summary>
    public string ToDisplay { get; init; } = string.Empty;

    /// <summary>The decoded subject.</summary>
    public string Subject { get; init; } = string.Empty;

    /// <summary>Whether the message has an HTML body.</summary>
    public bool HasHtml { get; init; }

    /// <summary>Whether the message has a plain-text body.</summary>
    public bool HasText { get; init; }

    /// <summary>Size of the raw message in bytes.</summary>
    public int RawSize { get; init; }

    /// <summary>Number of attachments and inline resources.</summary>
    public int AttachmentCount { get; init; }

    /// <summary>The remote endpoint that delivered the message.</summary>
    public string RemoteEndPoint { get; init; } = string.Empty;

    /// <summary>The envelope recipients — each becomes (or maps to) a mailbox.</summary>
    public IReadOnlyList<string> Recipients { get; init; } = Array.Empty<string>();

    /// <summary>Whether the message was delivered over a TLS-secured connection.</summary>
    public bool SecuredWithTls { get; init; }

    /// <summary>The SMTP command/response transcript of the delivering session, if captured.</summary>
    public string? Transcript { get; init; }

    /// <summary>The raw RFC 5322 payload.</summary>
    public byte[] RawData { get; init; } = Array.Empty<byte>();
}

/// <summary>A lightweight inbox-list row.</summary>
public sealed class MessageSummary
{
    public long Id { get; init; }
    public DateTimeOffset ReceivedAtUtc { get; init; }
    public string From { get; init; } = string.Empty;
    public string FromAddress { get; init; } = string.Empty;
    public string To { get; init; } = string.Empty;
    public string Subject { get; init; } = string.Empty;
    public bool HasHtml { get; init; }
    public bool HasAttachments { get; init; }
    public int AttachmentCount { get; init; }
    public int RawSize { get; init; }
    public bool IsRead { get; init; }
}

/// <summary>A single header field of a stored message.</summary>
public sealed record HeaderField(string Name, string Value);

/// <summary>Attachment metadata (without the binary content).</summary>
public sealed class AttachmentInfo
{
    /// <summary>Zero-based position of the attachment within the message.</summary>
    public int Index { get; init; }
    public string FileName { get; init; } = string.Empty;
    public string ContentType { get; init; } = string.Empty;
    public string? ContentId { get; init; }
    public bool IsInline { get; init; }
    public int Size { get; init; }
}

/// <summary>Attachment metadata together with its decoded bytes.</summary>
public sealed class AttachmentContent
{
    public AttachmentInfo Info { get; init; } = new();
    public byte[] Content { get; init; } = Array.Empty<byte>();
}

/// <summary>The full detail view of a stored message.</summary>
public sealed class MessageDetail
{
    public long Id { get; init; }
    public string SessionId { get; init; } = string.Empty;
    public DateTimeOffset ReceivedAtUtc { get; init; }
    public string EnvelopeFrom { get; init; } = string.Empty;
    public string EnvelopeTo { get; init; } = string.Empty;
    public string Subject { get; init; } = string.Empty;
    public string FromDisplay { get; init; } = string.Empty;
    public string FromAddress { get; init; } = string.Empty;
    public string ToDisplay { get; init; } = string.Empty;
    public string CcDisplay { get; init; } = string.Empty;
    public string? TextBody { get; init; }
    public string? HtmlBody { get; init; }
    public bool IsRead { get; init; }
    public int RawSize { get; init; }
    public string RemoteEndPoint { get; init; } = string.Empty;
    public bool SecuredWithTls { get; init; }
    public string? Transcript { get; init; }
    public string? MessageIdHeader { get; init; }
    public DateTimeOffset? DateHeader { get; init; }
    public IReadOnlyList<HeaderField> Headers { get; init; } = Array.Empty<HeaderField>();
    public IReadOnlyList<AttachmentInfo> Attachments { get; init; } = Array.Empty<AttachmentInfo>();
    public MimePartNode? Structure { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

/// <summary>A node in the message's MIME structure tree.</summary>
public sealed class MimePartNode
{
    /// <summary>The media type, e.g. <c>multipart/alternative</c> or <c>image/png</c>.</summary>
    public string MediaType { get; init; } = string.Empty;

    /// <summary>The role this part plays: <c>container</c>, <c>body</c>, <c>attachment</c> or <c>inline</c>.</summary>
    public string Role { get; init; } = "body";

    /// <summary>The transfer encoding (<c>7bit</c>, <c>base64</c>, ...).</summary>
    public string Encoding { get; init; } = string.Empty;

    /// <summary>The charset, for text parts.</summary>
    public string? Charset { get; init; }

    /// <summary>The file name, for attachments and inline resources.</summary>
    public string? FileName { get; init; }

    /// <summary>The decoded content size in bytes (leaf parts only).</summary>
    public int Size { get; init; }

    /// <summary>Child parts of a multipart container.</summary>
    public IReadOnlyList<MimePartNode> Children { get; init; } = Array.Empty<MimePartNode>();
}

/// <summary>Aggregate statistics over the mail store.</summary>
public sealed class MailStats
{
    public int TotalMessages { get; init; }
    public int UnreadMessages { get; init; }
    public long TotalSizeBytes { get; init; }
    public DateTimeOffset? LastReceivedUtc { get; init; }
}

/// <summary>Inbox query parameters.</summary>
public sealed class MessageQuery
{
    /// <summary>1-based page number.</summary>
    public int Page { get; set; } = 1;

    /// <summary>Page size.</summary>
    public int PageSize { get; set; } = 50;

    /// <summary>Optional case-insensitive search over subject and addresses.</summary>
    public string? Search { get; set; }

    /// <summary>When true, only unread messages are returned.</summary>
    public bool UnreadOnly { get; set; }

    /// <summary>When set, only messages routed to this mailbox are returned.</summary>
    public long? MailboxId { get; set; }
}

/// <summary>A mailbox — created automatically for each recipient address.</summary>
public sealed class Mailbox
{
    public long Id { get; init; }
    public string Address { get; init; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>IMAP UIDVALIDITY — constant for the life of the mailbox.</summary>
    public long UidValidity { get; init; }

    /// <summary>IMAP UIDNEXT — an upper bound on the next UID to be assigned.</summary>
    public long UidNext { get; init; }

    /// <summary>Number of messages currently in the mailbox.</summary>
    public int MessageCount { get; init; }

    /// <summary>Number of unread messages in the mailbox.</summary>
    public int UnreadCount { get; init; }
}

/// <summary>One message as it appears inside a mailbox (used by the IMAP server).</summary>
public sealed class MailboxMessageInfo
{
    /// <summary>1-based position of the message within the mailbox.</summary>
    public int SequenceNumber { get; init; }

    /// <summary>The IMAP UID (equal to the global message id, ascending within a mailbox).</summary>
    public long Uid { get; init; }

    /// <summary>Whether the message has been read (the IMAP <c>\Seen</c> flag).</summary>
    public bool IsSeen { get; init; }

    /// <summary>Whether the message is flagged for deletion in this mailbox (<c>\Deleted</c>).</summary>
    public bool IsDeleted { get; init; }

    /// <summary>Raw message size in bytes.</summary>
    public int Size { get; init; }

    /// <summary>When the message was received.</summary>
    public DateTimeOffset ReceivedAtUtc { get; init; }
}

/// <summary>A page of results plus paging metadata.</summary>
public sealed class PagedResult<T>
{
    public IReadOnlyList<T> Items { get; init; } = Array.Empty<T>();
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalCount { get; init; }
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling(TotalCount / (double)PageSize) : 0;
}
