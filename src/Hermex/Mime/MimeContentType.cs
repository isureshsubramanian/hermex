using System.Text;

namespace Hermex.Mime;

/// <summary>A parsed MIME <c>Content-Type</c> header (media type plus parameters).</summary>
public sealed class MimeContentType
{
    public MimeContentType(string type, string subType, IReadOnlyDictionary<string, string> parameters)
    {
        Type = type;
        SubType = subType;
        Parameters = parameters;
    }

    /// <summary>The top-level type, e.g. <c>text</c>, <c>multipart</c>, <c>image</c>.</summary>
    public string Type { get; }

    /// <summary>The subtype, e.g. <c>plain</c>, <c>html</c>, <c>mixed</c>.</summary>
    public string SubType { get; }

    /// <summary>Case-insensitive parameters such as <c>charset</c> and <c>boundary</c>.</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; }

    /// <summary>The full media type, e.g. <c>text/html</c>.</summary>
    public string MediaType => $"{Type}/{SubType}".ToLowerInvariant();

    /// <summary>The <c>charset</c> parameter, if present.</summary>
    public string? Charset => GetParameter("charset");

    /// <summary>The multipart <c>boundary</c> parameter, if present.</summary>
    public string? Boundary => GetParameter("boundary");

    /// <summary>The legacy <c>name</c> parameter (an older way to label attachments).</summary>
    public string? Name => GetParameter("name");

    /// <summary>True for any <c>multipart/*</c> media type.</summary>
    public bool IsMultipart => string.Equals(Type, "multipart", StringComparison.OrdinalIgnoreCase);

    /// <summary>True for any <c>text/*</c> media type.</summary>
    public bool IsText => string.Equals(Type, "text", StringComparison.OrdinalIgnoreCase);

    /// <summary>True for an embedded <c>message/rfc822</c> entity.</summary>
    public bool IsRfc822 =>
        string.Equals(Type, "message", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(SubType, "rfc822", StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns a parameter value by name, or <c>null</c> when absent.</summary>
    public string? GetParameter(string name) =>
        Parameters.TryGetValue(name, out var value) ? value : null;

    /// <summary>The default content type assumed when a part has no <c>Content-Type</c> header.</summary>
    public static MimeContentType Default { get; } =
        new("text", "plain", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>Parses a raw <c>Content-Type</c> header value, never throwing.</summary>
    public static MimeContentType Parse(string? headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
            return Default;

        var (mediaType, parameters) = MimeParameterParser.Parse(headerValue);

        var slash = mediaType.IndexOf('/');
        string type, subType;
        if (slash > 0)
        {
            type = mediaType[..slash].Trim();
            subType = mediaType[(slash + 1)..].Trim();
        }
        else
        {
            // Malformed value such as "text" — assume a sensible subtype.
            type = mediaType.Trim();
            subType = string.Empty;
        }

        if (string.IsNullOrEmpty(type))
            return Default;
        if (string.IsNullOrEmpty(subType))
            subType = string.Equals(type, "text", StringComparison.OrdinalIgnoreCase) ? "plain" : "octet-stream";

        return new MimeContentType(type.ToLowerInvariant(), subType.ToLowerInvariant(), parameters);
    }

    public override string ToString() => MediaType;
}

/// <summary>A parsed MIME <c>Content-Disposition</c> header.</summary>
public sealed class MimeContentDisposition
{
    public MimeContentDisposition(string disposition, IReadOnlyDictionary<string, string> parameters)
    {
        Disposition = disposition;
        Parameters = parameters;
    }

    /// <summary>The disposition token, typically <c>inline</c> or <c>attachment</c>.</summary>
    public string Disposition { get; }

    /// <summary>Case-insensitive disposition parameters such as <c>filename</c>.</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; }

    /// <summary>True when the disposition is <c>attachment</c>.</summary>
    public bool IsAttachment => string.Equals(Disposition, "attachment", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the disposition is <c>inline</c>.</summary>
    public bool IsInline => string.Equals(Disposition, "inline", StringComparison.OrdinalIgnoreCase);

    /// <summary>The <c>filename</c> parameter, if present.</summary>
    public string? FileName => Parameters.TryGetValue("filename", out var value) ? value : null;

    /// <summary>Parses a raw <c>Content-Disposition</c> header value, never throwing.</summary>
    public static MimeContentDisposition? Parse(string? headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
            return null;

        var (disposition, parameters) = MimeParameterParser.Parse(headerValue);
        return new MimeContentDisposition(
            string.IsNullOrEmpty(disposition) ? "attachment" : disposition.Trim().ToLowerInvariant(),
            parameters);
    }
}

/// <summary>
/// Parses the "value; name=val; name2=&quot;quoted val&quot;" structure shared by
/// <c>Content-Type</c> and <c>Content-Disposition</c>, including RFC 2231 extended and
/// continued parameters (<c>name*=</c>, <c>name*0=</c>, <c>name*0*=</c>).
/// </summary>
internal static class MimeParameterParser
{
    public static (string Value, IReadOnlyDictionary<string, string> Parameters) Parse(string input)
    {
        var segments = SplitRespectingQuotes(input);
        var value = segments.Count > 0 ? segments[0].Trim() : string.Empty;

        // Raw parameters keyed exactly as written (so RFC 2231 sections survive).
        var raw = new List<KeyValuePair<string, string>>();
        for (var i = 1; i < segments.Count; i++)
        {
            var segment = segments[i];
            var eq = segment.IndexOf('=');
            if (eq < 0)
                continue;

            var key = segment[..eq].Trim();
            var paramValue = segment[(eq + 1)..].Trim();
            if (key.Length == 0)
                continue;

            raw.Add(new KeyValuePair<string, string>(key, paramValue));
        }

        var result = AssembleParameters(raw);
        return (value, result);
    }

    private static Dictionary<string, string> AssembleParameters(List<KeyValuePair<string, string>> raw)
    {
        // Group RFC 2231 continuations: baseName -> ordered sections.
        var groups = new Dictionary<string, SortedDictionary<int, (string Text, bool Extended)>>(StringComparer.OrdinalIgnoreCase);
        var simple = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (rawKey, rawValue) in raw)
        {
            var key = rawKey;
            var extended = false;
            var section = 0;

            if (key.EndsWith('*'))
            {
                extended = true;
                key = key[..^1];
            }

            var starIndex = key.LastIndexOf('*');
            if (starIndex >= 0 && int.TryParse(key[(starIndex + 1)..], out var parsedSection))
            {
                section = parsedSection;
                key = key[..starIndex];
            }

            var text = Unquote(rawValue);

            if (!groups.TryGetValue(key, out var sections))
            {
                sections = new SortedDictionary<int, (string, bool)>();
                groups[key] = sections;
            }
            sections[section] = (text, extended);
        }

        foreach (var (key, sections) in groups)
        {
            // A non-extended, single-section parameter is the common simple case.
            if (sections.Count == 1 && sections.TryGetValue(0, out var only) && !only.Extended)
            {
                simple[key] = only.Text;
                continue;
            }

            simple[key] = DecodeExtended(sections);
        }

        return simple;
    }

    private static string DecodeExtended(SortedDictionary<int, (string Text, bool Extended)> sections)
    {
        string? charset = null;
        var encodedBuilder = new StringBuilder();
        var literalBuilder = new StringBuilder();
        var first = true;

        foreach (var (_, (text, extended)) in sections)
        {
            if (extended)
            {
                var value = text;
                if (first)
                {
                    // First extended section carries charset'language'value.
                    var firstQuote = value.IndexOf('\'');
                    if (firstQuote >= 0)
                    {
                        var secondQuote = value.IndexOf('\'', firstQuote + 1);
                        if (secondQuote >= 0)
                        {
                            charset = value[..firstQuote];
                            value = value[(secondQuote + 1)..];
                        }
                    }
                }
                encodedBuilder.Append(value);
            }
            else
            {
                literalBuilder.Append(text);
            }
            first = false;
        }

        var decoded = encodedBuilder.Length > 0
            ? PercentDecode(encodedBuilder.ToString(), charset)
            : string.Empty;

        return decoded + literalBuilder;
    }

    private static string PercentDecode(string value, string? charset)
    {
        var bytes = new List<byte>(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '%' && i + 2 < value.Length &&
                MimeContentDecoder.IsHex(value[i + 1]) && MimeContentDecoder.IsHex(value[i + 2]))
            {
                bytes.Add((byte)((MimeContentDecoder.HexValue(value[i + 1]) << 4) |
                                 MimeContentDecoder.HexValue(value[i + 2])));
                i += 2;
            }
            else
            {
                bytes.Add((byte)c);
            }
        }

        try
        {
            return MimeEncoding.GetEncoding(charset).GetString(bytes.ToArray());
        }
        catch
        {
            return value;
        }
    }

    private static string Unquote(string value)
    {
        value = value.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            var inner = value[1..^1];
            // Honour backslash escaping inside quoted strings.
            var sb = new StringBuilder(inner.Length);
            for (var i = 0; i < inner.Length; i++)
            {
                if (inner[i] == '\\' && i + 1 < inner.Length)
                {
                    sb.Append(inner[i + 1]);
                    i++;
                }
                else
                {
                    sb.Append(inner[i]);
                }
            }
            return sb.ToString();
        }
        return value;
    }

    private static List<string> SplitRespectingQuotes(string input)
    {
        var segments = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
                sb.Append(c);
            }
            else if (c == '\\' && inQuotes && i + 1 < input.Length)
            {
                sb.Append(c);
                sb.Append(input[i + 1]);
                i++;
            }
            else if (c == ';' && !inQuotes)
            {
                segments.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }

        if (sb.Length > 0)
            segments.Add(sb.ToString());

        return segments;
    }
}
