using System.Text;

namespace Hermex.Mime;

/// <summary>
/// A hand-rolled, allocation-conscious MIME parser. It is deliberately tolerant: malformed
/// structure produces a best-effort result plus a warning rather than an exception, because
/// the mail real applications send is frequently imperfect.
/// </summary>
public static class MimeParser
{
    /// <summary>Maximum multipart nesting depth before parsing stops descending.</summary>
    private const int MaxDepth = 30;

    /// <summary>Parses a complete raw RFC 5322 / MIME message.</summary>
    public static MimeMessage Parse(byte[] raw)
    {
        raw ??= Array.Empty<byte>();
        var warnings = new List<string>();
        var root = ParseEntity(raw, 0, raw.Length, 0, warnings);
        return new MimeMessage(root, raw.Length, warnings);
    }

    /// <summary>Parses a complete raw message from a span.</summary>
    public static MimeMessage Parse(ReadOnlySpan<byte> raw) => Parse(raw.ToArray());

    private static MimeEntity ParseEntity(byte[] data, int start, int end, int depth, List<string> warnings)
    {
        var bodyStart = FindBodyStart(data, start, end, out var headerEnd);
        var headers = ParseHeaders(data, start, headerEnd, warnings);
        var entity = new MimeEntity(headers, depth);

        var bodyLength = Math.Max(0, end - bodyStart);

        // --- multipart container ---
        if (entity.ContentType.IsMultipart && depth < MaxDepth)
        {
            var boundary = entity.ContentType.Boundary;
            if (!string.IsNullOrEmpty(boundary))
            {
                var children = SplitMultipart(data, bodyStart, end, boundary!, depth, warnings);
                if (children.Count > 0)
                {
                    entity.Children = children;
                    return entity;
                }
                warnings.Add($"multipart/{entity.ContentType.SubType}: boundary '{boundary}' declared but no parts were found.");
            }
            else
            {
                warnings.Add($"multipart/{entity.ContentType.SubType}: missing boundary parameter.");
            }

            // Degrade gracefully — expose the body so it is not silently lost.
            entity.Children = new[] { CreateFallbackPart(data, bodyStart, end, depth + 1) };
            return entity;
        }

        // --- embedded message/rfc822 ---
        if (entity.ContentType.IsRfc822 && depth < MaxDepth && bodyLength > 0)
        {
            entity.RawContent = Slice(data, bodyStart, end);
            entity.Content = entity.RawContent;
            entity.Children = new[] { ParseEntity(data, bodyStart, end, depth + 1, warnings) };
            return entity;
        }

        // --- leaf part ---
        var rawContent = Slice(data, bodyStart, end);
        entity.RawContent = rawContent;
        try
        {
            entity.Content = MimeContentDecoder.Decode(rawContent, entity.ContentTransferEncoding);
        }
        catch
        {
            warnings.Add($"Failed to decode a '{entity.ContentTransferEncoding}' part; raw bytes were kept.");
            entity.Content = rawContent;
        }

        return entity;
    }

    // -------------------------------------------------------------- header / body split

    private static int FindBodyStart(byte[] data, int start, int end, out int headerEnd)
    {
        var lineStart = start;
        for (var i = start; i < end; i++)
        {
            if (data[i] != (byte)'\n')
                continue;

            var contentEnd = i;
            if (contentEnd > lineStart && data[contentEnd - 1] == (byte)'\r')
                contentEnd--;

            if (contentEnd == lineStart)
            {
                // An empty line — the header/body separator.
                headerEnd = lineStart;
                return i + 1;
            }

            lineStart = i + 1;
        }

        // No blank line found: treat the whole range as headers with an empty body.
        headerEnd = end;
        return end;
    }

    private static MimeHeaderCollection ParseHeaders(byte[] data, int start, int end, List<string> warnings)
    {
        var headers = new MimeHeaderCollection();
        if (end <= start)
            return headers;

        // Headers are nominally ASCII; Latin-1 maps every byte 1:1 so raw 8-bit bytes survive
        // for later RFC 2047 / charset handling.
        var block = Encoding.Latin1.GetString(data, start, end - start);

        string? name = null;
        var value = new StringBuilder();

        foreach (var line in SplitLines(block))
        {
            if (line.Length == 0)
                continue;

            if (line[0] == ' ' || line[0] == '\t')
            {
                // Folded continuation of the previous header.
                if (name is not null)
                    value.Append(line);
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                warnings.Add($"Skipped malformed header line: '{Truncate(line, 60)}'.");
                continue;
            }

            if (name is not null)
                headers.Add(name, value.ToString());

            name = line[..colon].Trim();
            var raw = line[(colon + 1)..];
            if (raw.Length > 0 && raw[0] == ' ')
                raw = raw[1..];
            value.Clear();
            value.Append(raw);
        }

        if (name is not null)
            headers.Add(name, value.ToString());

        return headers;
    }

    // -------------------------------------------------------------- multipart splitting

    private static List<MimeEntity> SplitMultipart(byte[] data, int bodyStart, int end, string boundary,
        int depth, List<string> warnings)
    {
        var children = new List<MimeEntity>();
        var marker = Encoding.ASCII.GetBytes("--" + boundary);

        var markers = FindBoundaryMarkers(data, bodyStart, end, marker);
        if (markers.Count == 0)
            return children;

        for (var k = 0; k < markers.Count; k++)
        {
            if (markers[k].IsClose)
                break;

            var partStart = markers[k].EndExclusive;
            var rawPartEnd = k + 1 < markers.Count ? markers[k + 1].Start : end;
            var partEnd = TrimTrailingEol(data, partStart, rawPartEnd);

            if (partEnd <= partStart)
                continue; // empty part — skip

            if (depth + 1 <= MaxDepth)
                children.Add(ParseEntity(data, partStart, partEnd, depth + 1, warnings));
        }

        return children;
    }

    private readonly struct BoundaryMarker
    {
        public BoundaryMarker(int start, int endExclusive, bool isClose)
        {
            Start = start;
            EndExclusive = endExclusive;
            IsClose = isClose;
        }

        public int Start { get; }
        public int EndExclusive { get; }
        public bool IsClose { get; }
    }

    private static List<BoundaryMarker> FindBoundaryMarkers(byte[] data, int start, int end, byte[] marker)
    {
        var list = new List<BoundaryMarker>();
        var lineStart = start;

        while (lineStart < end)
        {
            var newline = -1;
            for (var j = lineStart; j < end; j++)
            {
                if (data[j] == (byte)'\n')
                {
                    newline = j;
                    break;
                }
            }

            int contentEnd;
            int lineEndExclusive;
            if (newline < 0)
            {
                contentEnd = end;
                lineEndExclusive = end;
            }
            else
            {
                contentEnd = newline;
                if (contentEnd > lineStart && data[contentEnd - 1] == (byte)'\r')
                    contentEnd--;
                lineEndExclusive = newline + 1;
            }

            var kind = ClassifyBoundaryLine(data, lineStart, contentEnd, marker);
            if (kind != 0)
                list.Add(new BoundaryMarker(lineStart, lineEndExclusive, kind == 2));

            if (newline < 0)
                break;
            lineStart = newline + 1;
        }

        return list;
    }

    /// <summary>Returns 0 (not a boundary), 1 (delimiter) or 2 (closing delimiter).</summary>
    private static int ClassifyBoundaryLine(byte[] data, int lineStart, int contentEnd, byte[] marker)
    {
        if (contentEnd - lineStart < marker.Length)
            return 0;

        for (var j = 0; j < marker.Length; j++)
        {
            if (data[lineStart + j] != marker[j])
                return 0;
        }

        var p = lineStart + marker.Length;
        var close = false;
        if (contentEnd - p >= 2 && data[p] == (byte)'-' && data[p + 1] == (byte)'-')
        {
            close = true;
            p += 2;
        }

        // Anything left on the line must be insignificant whitespace.
        for (var j = p; j < contentEnd; j++)
        {
            if (data[j] != (byte)' ' && data[j] != (byte)'\t')
                return 0;
        }

        return close ? 2 : 1;
    }

    // -------------------------------------------------------------- helpers

    private static MimeEntity CreateFallbackPart(byte[] data, int start, int end, int depth)
    {
        var leaf = new MimeEntity(new MimeHeaderCollection(), depth);
        var content = Slice(data, start, end);
        leaf.RawContent = content;
        leaf.Content = content;
        return leaf;
    }

    private static int TrimTrailingEol(byte[] data, int start, int end)
    {
        var e = end;
        if (e > start && data[e - 1] == (byte)'\n')
        {
            e--;
            if (e > start && data[e - 1] == (byte)'\r')
                e--;
        }
        return e;
    }

    private static byte[] Slice(byte[] data, int start, int end)
    {
        var length = Math.Max(0, end - start);
        if (length == 0)
            return Array.Empty<byte>();

        var result = new byte[length];
        Array.Copy(data, start, result, 0, length);
        return result;
    }

    private static IEnumerable<string> SplitLines(string block)
    {
        var lineStart = 0;
        for (var i = 0; i < block.Length; i++)
        {
            if (block[i] != '\n')
                continue;

            var lineEnd = i;
            if (lineEnd > lineStart && block[lineEnd - 1] == '\r')
                lineEnd--;
            yield return block[lineStart..lineEnd];
            lineStart = i + 1;
        }

        if (lineStart < block.Length)
            yield return block[lineStart..];
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
