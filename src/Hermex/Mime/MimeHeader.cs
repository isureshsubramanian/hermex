using System.Collections;

namespace Hermex.Mime;

/// <summary>A single MIME/RFC 5322 header field with its unfolded value.</summary>
public sealed class MimeHeader
{
    public MimeHeader(string name, string value)
    {
        Name = name;
        Value = value;
    }

    /// <summary>The header field name, e.g. <c>Content-Type</c>.</summary>
    public string Name { get; }

    /// <summary>The unfolded raw header value (RFC 2047 encoded-words are <em>not</em> decoded here).</summary>
    public string Value { get; }

    public override string ToString() => $"{Name}: {Value}";
}

/// <summary>An ordered, case-insensitive collection of <see cref="MimeHeader"/> fields.</summary>
public sealed class MimeHeaderCollection : IEnumerable<MimeHeader>
{
    private readonly List<MimeHeader> _headers = new();

    /// <summary>Number of header fields.</summary>
    public int Count => _headers.Count;

    internal void Add(string name, string value) => _headers.Add(new MimeHeader(name, value));

    /// <summary>Returns the first value for <paramref name="name"/>, or <c>null</c> when absent.</summary>
    public string? Get(string name)
    {
        foreach (var header in _headers)
        {
            if (string.Equals(header.Name, name, StringComparison.OrdinalIgnoreCase))
                return header.Value;
        }
        return null;
    }

    /// <summary>Returns every value for <paramref name="name"/> (e.g. multiple <c>Received</c> fields).</summary>
    public IEnumerable<string> GetAll(string name)
    {
        foreach (var header in _headers)
        {
            if (string.Equals(header.Name, name, StringComparison.OrdinalIgnoreCase))
                yield return header.Value;
        }
    }

    /// <summary>Whether a header with the given name is present.</summary>
    public bool Contains(string name) => Get(name) is not null;

    public IEnumerator<MimeHeader> GetEnumerator() => _headers.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
