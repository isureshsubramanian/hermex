using System.Text;
using Hermex.Mime;
using Xunit;

namespace Hermex.Tests;

public class MimeAddressTests
{
    [Fact]
    public void Parses_bare_address()
    {
        var address = MimeAddress.ParseSingle("user@example.com");

        Assert.NotNull(address);
        Assert.Equal("user@example.com", address!.Address);
        Assert.False(address.HasDisplayName);
    }

    [Fact]
    public void Parses_display_name_with_angle_brackets()
    {
        var address = MimeAddress.ParseSingle("Jane Doe <jane@example.com>");

        Assert.Equal("jane@example.com", address!.Address);
        Assert.Equal("Jane Doe", address.DisplayName);
    }

    [Fact]
    public void Parses_address_list_with_quoted_comma()
    {
        var list = MimeAddress.ParseList("\"Doe, Jane\" <jane@example.com>, bob@example.com");

        Assert.Equal(2, list.Count);
        Assert.Equal("Doe, Jane", list[0].DisplayName);
        Assert.Equal("jane@example.com", list[0].Address);
        Assert.Equal("bob@example.com", list[1].Address);
    }

    [Fact]
    public void Decodes_encoded_word_display_name()
    {
        var encoded = "=?utf-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes("Renée")) + "?=";
        var address = MimeAddress.ParseSingle(encoded + " <renee@example.com>");

        Assert.Equal("Renée", address!.DisplayName);
        Assert.Equal("renee@example.com", address.Address);
    }
}
