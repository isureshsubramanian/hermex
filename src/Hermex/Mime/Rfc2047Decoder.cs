using System.Text;
using System.Text.RegularExpressions;

namespace Hermex.Mime;

/// <summary>
/// Decodes RFC 2047 "encoded-words" (<c>=?charset?B?...?=</c> / <c>=?charset?Q?...?=</c>)
/// found in header fields such as Subject, From and attachment names.
/// </summary>
public static partial class Rfc2047Decoder
{
#if NET8_0_OR_GREATER
    [GeneratedRegex(@"=\?([^?\s]+)\?([BbQq])\?([^?]*)\?=", RegexOptions.CultureInvariant)]
    private static partial Regex EncodedWordRegex();
#endif

    private static readonly Regex EncodedWord =
#if NET8_0_OR_GREATER
        EncodedWordRegex();
#else
        new(@"=\?([^?\s]+)\?([BbQq])\?([^?]*)\?=", RegexOptions.Compiled | RegexOptions.CultureInvariant);
#endif

    /// <summary>
    /// Decodes every encoded-word in <paramref name="value"/>. Whitespace that merely separates
    /// two adjacent encoded-words is removed, as required by RFC 2047.
    /// </summary>
    public static string Decode(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value ?? string.Empty;
        if (!value.Contains("=?", StringComparison.Ordinal))
            return value;

        var matches = EncodedWord.Matches(value);
        if (matches.Count == 0)
            return value;

        var sb = new StringBuilder(value.Length);
        var pos = 0;
        var previousWasEncoded = false;

        foreach (Match match in matches)
        {
            var between = value.Substring(pos, match.Index - pos);
            var betweenIsWhitespaceOnly = between.Length > 0 && IsWhitespace(between);

            if (!(previousWasEncoded && betweenIsWhitespaceOnly))
                sb.Append(between);

            sb.Append(DecodeWord(match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value));

            pos = match.Index + match.Length;
            previousWasEncoded = true;
        }

        sb.Append(value[pos..]);
        return sb.ToString();
    }

    private static string DecodeWord(string charset, string encoding, string text)
    {
        try
        {
            var enc = MimeEncoding.GetEncoding(StripLanguageTag(charset));
            var bytes = encoding.Equals("B", StringComparison.OrdinalIgnoreCase)
                ? MimeContentDecoder.DecodeBase64(Encoding.ASCII.GetBytes(text))
                : DecodeQ(text);
            return enc.GetString(bytes);
        }
        catch
        {
            // Could not decode this word — surface the raw text rather than losing it.
            return text;
        }
    }

    private static byte[] DecodeQ(string text)
    {
        var bytes = new List<byte>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '_')
            {
                bytes.Add((byte)' ');
            }
            else if (c == '=' && i + 2 < text.Length &&
                     MimeContentDecoder.IsHex(text[i + 1]) && MimeContentDecoder.IsHex(text[i + 2]))
            {
                bytes.Add((byte)((MimeContentDecoder.HexValue(text[i + 1]) << 4) |
                                 MimeContentDecoder.HexValue(text[i + 2])));
                i += 2;
            }
            else
            {
                bytes.Add((byte)c);
            }
        }
        return bytes.ToArray();
    }

    private static string StripLanguageTag(string charset)
    {
        // RFC 2231 allows a "charset*language" form inside encoded-words.
        var star = charset.IndexOf('*');
        return star >= 0 ? charset[..star] : charset;
    }

    private static bool IsWhitespace(string value)
    {
        foreach (var c in value)
        {
            if (!char.IsWhiteSpace(c))
                return false;
        }
        return true;
    }
}
