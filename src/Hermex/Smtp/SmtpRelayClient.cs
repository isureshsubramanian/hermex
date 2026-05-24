using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace Hermex.Smtp;

/// <summary>
/// A minimal hand-rolled SMTP client that forwards a captured message, byte-for-byte, to a
/// real upstream SMTP server. Sending the raw payload preserves the message exactly.
/// </summary>
internal static class SmtpRelayClient
{
    public static async Task SendAsync(
        HermexRelayOptions relay,
        string mailFrom,
        IReadOnlyList<string> recipients,
        byte[] rawMessage,
        CancellationToken cancellationToken)
    {
        if (recipients.Count == 0)
            throw new HermexException("The message has no envelope recipients to relay to.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(relay.Timeout);
        var ct = timeout.Token;

        using var client = new TcpClient();
        await client.ConnectAsync(relay.Host, relay.Port, ct).ConfigureAwait(false);
        client.NoDelay = true;

        Stream stream = client.GetStream();
        var conversation = new RelayConversation(stream);

        await conversation.ExpectAsync(220, ct).ConfigureAwait(false);
        var capabilities = await conversation.EhloAsync(ct).ConfigureAwait(false);

        if (relay.UseStartTls && capabilities.Contains("STARTTLS"))
        {
            await conversation.CommandAsync("STARTTLS", 220, ct).ConfigureAwait(false);

            // Development tooling: accept whatever certificate the upstream presents.
            var ssl = new SslStream(stream, leaveInnerStreamOpen: false,
                userCertificateValidationCallback: static (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync(relay.Host).ConfigureAwait(false);
            stream = ssl;
            conversation = new RelayConversation(stream);
            await conversation.EhloAsync(ct).ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(relay.Username))
            await conversation.AuthLoginAsync(relay.Username!, relay.Password ?? string.Empty, ct).ConfigureAwait(false);

        await conversation.CommandAsync($"MAIL FROM:<{mailFrom}>", 250, ct).ConfigureAwait(false);
        foreach (var recipient in recipients)
        {
            if (!string.IsNullOrWhiteSpace(recipient))
                await conversation.CommandAsync($"RCPT TO:<{recipient.Trim()}>", 250, ct).ConfigureAwait(false);
        }

        await conversation.CommandAsync("DATA", 354, ct).ConfigureAwait(false);
        await conversation.SendMessageBodyAsync(rawMessage, ct).ConfigureAwait(false);
        await conversation.ExpectAsync(250, ct).ConfigureAwait(false);

        try
        {
            await conversation.CommandAsync("QUIT", 221, ct).ConfigureAwait(false);
        }
        catch
        {
            // The message was already accepted; a noisy QUIT is not worth failing the relay.
        }
    }

    /// <summary>Drives the line-level SMTP conversation with the upstream server.</summary>
    private sealed class RelayConversation
    {
        private readonly Stream _stream;
        private readonly SmtpLineReader _reader;

        public RelayConversation(Stream stream)
        {
            _stream = stream;
            _reader = new SmtpLineReader(stream);
        }

        public async Task<(int Code, List<string> Lines)> ReadReplyAsync(CancellationToken ct)
        {
            var code = 0;
            var lines = new List<string>();
            while (true)
            {
                var line = await _reader.ReadLineAsync(8192, ct).ConfigureAwait(false);
                if (line.Status != SmtpLineStatus.Ok)
                    throw new HermexException("The upstream SMTP server closed the connection unexpectedly.");

                var text = line.AsAscii();
                if (text.Length >= 3 && int.TryParse(text.AsSpan(0, 3), out var parsed))
                    code = parsed;
                lines.Add(text.Length > 4 ? text[4..] : string.Empty);

                // A '-' as the 4th character marks a continuation line.
                if (text.Length < 4 || text[3] != '-')
                    break;
            }
            return (code, lines);
        }

        public async Task ExpectAsync(int expectedCode, CancellationToken ct)
        {
            var (code, lines) = await ReadReplyAsync(ct).ConfigureAwait(false);
            if (code != expectedCode)
            {
                throw new HermexException(
                    $"Upstream SMTP server replied {code} (expected {expectedCode}): {string.Join(' ', lines)}");
            }
        }

        public async Task SendLineAsync(string line, CancellationToken ct)
        {
            var bytes = Encoding.ASCII.GetBytes(line + "\r\n");
            await _stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }

        public async Task CommandAsync(string command, int expectedCode, CancellationToken ct)
        {
            await SendLineAsync(command, ct).ConfigureAwait(false);
            await ExpectAsync(expectedCode, ct).ConfigureAwait(false);
        }

        public async Task<HashSet<string>> EhloAsync(CancellationToken ct)
        {
            await SendLineAsync("EHLO hermex.relay", ct).ConfigureAwait(false);
            var (code, lines) = await ReadReplyAsync(ct).ConfigureAwait(false);

            if (code != 250)
            {
                // Fall back to plain HELO for servers that do not support ESMTP.
                await SendLineAsync("HELO hermex.relay", ct).ConfigureAwait(false);
                await ExpectAsync(250, ct).ConfigureAwait(false);
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            var capabilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines)
            {
                var keyword = line.Split(' ', 2)[0];
                if (keyword.Length > 0)
                    capabilities.Add(keyword);
            }
            return capabilities;
        }

        public async Task AuthLoginAsync(string user, string password, CancellationToken ct)
        {
            await SendLineAsync("AUTH LOGIN", ct).ConfigureAwait(false);
            await ExpectAsync(334, ct).ConfigureAwait(false);
            await SendLineAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(user)), ct).ConfigureAwait(false);
            await ExpectAsync(334, ct).ConfigureAwait(false);
            await SendLineAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(password)), ct).ConfigureAwait(false);
            await ExpectAsync(235, ct).ConfigureAwait(false);
        }

        public async Task SendMessageBodyAsync(byte[] raw, CancellationToken ct)
        {
            using var payload = new MemoryStream(raw.Length + 64);
            var i = 0;
            while (i < raw.Length)
            {
                var lineEnd = i;
                while (lineEnd < raw.Length && raw[lineEnd] != (byte)'\n')
                    lineEnd++;

                var contentEnd = lineEnd;
                if (contentEnd > i && raw[contentEnd - 1] == (byte)'\r')
                    contentEnd--;

                // Dot-stuffing: a line beginning with '.' gets an extra leading '.'.
                if (contentEnd > i && raw[i] == (byte)'.')
                    payload.WriteByte((byte)'.');

                payload.Write(raw, i, contentEnd - i);
                payload.WriteByte((byte)'\r');
                payload.WriteByte((byte)'\n');

                i = lineEnd < raw.Length ? lineEnd + 1 : raw.Length;
            }

            // End-of-data terminator.
            payload.WriteByte((byte)'.');
            payload.WriteByte((byte)'\r');
            payload.WriteByte((byte)'\n');

            var bytes = payload.ToArray();
            await _stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
    }
}
