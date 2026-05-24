using System.Text;

namespace Hermex.Mime;

/// <summary>An email address with an optional display name, parsed from an RFC 5322 header.</summary>
public sealed class MimeAddress
{
    public MimeAddress(string displayName, string address)
    {
        DisplayName = displayName;
        Address = address;
    }

    /// <summary>The human-readable name (RFC 2047 decoded), or an empty string.</summary>
    public string DisplayName { get; }

    /// <summary>The email address itself, e.g. <c>user@example.com</c>.</summary>
    public string Address { get; }

    /// <summary>Whether a non-empty display name is present.</summary>
    public bool HasDisplayName => !string.IsNullOrWhiteSpace(DisplayName);

    /// <summary>A friendly representation: <c>Name &lt;address&gt;</c> or just the address.</summary>
    public override string ToString() =>
        HasDisplayName ? $"{DisplayName} <{Address}>" : Address;

    /// <summary>Parses a single address; returns the first address of a list, or <c>null</c>.</summary>
    public static MimeAddress? ParseSingle(string? headerValue)
    {
        var list = ParseList(headerValue);
        return list.Count > 0 ? list[0] : null;
    }

    /// <summary>Parses an address-list header value (To, Cc, ...) into individual addresses.</summary>
    public static IReadOnlyList<MimeAddress> ParseList(string? headerValue)
    {
        var result = new List<MimeAddress>();
        if (string.IsNullOrWhiteSpace(headerValue))
            return result;

        var cleaned = RemoveComments(headerValue);

        foreach (var token in SplitTopLevelCommas(cleaned))
        {
            var address = ParseToken(token);
            if (address is not null)
                result.Add(address);
        }

        return result;
    }

    private static MimeAddress? ParseToken(string token)
    {
        token = StripGroupPrefix(token).Trim().Trim(';').Trim();
        if (token.Length == 0)
            return null;

        string display;
        string address;

        var lt = token.IndexOf('<');
        var gt = token.LastIndexOf('>');
        if (lt >= 0 && gt > lt)
        {
            address = token[(lt + 1)..gt].Trim();
            display = token[..lt].Trim();
        }
        else
        {
            address = token.Trim();
            display = string.Empty;
        }

        display = Unquote(display);
        display = Rfc2047Decoder.Decode(display);

        // Some senders place the address in the display slot and leave the brackets empty.
        if (address.Length == 0 && display.Contains('@'))
        {
            address = display;
            display = string.Empty;
        }

        if (address.Length == 0 && display.Length == 0)
            return null;

        return new MimeAddress(display, address);
    }

    private static string StripGroupPrefix(string token)
    {
        // Group syntax: "Group Name: addr1, addr2;". After comma-splitting, the group name
        // is glued to the first member. Drop a leading "name:" that precedes the address.
        var inQuotes = false;
        var angle = 0;
        for (var i = 0; i < token.Length; i++)
        {
            var c = token[i];
            switch (c)
            {
                case '"':
                    inQuotes = !inQuotes;
                    break;
                case '<' when !inQuotes:
                    angle++;
                    break;
                case '>' when !inQuotes:
                    if (angle > 0) angle--;
                    break;
                case '@' when !inQuotes && angle == 0:
                    return token; // reached the address before any ':' — not a group prefix
                case ':' when !inQuotes && angle == 0:
                    return token[(i + 1)..];
            }
        }
        return token;
    }

    private static string Unquote(string value)
    {
        value = value.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            var inner = value[1..^1];
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

    private static IEnumerable<string> SplitTopLevelCommas(string value)
    {
        var sb = new StringBuilder();
        var inQuotes = false;
        var angle = 0;

        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    inQuotes = !inQuotes;
                    sb.Append(c);
                    break;
                case '<' when !inQuotes:
                    angle++;
                    sb.Append(c);
                    break;
                case '>' when !inQuotes:
                    if (angle > 0) angle--;
                    sb.Append(c);
                    break;
                case ',' when !inQuotes && angle == 0:
                    if (sb.Length > 0) yield return sb.ToString();
                    sb.Clear();
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        if (sb.Length > 0)
            yield return sb.ToString();
    }

    private static string RemoveComments(string value)
    {
        var sb = new StringBuilder(value.Length);
        var inQuotes = false;
        var commentDepth = 0;

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];

            if (inQuotes)
            {
                sb.Append(c);
                if (c == '\\' && i + 1 < value.Length)
                {
                    sb.Append(value[i + 1]);
                    i++;
                }
                else if (c == '"')
                {
                    inQuotes = false;
                }
                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    sb.Append(c);
                    break;
                case '(':
                    commentDepth++;
                    break;
                case ')':
                    if (commentDepth > 0) commentDepth--;
                    else sb.Append(c);
                    break;
                case '\\' when commentDepth > 0 && i + 1 < value.Length:
                    i++; // skip escaped char inside a comment
                    break;
                default:
                    if (commentDepth == 0)
                        sb.Append(c);
                    break;
            }
        }

        return sb.ToString();
    }
}
