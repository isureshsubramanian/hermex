using System.Text;

namespace Hermex.Mime;

/// <summary>
/// Resolves MIME charset names to <see cref="Encoding"/> instances. All encodings are created
/// with replacement fallbacks so malformed bytes never throw — important when decoding the
/// arbitrary, sometimes broken mail that real applications produce.
/// </summary>
public static class MimeEncoding
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    static MimeEncoding()
    {
        // Enables legacy code pages (windows-1252, iso-8859-x, koi8-r, shift_jis, ...).
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); }
        catch { /* provider already registered or unavailable — safe to ignore */ }
    }

    /// <summary>The encoding used when a charset is missing or unrecognised (UTF-8, no BOM).</summary>
    public static Encoding Default => Utf8NoBom;

    /// <summary>Resolves a charset name, falling back to UTF-8 for unknown or empty values.</summary>
    public static Encoding GetEncoding(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset))
            return Utf8NoBom;

        var name = Normalize(charset);

        try
        {
            return Encoding.GetEncoding(name, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
        }
        catch
        {
            // Unknown charset — degrade gracefully rather than failing the whole message.
            return Utf8NoBom;
        }
    }

    private static string Normalize(string charset)
    {
        var name = charset.Trim().Trim('"', '\'').Trim().ToLowerInvariant();

        // Some agents append junk such as "utf-8; format=flowed" inside the charset token.
        var cut = name.IndexOfAny(new[] { ' ', ';', '\t' });
        if (cut > 0)
            name = name[..cut];

        return name switch
        {
            "utf8" => "utf-8",
            "utf-8" => "utf-8",
            "utf7" => "utf-7",
            "latin1" or "latin-1" => "iso-8859-1",
            "cp1250" => "windows-1250",
            "cp1251" => "windows-1251",
            "cp1252" => "windows-1252",
            "unicode" or "utf-16" => "utf-16",
            "ascii" or "us-ascii" or "ansi_x3.4-1968" or "646" or "iso646-us" => "us-ascii",
            "" => "utf-8",
            _ => name,
        };
    }
}
