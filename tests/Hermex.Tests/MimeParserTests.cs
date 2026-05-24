using System.Text;
using Hermex.Mime;
using Xunit;

namespace Hermex.Tests;

public class MimeParserTests
{
    [Fact]
    public void Parses_simple_plain_text_message()
    {
        var raw = TestSupport.Raw(
            "From: Alice <alice@example.com>\n" +
            "To: bob@example.com\n" +
            "Subject: Hello there\n" +
            "\n" +
            "This is the body.\n");

        var message = MimeParser.Parse(raw);

        Assert.Equal("Hello there", message.Subject);
        Assert.Equal("alice@example.com", message.From?.Address);
        Assert.Equal("Alice", message.From?.DisplayName);
        Assert.Contains("This is the body.", message.TextBody);
        Assert.Null(message.HtmlBody);
    }

    [Fact]
    public void Parses_multipart_alternative()
    {
        var raw = TestSupport.Raw(
            "Subject: Alternative\n" +
            "Content-Type: multipart/alternative; boundary=\"BOUND\"\n" +
            "\n" +
            "--BOUND\n" +
            "Content-Type: text/plain; charset=utf-8\n" +
            "\n" +
            "Plain version here\n" +
            "--BOUND\n" +
            "Content-Type: text/html; charset=utf-8\n" +
            "\n" +
            "<p>HTML version here</p>\n" +
            "--BOUND--\n");

        var message = MimeParser.Parse(raw);

        Assert.True(message.Root.IsMultipart);
        Assert.Equal(2, message.Root.Children.Count);
        Assert.Contains("Plain version here", message.TextBody);
        Assert.Contains("HTML version here", message.HtmlBody);
    }

    [Fact]
    public void Decodes_base64_body()
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("Hello from base64"));
        var raw = TestSupport.Raw(
            "Subject: Base64\n" +
            "Content-Type: text/plain; charset=utf-8\n" +
            "Content-Transfer-Encoding: base64\n" +
            "\n" +
            encoded + "\n");

        var message = MimeParser.Parse(raw);

        Assert.Contains("Hello from base64", message.TextBody);
    }

    [Fact]
    public void Decodes_quoted_printable_body()
    {
        var raw = TestSupport.Raw(
            "Subject: QP\n" +
            "Content-Type: text/plain; charset=utf-8\n" +
            "Content-Transfer-Encoding: quoted-printable\n" +
            "\n" +
            "Caf=C3=A9 and =E2=82=AC sign\n");

        var message = MimeParser.Parse(raw);

        Assert.Contains("Café", message.TextBody);
        Assert.Contains("€", message.TextBody);
    }

    [Fact]
    public void Decodes_rfc2047_encoded_subject()
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("Encoded Subject"));
        var raw = TestSupport.Raw(
            "Subject: =?utf-8?B?" + encoded + "?=\n" +
            "\n" +
            "body\n");

        var message = MimeParser.Parse(raw);

        Assert.Equal("Encoded Subject", message.Subject);
    }

    [Fact]
    public void Extracts_attachment_from_multipart_mixed()
    {
        var bytes = new byte[] { 10, 20, 30, 40, 50 };
        var raw = TestSupport.Raw(
            "Subject: With attachment\n" +
            "Content-Type: multipart/mixed; boundary=\"MIX\"\n" +
            "\n" +
            "--MIX\n" +
            "Content-Type: text/plain\n" +
            "\n" +
            "See attachment\n" +
            "--MIX\n" +
            "Content-Type: application/octet-stream\n" +
            "Content-Transfer-Encoding: base64\n" +
            "Content-Disposition: attachment; filename=\"data.bin\"\n" +
            "\n" +
            Convert.ToBase64String(bytes) + "\n" +
            "--MIX--\n");

        var message = MimeParser.Parse(raw);

        Assert.Single(message.Attachments);
        Assert.Equal("data.bin", message.Attachments[0].FileName);
        Assert.Equal(bytes, message.Attachments[0].Content);
        Assert.Contains("See attachment", message.TextBody);
    }

    [Fact]
    public void Handles_nested_multipart()
    {
        var raw = TestSupport.Raw(
            "Content-Type: multipart/mixed; boundary=\"OUT\"\n" +
            "\n" +
            "--OUT\n" +
            "Content-Type: multipart/alternative; boundary=\"IN\"\n" +
            "\n" +
            "--IN\n" +
            "Content-Type: text/plain\n" +
            "\n" +
            "nested plain text\n" +
            "--IN\n" +
            "Content-Type: text/html\n" +
            "\n" +
            "<b>nested html</b>\n" +
            "--IN--\n" +
            "--OUT--\n");

        var message = MimeParser.Parse(raw);

        Assert.Contains("nested plain text", message.TextBody);
        Assert.Contains("nested html", message.HtmlBody);
    }

    [Fact]
    public void Tolerates_multipart_without_boundary_lines()
    {
        var raw = TestSupport.Raw(
            "Content-Type: multipart/mixed; boundary=\"MISSING\"\n" +
            "\n" +
            "this body has no boundary delimiters at all\n");

        // The parser must degrade gracefully, never throw.
        var message = MimeParser.Parse(raw);

        Assert.NotEmpty(message.Warnings);
    }

    [Fact]
    public void Tolerates_empty_input()
    {
        var message = MimeParser.Parse(Array.Empty<byte>());

        Assert.NotNull(message);
        Assert.Equal(0, message.RawSize);
    }
}
