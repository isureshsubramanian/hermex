using System.Text;
using Hermex.Mime;
using Xunit;

namespace Hermex.Tests;

public class ContentDecoderTests
{
    [Fact]
    public void Base64_decodes_standard_input()
    {
        var original = Encoding.UTF8.GetBytes("The quick brown fox");
        var encoded = Encoding.ASCII.GetBytes(Convert.ToBase64String(original));

        Assert.Equal(original, MimeContentDecoder.DecodeBase64(encoded));
    }

    [Fact]
    public void Base64_ignores_embedded_whitespace_and_newlines()
    {
        var original = Encoding.UTF8.GetBytes("wrapped base64 content stays intact");
        var b64 = Convert.ToBase64String(original);
        var wrapped = b64[..8] + "\r\n" + b64[8..16] + " " + b64[16..];

        Assert.Equal(original, MimeContentDecoder.DecodeBase64(Encoding.ASCII.GetBytes(wrapped)));
    }

    [Fact]
    public void QuotedPrintable_decodes_hex_escapes()
    {
        var decoded = MimeContentDecoder.DecodeQuotedPrintable(Encoding.ASCII.GetBytes("Caf=C3=A9"));

        Assert.Equal("Café", Encoding.UTF8.GetString(decoded));
    }

    [Fact]
    public void QuotedPrintable_collapses_soft_line_breaks()
    {
        var decoded = MimeContentDecoder.DecodeQuotedPrintable(Encoding.ASCII.GetBytes("abc=\r\ndef"));

        Assert.Equal("abcdef", Encoding.ASCII.GetString(decoded));
    }

    [Fact]
    public void Decode_passes_through_unknown_transfer_encoding()
    {
        var input = Encoding.ASCII.GetBytes("plain 7bit text");

        Assert.Equal(input, MimeContentDecoder.Decode(input, "7bit"));
    }
}
