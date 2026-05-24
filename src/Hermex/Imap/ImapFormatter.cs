using System.Globalization;
using System.Text;
using Hermex.Mime;
using Hermex.Storage;

namespace Hermex.Imap;

/// <summary>Formats IMAP response fragments: strings, ENVELOPE, BODYSTRUCTURE, dates and sequence sets.</summary>
internal static class ImapFormatter
{
    /// <summary>Renders a value as an IMAP quoted string, or <c>NIL</c> when null.</summary>
    public static string QuoteOrNil(string? value)
    {
        if (value is null)
            return "NIL";

        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var c in value)
        {
            if (c is '\\' or '"')
                sb.Append('\\').Append(c);
            else if (c is '\r' or '\n')
                sb.Append(' ');
            else
                sb.Append(c);
        }
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>Formats an IMAP INTERNALDATE quoted string (<c>"dd-MMM-yyyy HH:mm:ss +0000"</c>).</summary>
    public static string FormatInternalDate(DateTimeOffset value)
    {
        var offset = value.Offset;
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        var zone = $"{sign}{Math.Abs(offset.Hours):D2}{Math.Abs(offset.Minutes):D2}";
        return "\"" + value.ToString("dd-MMM-yyyy HH:mm:ss ", CultureInfo.InvariantCulture) + zone + "\"";
    }

    /// <summary>Builds the IMAP ENVELOPE structure for a message.</summary>
    public static string FormatEnvelope(MimeMessage message)
    {
        var date = QuoteOrNil(message.Headers.Get("Date"));
        var subject = QuoteOrNil(message.Subject);
        var from = FormatAddressList(message.From is null
            ? Array.Empty<MimeAddress>()
            : new[] { message.From });
        var sender = from;
        var replyTo = message.ReplyTo.Count > 0 ? FormatAddressList(message.ReplyTo) : from;
        var to = FormatAddressList(message.To);
        var cc = FormatAddressList(message.Cc);
        var bcc = FormatAddressList(message.Bcc);
        var inReplyTo = QuoteOrNil(message.Headers.Get("In-Reply-To"));
        var messageId = QuoteOrNil(message.MessageId is null ? null : $"<{message.MessageId}>");

        return $"({date} {subject} {from} {sender} {replyTo} {to} {cc} {bcc} {inReplyTo} {messageId})";
    }

    /// <summary>Builds the IMAP BODYSTRUCTURE for a MIME entity (recursive).</summary>
    public static string FormatBodyStructure(MimeEntity entity)
    {
        if (entity.IsMultipart)
        {
            var sb = new StringBuilder("(");
            foreach (var child in entity.Children)
                sb.Append(FormatBodyStructure(child));
            sb.Append(' ').Append(QuoteOrNil(entity.ContentType.SubType));
            sb.Append(')');
            return sb.ToString();
        }

        var encoding = string.IsNullOrEmpty(entity.ContentTransferEncoding)
            ? "7BIT"
            : entity.ContentTransferEncoding.ToUpperInvariant();

        var sb2 = new StringBuilder("(");
        sb2.Append(QuoteOrNil(entity.ContentType.Type)).Append(' ')
           .Append(QuoteOrNil(entity.ContentType.SubType)).Append(' ')
           .Append(FormatParameters(entity.ContentType)).Append(' ')
           .Append(QuoteOrNil(entity.ContentId is null ? null : $"<{entity.ContentId}>")).Append(' ')
           .Append("NIL").Append(' ')
           .Append(QuoteOrNil(encoding)).Append(' ')
           .Append(entity.RawContent.Length);

        if (entity.ContentType.IsText)
            sb2.Append(' ').Append(CountLines(entity.Content));

        sb2.Append(')');
        return sb2.ToString();
    }

    /// <summary>Selects messages matching an IMAP sequence set (by sequence number or by UID).</summary>
    public static List<MailboxMessageInfo> SelectMessages(
        string set, IReadOnlyList<MailboxMessageInfo> messages, bool byUid)
    {
        var result = new List<MailboxMessageInfo>();
        if (messages.Count == 0 || string.IsNullOrWhiteSpace(set))
            return result;

        var max = byUid ? messages[^1].Uid : messages.Count;

        foreach (var part in set.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            long lo, hi;
            var colon = part.IndexOf(':');
            if (colon < 0)
            {
                lo = hi = ParseSetValue(part, max);
            }
            else
            {
                lo = ParseSetValue(part[..colon], max);
                hi = ParseSetValue(part[(colon + 1)..], max);
            }
            if (lo > hi)
                (lo, hi) = (hi, lo);

            foreach (var message in messages)
            {
                var key = byUid ? message.Uid : message.SequenceNumber;
                if (key >= lo && key <= hi && !result.Contains(message))
                    result.Add(message);
            }
        }

        result.Sort((a, b) => a.SequenceNumber.CompareTo(b.SequenceNumber));
        return result;
    }

    private static long ParseSetValue(string token, long max)
    {
        token = token.Trim();
        if (token == "*")
            return max;
        return long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    private static string FormatAddressList(IReadOnlyList<MimeAddress> addresses)
    {
        if (addresses.Count == 0)
            return "NIL";

        var sb = new StringBuilder("(");
        foreach (var address in addresses)
        {
            var at = address.Address.IndexOf('@');
            var mailbox = at >= 0 ? address.Address[..at] : address.Address;
            var host = at >= 0 ? address.Address[(at + 1)..] : string.Empty;

            sb.Append('(')
              .Append(QuoteOrNil(address.HasDisplayName ? address.DisplayName : null)).Append(' ')
              .Append("NIL").Append(' ')
              .Append(QuoteOrNil(mailbox.Length > 0 ? mailbox : null)).Append(' ')
              .Append(QuoteOrNil(host.Length > 0 ? host : null))
              .Append(')');
        }
        sb.Append(')');
        return sb.ToString();
    }

    private static string FormatParameters(MimeContentType contentType)
    {
        if (contentType.Parameters.Count == 0)
            return "NIL";

        var sb = new StringBuilder("(");
        var first = true;
        foreach (var pair in contentType.Parameters)
        {
            if (!first)
                sb.Append(' ');
            sb.Append(QuoteOrNil(pair.Key)).Append(' ').Append(QuoteOrNil(pair.Value));
            first = false;
        }
        sb.Append(')');
        return sb.ToString();
    }

    private static int CountLines(byte[] content)
    {
        var lines = 0;
        foreach (var b in content)
        {
            if (b == (byte)'\n')
                lines++;
        }
        return lines;
    }
}
