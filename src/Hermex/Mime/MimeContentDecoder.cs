namespace Hermex.Mime;

/// <summary>
/// Decodes MIME Content-Transfer-Encoding payloads (<c>base64</c> and <c>quoted-printable</c>).
/// Every routine is deliberately lenient: invalid input degrades to a best-effort result
/// instead of throwing.
/// </summary>
public static class MimeContentDecoder
{
    /// <summary>Decodes <paramref name="content"/> according to the given transfer encoding.</summary>
    public static byte[] Decode(byte[] content, string? transferEncoding)
    {
        var cte = (transferEncoding ?? string.Empty).Trim().Trim('"').ToLowerInvariant();
        return cte switch
        {
            "base64" => DecodeBase64(content),
            "quoted-printable" => DecodeQuotedPrintable(content),
            // 7bit, 8bit, binary, empty or unknown encodings are passed through untouched.
            _ => content,
        };
    }

    /// <summary>Decodes a base64 payload, ignoring whitespace, line breaks and stray characters.</summary>
    public static byte[] DecodeBase64(byte[] input)
    {
        if (input.Length == 0)
            return Array.Empty<byte>();

        var chars = new List<char>(input.Length);
        foreach (var b in input)
        {
            var c = (char)b;
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                (c >= '0' && c <= '9') || c == '+' || c == '/')
            {
                chars.Add(c);
            }
            // '=', CR, LF, spaces and any other byte are intentionally skipped.
        }

        var remainder = chars.Count % 4;
        if (remainder == 1)
        {
            chars.RemoveAt(chars.Count - 1); // a single trailing char cannot encode a byte
        }
        else if (remainder == 2)
        {
            chars.Add('='); chars.Add('=');
        }
        else if (remainder == 3)
        {
            chars.Add('=');
        }

        if (chars.Count == 0)
            return Array.Empty<byte>();

        try
        {
            return Convert.FromBase64CharArray(chars.ToArray(), 0, chars.Count);
        }
        catch
        {
            return Array.Empty<byte>();
        }
    }

    /// <summary>Decodes a quoted-printable payload, handling soft line breaks and malformed escapes.</summary>
    public static byte[] DecodeQuotedPrintable(byte[] input)
    {
        var output = new List<byte>(input.Length);
        var i = 0;
        while (i < input.Length)
        {
            var b = input[i];
            if (b != (byte)'=')
            {
                output.Add(b);
                i++;
                continue;
            }

            // "=XX" hex escape.
            if (i + 2 < input.Length && IsHex(input[i + 1]) && IsHex(input[i + 2]))
            {
                output.Add((byte)((HexValue(input[i + 1]) << 4) | HexValue(input[i + 2])));
                i += 3;
                continue;
            }

            // Soft line breaks: "=\r\n", "=\n" or a stray "=\r".
            if (i + 2 < input.Length && input[i + 1] == (byte)'\r' && input[i + 2] == (byte)'\n')
            {
                i += 3;
                continue;
            }
            if (i + 1 < input.Length && (input[i + 1] == (byte)'\n' || input[i + 1] == (byte)'\r'))
            {
                i += 2;
                continue;
            }

            // Trailing '=' or '=' followed by non-hex: keep it literally.
            output.Add(b);
            i++;
        }

        return output.ToArray();
    }

    internal static bool IsHex(byte b) =>
        (b >= '0' && b <= '9') || (b >= 'A' && b <= 'F') || (b >= 'a' && b <= 'f');

    internal static bool IsHex(char c) =>
        (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f');

    internal static int HexValue(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        _ => 0,
    };

    internal static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'A' and <= 'F' => c - 'A' + 10,
        >= 'a' and <= 'f' => c - 'a' + 10,
        _ => 0,
    };
}
