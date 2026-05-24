using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Hermex.Diagnostics;

namespace Hermex.Smtp;

/// <summary>
/// Drives one SMTP connection through the RFC 5321 command/response state machine:
/// greeting, HELO/EHLO, MAIL, RCPT, DATA (with dot-unstuffing), AUTH, STARTTLS, RSET, NOOP
/// and QUIT. Optionally records the conversation as a transcript.
/// </summary>
internal sealed class SmtpSession
{
    private const int MaxCommandLength = 4096;
    private const int MaxDataLineLength = 1024 * 1024;
    private const int MaxRecipients = 200;
    private const int TranscriptLineCap = 400;

    private static readonly byte[] Crlf = { (byte)'\r', (byte)'\n' };

    private readonly TcpClient _client;
    private readonly HermexOptions _options;
    private readonly HermexRuntimeState _state;
    private readonly IReceivedMailSink _sink;
    private readonly IHermexEventLog _eventLog;
    private readonly X509Certificate2? _tlsCertificate;
    private readonly string _sessionId;
    private readonly string _remoteEndPoint;
    private readonly List<string> _transcript = new();

    private Stream _stream = Stream.Null;
    private SmtpLineReader _reader = null!;

    private string? _heloName;
    private bool _authenticated;
    private string? _authUser;
    private bool _securedWithTls;
    private string? _mailFrom;
    private bool _hasMailFrom;
    private readonly List<string> _recipients = new();

    public SmtpSession(TcpClient client, HermexOptions options, HermexRuntimeState state,
        IReceivedMailSink sink, IHermexEventLog eventLog, X509Certificate2? tlsCertificate)
    {
        _client = client;
        _options = options;
        _state = state;
        _sink = sink;
        _eventLog = eventLog;
        _tlsCertificate = tlsCertificate;
        _sessionId = Guid.NewGuid().ToString("N")[..12];
        _remoteEndPoint = SafeRemoteEndPoint(client);
    }

    public async Task RunAsync(CancellationToken serverToken)
    {
        try
        {
            _client.NoDelay = true;
            Stream transport = _client.GetStream();

            // Implicit TLS — the whole connection is encrypted from the first byte.
            if (_options.TlsMode == HermexTlsMode.Implicit && _tlsCertificate is not null)
            {
                var ssl = new SslStream(transport, leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsServerAsync(_tlsCertificate, clientCertificateRequired: false,
                    checkCertificateRevocation: false).ConfigureAwait(false);
                transport = ssl;
                _securedWithTls = true;
            }

            _stream = transport;
            _reader = new SmtpLineReader(_stream);

            _eventLog.Info("Smtp",
                $"Connection opened from {_remoteEndPoint}{(_securedWithTls ? " (implicit TLS)" : string.Empty)}.",
                _sessionId, _remoteEndPoint);
            await SendAsync($"220 {_options.ServerHostName} Hermex SMTP service ready", serverToken).ConfigureAwait(false);

            while (!serverToken.IsCancellationRequested)
            {
                var command = await ReadCommandAsync(serverToken).ConfigureAwait(false);
                if (command is null)
                    break;

                if (!await DispatchAsync(command, serverToken).ConfigureAwait(false))
                    break;
            }
        }
        catch (OperationCanceledException) { /* shutdown or idle timeout */ }
        catch (IOException) { /* the client dropped the connection */ }
        catch (SocketException) { /* the client dropped the connection */ }
        catch (AuthenticationException ex)
        {
            _eventLog.Warning("Smtp", $"TLS handshake failed: {ex.Message}", _sessionId, _remoteEndPoint);
        }
        catch (Exception ex)
        {
            _eventLog.Error("Smtp", $"Session error: {ex.Message}", _sessionId, _remoteEndPoint);
        }
        finally
        {
            try { _client.Close(); }
            catch { /* ignore */ }
            _eventLog.Debug("Smtp", $"Connection closed ({_remoteEndPoint}).", _sessionId, _remoteEndPoint);
        }
    }

    // -------------------------------------------------------------- command dispatch

    private async Task<bool> DispatchAsync(string commandLine, CancellationToken ct)
    {
        commandLine = commandLine.TrimEnd();
        if (commandLine.Length == 0)
        {
            await SendAsync("500 5.5.2 Command not recognized", ct).ConfigureAwait(false);
            return true;
        }

        var space = commandLine.IndexOf(' ');
        var verb = (space < 0 ? commandLine : commandLine[..space]).ToUpperInvariant();
        var args = space < 0 ? string.Empty : commandLine[(space + 1)..].Trim();

        RecordCommand(verb, args, commandLine);

        switch (verb)
        {
            case "HELO": return await HandleHeloAsync(args, extended: false, ct).ConfigureAwait(false);
            case "EHLO": return await HandleHeloAsync(args, extended: true, ct).ConfigureAwait(false);
            case "STARTTLS": return await HandleStartTlsAsync(ct).ConfigureAwait(false);
            case "MAIL": return await HandleMailAsync(args, ct).ConfigureAwait(false);
            case "RCPT": return await HandleRcptAsync(args, ct).ConfigureAwait(false);
            case "DATA": return await HandleDataAsync(ct).ConfigureAwait(false);
            case "AUTH": return await HandleAuthAsync(args, ct).ConfigureAwait(false);
            case "RSET":
                ResetTransaction();
                await SendAsync("250 2.0.0 OK", ct).ConfigureAwait(false);
                return true;
            case "NOOP":
                await SendAsync("250 2.0.0 OK", ct).ConfigureAwait(false);
                return true;
            case "VRFY":
                await SendAsync("252 2.1.5 Cannot verify, but will accept the message", ct).ConfigureAwait(false);
                return true;
            case "EXPN":
                await SendAsync("252 2.1.5 Cannot expand the mailing list", ct).ConfigureAwait(false);
                return true;
            case "HELP":
                await SendAsync("214 2.0.0 Supported: HELO EHLO STARTTLS MAIL RCPT DATA RSET NOOP AUTH QUIT", ct)
                    .ConfigureAwait(false);
                return true;
            case "QUIT":
                await SendAsync($"221 2.0.0 {_options.ServerHostName} closing connection", ct).ConfigureAwait(false);
                return false;
            default:
                await SendAsync("500 5.5.2 Command not recognized", ct).ConfigureAwait(false);
                return true;
        }
    }

    private async Task<bool> HandleHeloAsync(string args, bool extended, CancellationToken ct)
    {
        _heloName = string.IsNullOrWhiteSpace(args) ? null : args.Trim();
        ResetTransaction();

        if (!extended)
        {
            await SendAsync($"250 {_options.ServerHostName} Hello {DescribeClient()}", ct).ConfigureAwait(false);
            return true;
        }

        var lines = new List<string>
        {
            $"{_options.ServerHostName} Hello {DescribeClient()}",
            $"SIZE {_options.MaxMessageSizeBytes}",
            "8BITMIME",
            "SMTPUTF8",
            "PIPELINING",
            "ENHANCEDSTATUSCODES",
        };
        if (CanOfferStartTls())
            lines.Add("STARTTLS");
        lines.Add("AUTH PLAIN LOGIN");
        lines.Add("HELP");

        await SendMultilineAsync(250, lines, ct).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> HandleStartTlsAsync(CancellationToken ct)
    {
        if (_securedWithTls)
        {
            await SendAsync("503 5.5.1 TLS is already active", ct).ConfigureAwait(false);
            return true;
        }
        if (!CanOfferStartTls())
        {
            await SendAsync("502 5.5.1 STARTTLS is not available", ct).ConfigureAwait(false);
            return true;
        }

        await SendAsync("220 2.0.0 Ready to start TLS", ct).ConfigureAwait(false);

        var ssl = new SslStream(_stream, leaveInnerStreamOpen: false);
        try
        {
            await ssl.AuthenticateAsServerAsync(_tlsCertificate!, clientCertificateRequired: false,
                checkCertificateRevocation: false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _eventLog.Warning("Smtp", $"STARTTLS handshake failed: {ex.Message}", _sessionId, _remoteEndPoint);
            await ssl.DisposeAsync().ConfigureAwait(false);
            return false;
        }

        _stream = ssl;
        _reader = new SmtpLineReader(_stream);
        _securedWithTls = true;
        Record("--- TLS negotiated ---");

        // RFC 3207: discard all state learned before the TLS upgrade.
        _heloName = null;
        _authenticated = false;
        _authUser = null;
        ResetTransaction();
        return true;
    }

    private async Task<bool> HandleMailAsync(string args, CancellationToken ct)
    {
        if (_heloName is null)
        {
            await SendAsync("503 5.5.1 Send HELO/EHLO first", ct).ConfigureAwait(false);
            return true;
        }
        if (_options.RequireAuthentication && !_authenticated)
        {
            await SendAsync("530 5.7.0 Authentication required", ct).ConfigureAwait(false);
            return true;
        }
        if (!args.StartsWith("FROM:", StringComparison.OrdinalIgnoreCase))
        {
            await SendAsync("501 5.5.4 Syntax: MAIL FROM:<address>", ct).ConfigureAwait(false);
            return true;
        }

        var (path, parameters) = ParsePath(args[5..]);
        var declaredSize = TryGetSizeParameter(parameters);
        if (declaredSize > _options.MaxMessageSizeBytes)
        {
            await SendAsync(
                $"552 5.3.4 Declared size {declaredSize} exceeds the {_options.MaxMessageSizeBytes}-byte limit",
                ct).ConfigureAwait(false);
            return true;
        }

        ResetTransaction();
        _mailFrom = path;
        _hasMailFrom = true;
        await SendAsync("250 2.1.0 Sender OK", ct).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> HandleRcptAsync(string args, CancellationToken ct)
    {
        if (!_hasMailFrom)
        {
            await SendAsync("503 5.5.1 Send MAIL FROM first", ct).ConfigureAwait(false);
            return true;
        }
        if (!args.StartsWith("TO:", StringComparison.OrdinalIgnoreCase))
        {
            await SendAsync("501 5.5.4 Syntax: RCPT TO:<address>", ct).ConfigureAwait(false);
            return true;
        }
        if (_recipients.Count >= MaxRecipients)
        {
            await SendAsync("452 4.5.3 Too many recipients", ct).ConfigureAwait(false);
            return true;
        }

        var (path, _) = ParsePath(args[3..]);
        if (string.IsNullOrWhiteSpace(path))
        {
            await SendAsync("501 5.1.3 A recipient address is required", ct).ConfigureAwait(false);
            return true;
        }

        _recipients.Add(path);
        await SendAsync("250 2.1.5 Recipient OK", ct).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> HandleDataAsync(CancellationToken ct)
    {
        if (!_hasMailFrom)
        {
            await SendAsync("503 5.5.1 Send MAIL FROM first", ct).ConfigureAwait(false);
            return true;
        }
        if (_recipients.Count == 0)
        {
            await SendAsync("503 5.5.1 Send RCPT TO first", ct).ConfigureAwait(false);
            return true;
        }

        await SendAsync("354 Start mail input; end with <CRLF>.<CRLF>", ct).ConfigureAwait(false);

        using var buffer = new MemoryStream();
        long size = 0;
        var oversize = false;

        while (true)
        {
            var line = await ReadLineWithTimeoutAsync(MaxDataLineLength, ct).ConfigureAwait(false);

            if (line.Status == SmtpLineStatus.EndOfStream)
                return false; // client vanished mid-DATA

            if (line.Status == SmtpLineStatus.TooLong)
            {
                oversize = true;
                continue;
            }

            var data = line.Bytes;
            if (data.Length == 1 && data[0] == (byte)'.')
                break;

            var offset = data.Length > 0 && data[0] == (byte)'.' ? 1 : 0;
            var contentLength = data.Length - offset;

            size += contentLength + 2;
            if (size > _options.MaxMessageSizeBytes)
            {
                oversize = true;
                continue;
            }

            if (!oversize)
            {
                buffer.Write(data, offset, contentLength);
                buffer.Write(Crlf, 0, Crlf.Length);
            }
        }

        if (oversize)
        {
            _state.OnMessageRejected();
            _eventLog.Warning("Smtp",
                $"Message rejected — exceeds the {_options.MaxMessageSizeBytes}-byte limit.",
                _sessionId, _remoteEndPoint);
            await SendAsync(
                $"552 5.3.4 Message exceeds the {_options.MaxMessageSizeBytes}-byte size limit",
                ct).ConfigureAwait(false);
            ResetTransaction();
            return true;
        }

        var raw = buffer.ToArray();
        Record($"C: [message body — {raw.Length} bytes]");
        Record("C: .");

        var message = new ReceivedMessage
        {
            SessionId = _sessionId,
            MailFrom = _mailFrom ?? string.Empty,
            Recipients = _recipients.ToArray(),
            RawData = raw,
            ReceivedAtUtc = DateTimeOffset.UtcNow,
            RemoteEndPoint = _remoteEndPoint,
            ClientId = _heloName,
            AuthenticatedUser = _authUser,
            SecuredWithTls = _securedWithTls,
            Transcript = _options.CaptureSessionTranscript ? string.Join("\r\n", _transcript) : null,
        };

        switch (_sink.Submit(message))
        {
            case MailSubmissionResult.Accepted:
                _state.OnMessageReceived(raw.Length);
                await SendAsync($"250 2.0.0 OK: message accepted ({raw.Length} bytes)", ct).ConfigureAwait(false);
                break;
            case MailSubmissionResult.QueueFull:
                _state.OnMessageRejected();
                _eventLog.Warning("Smtp", "Ingestion queue is full — message deferred.", _sessionId, _remoteEndPoint);
                await SendAsync("451 4.3.1 Ingestion queue is full, please retry shortly", ct).ConfigureAwait(false);
                break;
            default:
                _state.OnMessageRejected();
                await SendAsync("554 5.3.0 Message rejected", ct).ConfigureAwait(false);
                break;
        }

        ResetTransaction();
        return true;
    }

    private async Task<bool> HandleAuthAsync(string args, CancellationToken ct)
    {
        if (_authenticated)
        {
            await SendAsync("503 5.5.1 Already authenticated", ct).ConfigureAwait(false);
            return true;
        }

        var space = args.IndexOf(' ');
        var mechanism = (space < 0 ? args : args[..space]).ToUpperInvariant();
        var initialResponse = space < 0 ? null : args[(space + 1)..].Trim();

        // Hermex is a development tool: authentication always succeeds. The mechanism
        // handshake exists only so apps configured to send authenticated mail still work.
        switch (mechanism)
        {
            case "PLAIN":
            {
                var response = initialResponse;
                if (string.IsNullOrEmpty(response))
                {
                    await SendAsync("334 ", ct).ConfigureAwait(false);
                    response = await ReadCommandAsync(ct).ConfigureAwait(false);
                    if (response is null)
                        return false;
                    Record("C: [credentials hidden]");
                }
                _authUser = DecodePlainUser(response);
                _authenticated = true;
                await SendAsync("235 2.7.0 Authentication successful", ct).ConfigureAwait(false);
                return true;
            }
            case "LOGIN":
            {
                await SendAsync("334 VXNlcm5hbWU6", ct).ConfigureAwait(false); // base64("Username:")
                var user = await ReadCommandAsync(ct).ConfigureAwait(false);
                if (user is null)
                    return false;
                Record("C: [credentials hidden]");
                await SendAsync("334 UGFzc3dvcmQ6", ct).ConfigureAwait(false); // base64("Password:")
                var password = await ReadCommandAsync(ct).ConfigureAwait(false);
                if (password is null)
                    return false;
                Record("C: [credentials hidden]");
                _authUser = SafeBase64ToString(user);
                _authenticated = true;
                await SendAsync("235 2.7.0 Authentication successful", ct).ConfigureAwait(false);
                return true;
            }
            default:
                await SendAsync("504 5.5.4 Unrecognized authentication mechanism", ct).ConfigureAwait(false);
                return true;
        }
    }

    // -------------------------------------------------------------- I/O helpers

    private async Task<string?> ReadCommandAsync(CancellationToken serverToken)
    {
        SmtpLine line;
        try
        {
            line = await ReadLineWithTimeoutAsync(MaxCommandLength, serverToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!serverToken.IsCancellationRequested)
        {
            try { await SendAsync("421 4.4.2 Idle timeout — closing connection", CancellationToken.None).ConfigureAwait(false); }
            catch { /* ignore */ }
            return null;
        }

        return line.Status switch
        {
            SmtpLineStatus.EndOfStream => null,
            SmtpLineStatus.TooLong => string.Empty, // DispatchAsync answers 500
            _ => line.AsAscii(),
        };
    }

    private async Task<SmtpLine> ReadLineWithTimeoutAsync(int maxLength, CancellationToken serverToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        timeoutCts.CancelAfter(_options.SessionTimeout);
        return await _reader.ReadLineAsync(maxLength, timeoutCts.Token).ConfigureAwait(false);
    }

    private async Task SendAsync(string line, CancellationToken ct)
    {
        Record("S: " + line);
        var bytes = Encoding.ASCII.GetBytes(line + "\r\n");
        await _stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private async Task SendMultilineAsync(int code, IReadOnlyList<string> lines, CancellationToken ct)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            var separator = i == lines.Count - 1 ? ' ' : '-';
            Record($"S: {code}{separator}{lines[i]}");
            sb.Append(code).Append(separator).Append(lines[i]).Append("\r\n");
        }
        var bytes = Encoding.ASCII.GetBytes(sb.ToString());
        await _stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
    }

    // -------------------------------------------------------------- transcript

    private void RecordCommand(string verb, string args, string fullLine)
    {
        if (!_options.CaptureSessionTranscript)
            return;

        if (verb == "AUTH")
        {
            var space = args.IndexOf(' ');
            var mechanism = space < 0 ? args : args[..space];
            Record(space < 0
                ? $"C: AUTH {mechanism}"
                : $"C: AUTH {mechanism} [credentials hidden]");
        }
        else
        {
            Record("C: " + fullLine);
        }
    }

    private void Record(string entry)
    {
        if (!_options.CaptureSessionTranscript)
            return;
        if (_transcript.Count > TranscriptLineCap)
            return;
        if (_transcript.Count == TranscriptLineCap)
        {
            _transcript.Add("… transcript truncated …");
            return;
        }
        _transcript.Add(entry);
    }

    // -------------------------------------------------------------- parsing helpers

    private bool CanOfferStartTls() =>
        _options.TlsMode == HermexTlsMode.StartTls && _tlsCertificate is not null && !_securedWithTls;

    private void ResetTransaction()
    {
        _mailFrom = null;
        _hasMailFrom = false;
        _recipients.Clear();
    }

    private string DescribeClient() => _heloName ?? _remoteEndPoint;

    private static (string Path, string Parameters) ParsePath(string input)
    {
        input = input.Trim();

        var lt = input.IndexOf('<');
        var gt = lt >= 0 ? input.IndexOf('>', lt + 1) : -1;
        if (lt >= 0 && gt > lt)
        {
            var path = input[(lt + 1)..gt].Trim();
            var parameters = input[(gt + 1)..].Trim();
            return (path, parameters);
        }

        var space = input.IndexOf(' ');
        return space < 0 ? (input, string.Empty) : (input[..space], input[(space + 1)..].Trim());
    }

    private static long TryGetSizeParameter(string parameters)
    {
        if (string.IsNullOrWhiteSpace(parameters))
            return 0;

        foreach (var token in parameters.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith("SIZE=", StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(token.AsSpan(5), out var size))
            {
                return size;
            }
        }
        return 0;
    }

    private static string? DecodePlainUser(string base64)
    {
        try
        {
            var bytes = Convert.FromBase64String(base64.Trim());
            var parts = Encoding.UTF8.GetString(bytes).Split('\0');
            return parts.Length >= 2 && parts[1].Length > 0 ? parts[1] : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? SafeBase64ToString(string base64)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(base64.Trim()));
        }
        catch
        {
            return null;
        }
    }

    private static string SafeRemoteEndPoint(TcpClient client)
    {
        try
        {
            return client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }
}
