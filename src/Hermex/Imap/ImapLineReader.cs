namespace Hermex.Imap;

/// <summary>
/// A buffered reader for the IMAP wire protocol: CRLF-terminated command lines plus
/// exact-length reads for IMAP literals (<c>{n}</c>).
/// </summary>
internal sealed class ImapLineReader
{
    private const int MaxLineLength = 65536;

    private readonly Stream _stream;
    private readonly byte[] _buffer;
    private int _position;
    private int _length;

    public ImapLineReader(Stream stream, int bufferSize = 8192)
    {
        _stream = stream;
        _buffer = new byte[Math.Max(1024, bufferSize)];
    }

    /// <summary>Reads one CRLF-terminated line; returns the bytes without the terminator, or <c>null</c> at end of stream.</summary>
    public async Task<byte[]?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var line = new List<byte>(128);
        while (true)
        {
            if (_position >= _length)
            {
                _length = await _stream.ReadAsync(_buffer.AsMemory(0, _buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                _position = 0;
                if (_length == 0)
                    return line.Count == 0 ? null : Trim(line);
            }

            var newline = -1;
            for (var i = _position; i < _length; i++)
            {
                if (_buffer[i] == (byte)'\n')
                {
                    newline = i;
                    break;
                }
            }

            if (newline < 0)
            {
                for (var i = _position; i < _length; i++)
                {
                    if (line.Count < MaxLineLength)
                        line.Add(_buffer[i]);
                }
                _position = _length;
                continue;
            }

            for (var i = _position; i < newline; i++)
            {
                if (line.Count < MaxLineLength)
                    line.Add(_buffer[i]);
            }
            _position = newline + 1;
            return Trim(line);
        }
    }

    /// <summary>Reads exactly <paramref name="count"/> raw bytes — used to consume IMAP literals.</summary>
    public async Task<byte[]> ReadExactAsync(int count, CancellationToken cancellationToken)
    {
        var result = new byte[Math.Max(0, count)];
        var read = 0;
        while (read < result.Length)
        {
            if (_position >= _length)
            {
                _length = await _stream.ReadAsync(_buffer.AsMemory(0, _buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                _position = 0;
                if (_length == 0)
                    throw new EndOfStreamException("The IMAP client closed the connection while sending a literal.");
            }

            var available = Math.Min(_length - _position, result.Length - read);
            Array.Copy(_buffer, _position, result, read, available);
            _position += available;
            read += available;
        }
        return result;
    }

    private static byte[] Trim(List<byte> line)
    {
        if (line.Count > 0 && line[^1] == (byte)'\r')
            line.RemoveAt(line.Count - 1);
        return line.ToArray();
    }
}
