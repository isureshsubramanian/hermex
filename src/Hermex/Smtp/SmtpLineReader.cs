using System.Text;

namespace Hermex.Smtp;

/// <summary>Outcome of a single line read.</summary>
internal enum SmtpLineStatus
{
    /// <summary>A complete line was read.</summary>
    Ok,
    /// <summary>The connection was closed before a line could be read.</summary>
    EndOfStream,
    /// <summary>The line exceeded the permitted length and was truncated.</summary>
    TooLong,
}

/// <summary>A line read from an SMTP connection.</summary>
internal readonly struct SmtpLine
{
    private SmtpLine(SmtpLineStatus status, byte[] bytes)
    {
        Status = status;
        Bytes = bytes;
    }

    public SmtpLineStatus Status { get; }

    /// <summary>The line content with the trailing CR/LF removed.</summary>
    public byte[] Bytes { get; }

    public static SmtpLine Ok(byte[] bytes) => new(SmtpLineStatus.Ok, bytes);
    public static readonly SmtpLine EndOfStream = new(SmtpLineStatus.EndOfStream, Array.Empty<byte>());
    public static readonly SmtpLine TooLong = new(SmtpLineStatus.TooLong, Array.Empty<byte>());

    /// <summary>Decodes the line as ASCII (used for SMTP commands, which are ASCII-only).</summary>
    public string AsAscii() => Encoding.ASCII.GetString(Bytes);
}

/// <summary>
/// A buffered, CRLF-aware reader over an SMTP connection. It reads command lines and DATA
/// lines as raw bytes so message payloads survive byte-for-byte.
/// </summary>
internal sealed class SmtpLineReader
{
    private readonly Stream _stream;
    private readonly byte[] _buffer;
    private int _position;
    private int _length;

    public SmtpLineReader(Stream stream, int bufferSize = 8192)
    {
        _stream = stream;
        _buffer = new byte[Math.Max(1024, bufferSize)];
    }

    /// <summary>
    /// Reads a single CRLF-terminated line. Bytes beyond <paramref name="maxLength"/> are
    /// consumed (to keep the protocol in sync) but the result is reported as
    /// <see cref="SmtpLineStatus.TooLong"/>.
    /// </summary>
    public async Task<SmtpLine> ReadLineAsync(int maxLength, CancellationToken cancellationToken)
    {
        var line = new List<byte>(128);
        var overflow = false;

        while (true)
        {
            if (_position >= _length)
            {
                _length = await _stream.ReadAsync(_buffer.AsMemory(0, _buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                _position = 0;

                if (_length == 0)
                {
                    if (line.Count == 0)
                        return SmtpLine.EndOfStream;
                    return overflow ? SmtpLine.TooLong : SmtpLine.Ok(Trim(line));
                }
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
                    if (line.Count < maxLength) line.Add(_buffer[i]);
                    else overflow = true;
                }
                _position = _length;
                continue;
            }

            for (var i = _position; i < newline; i++)
            {
                if (line.Count < maxLength) line.Add(_buffer[i]);
                else overflow = true;
            }
            _position = newline + 1;
            return overflow ? SmtpLine.TooLong : SmtpLine.Ok(Trim(line));
        }
    }

    private static byte[] Trim(List<byte> line)
    {
        // The LF has already been consumed; drop a trailing CR if present.
        if (line.Count > 0 && line[^1] == (byte)'\r')
            line.RemoveAt(line.Count - 1);
        return line.ToArray();
    }
}
