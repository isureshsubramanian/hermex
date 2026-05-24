using System.Net.Sockets;
using System.Text;
using Hermex.Diagnostics;
using Hermex.Mime;
using Hermex.Storage;

namespace Hermex.Imap;

/// <summary>
/// Drives one IMAP connection. Implements a practical IMAP4rev1 subset: LOGIN/AUTHENTICATE,
/// LIST, SELECT/EXAMINE, STATUS, FETCH, SEARCH, STORE, EXPUNGE, CLOSE and the housekeeping
/// commands — enough for a real mail client to browse and manage captured mail.
/// </summary>
internal sealed class ImapSession
{
    private const string Capabilities = "IMAP4rev1 AUTH=PLAIN";
    private const int MaxLiteralBytes = 32 * 1024 * 1024;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);

    private readonly TcpClient _client;
    private readonly HermexOptions _options;
    private readonly IMailStore _store;
    private readonly IHermexEventLog _eventLog;
    private readonly string _remoteEndPoint;

    private Stream _stream = Stream.Null;
    private ImapLineReader _reader = null!;

    private bool _authenticated;
    private Mailbox? _selected;
    private bool _readOnly;
    private IReadOnlyList<MailboxMessageInfo> _messages = Array.Empty<MailboxMessageInfo>();

    public ImapSession(TcpClient client, HermexOptions options, IMailStore store, IHermexEventLog eventLog)
    {
        _client = client;
        _options = options;
        _store = store;
        _eventLog = eventLog;
        _remoteEndPoint = SafeRemoteEndPoint(client);
    }

    public async Task RunAsync(CancellationToken serverToken)
    {
        try
        {
            _client.NoDelay = true;
            _stream = _client.GetStream();
            _reader = new ImapLineReader(_stream);

            _eventLog.Info("Imap", $"Connection opened from {_remoteEndPoint}.");
            await SendAsync($"* OK [CAPABILITY {Capabilities}] Hermex IMAP service ready", serverToken)
                .ConfigureAwait(false);

            while (!serverToken.IsCancellationRequested)
            {
                var command = await ReadCommandAsync(serverToken).ConfigureAwait(false);
                if (command is null)
                    break;
                if (!await DispatchAsync(command, serverToken).ConfigureAwait(false))
                    break;
            }
        }
        catch (OperationCanceledException) { /* shutdown / idle timeout */ }
        catch (IOException) { /* client dropped (also covers EndOfStreamException mid-literal) */ }
        catch (SocketException) { /* client dropped */ }
        catch (Exception ex)
        {
            _eventLog.Error("Imap", $"Session error: {ex.Message}");
        }
        finally
        {
            try { _client.Close(); }
            catch { /* ignore */ }
            _eventLog.Debug("Imap", $"Connection closed ({_remoteEndPoint}).");
        }
    }

    // -------------------------------------------------------------- command reading

    private async Task<string?> ReadCommandAsync(CancellationToken serverToken)
    {
        var firstLine = await ReadLineAsync(serverToken).ConfigureAwait(false);
        if (firstLine is null)
            return null;

        var text = Encoding.UTF8.GetString(firstLine);

        // Resolve IMAP literals ({n} / {n+}) appended to the command.
        while (TryParseTrailingLiteral(text, out var braceIndex, out var length, out var synchronizing))
        {
            if (synchronizing)
                await SendAsync("+ Ready for literal data", serverToken).ConfigureAwait(false);

            var captured = await _reader.ReadExactAsync(Math.Min(length, MaxLiteralBytes), serverToken)
                .ConfigureAwait(false);

            var overflow = length - captured.Length;
            while (overflow > 0)
            {
                var chunk = await _reader.ReadExactAsync(Math.Min(overflow, 65536), serverToken).ConfigureAwait(false);
                overflow -= chunk.Length;
            }

            var continuation = await ReadLineAsync(serverToken).ConfigureAwait(false);
            if (continuation is null)
                return null;

            text = text[..braceIndex] + Encoding.UTF8.GetString(captured) + Encoding.UTF8.GetString(continuation);
        }

        return text;
    }

    private async Task<byte[]?> ReadLineAsync(CancellationToken serverToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        timeoutCts.CancelAfter(IdleTimeout);
        return await _reader.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false);
    }

    private static bool TryParseTrailingLiteral(string line, out int braceIndex, out int length, out bool synchronizing)
    {
        braceIndex = -1;
        length = 0;
        synchronizing = true;

        if (line.Length < 3 || line[^1] != '}')
            return false;

        var open = line.LastIndexOf('{');
        if (open < 0)
            return false;

        var inner = line[(open + 1)..^1];
        if (inner.EndsWith('+'))
        {
            synchronizing = false;
            inner = inner[..^1];
        }

        if (inner.Length == 0 || !inner.All(char.IsDigit) || !int.TryParse(inner, out length) || length < 0)
            return false;

        braceIndex = open;
        return true;
    }

    // -------------------------------------------------------------- dispatch

    private async Task<bool> DispatchAsync(string commandLine, CancellationToken ct)
    {
        var space = commandLine.IndexOf(' ');
        if (space <= 0)
        {
            await SendAsync("* BAD Empty or malformed command", ct).ConfigureAwait(false);
            return true;
        }

        var tag = commandLine[..space];
        var rest = commandLine[(space + 1)..].TrimStart();
        var space2 = rest.IndexOf(' ');
        var command = (space2 < 0 ? rest : rest[..space2]).ToUpperInvariant();
        var args = space2 < 0 ? string.Empty : rest[(space2 + 1)..].Trim();

        switch (command)
        {
            case "CAPABILITY":
                await SendAsync($"* CAPABILITY {Capabilities}", ct).ConfigureAwait(false);
                await SendAsync($"{tag} OK CAPABILITY completed", ct).ConfigureAwait(false);
                return true;

            case "NOOP":
                await SendAsync($"{tag} OK NOOP completed", ct).ConfigureAwait(false);
                return true;

            case "LOGOUT":
                await SendAsync("* BYE Hermex IMAP signing off", ct).ConfigureAwait(false);
                await SendAsync($"{tag} OK LOGOUT completed", ct).ConfigureAwait(false);
                return false;

            case "LOGIN":
                _authenticated = true;
                await SendAsync($"{tag} OK LOGIN completed", ct).ConfigureAwait(false);
                return true;

            case "AUTHENTICATE":
                return await HandleAuthenticateAsync(tag, args, ct).ConfigureAwait(false);

            case "LIST":
            case "LSUB":
                await HandleListAsync(tag, command, args, ct).ConfigureAwait(false);
                return true;

            case "SELECT":
            case "EXAMINE":
                await HandleSelectAsync(tag, command, args, ct).ConfigureAwait(false);
                return true;

            case "STATUS":
                await HandleStatusAsync(tag, args, ct).ConfigureAwait(false);
                return true;

            case "FETCH":
                await HandleFetchAsync(tag, args, byUid: false, ct).ConfigureAwait(false);
                return true;

            case "SEARCH":
                await HandleSearchAsync(tag, args, byUid: false, ct).ConfigureAwait(false);
                return true;

            case "STORE":
                await HandleStoreAsync(tag, args, byUid: false, ct).ConfigureAwait(false);
                return true;

            case "UID":
                await HandleUidAsync(tag, args, ct).ConfigureAwait(false);
                return true;

            case "EXPUNGE":
                await HandleExpungeAsync(tag, ct).ConfigureAwait(false);
                return true;

            case "CLOSE":
                await HandleCloseAsync(tag, ct).ConfigureAwait(false);
                return true;

            case "CHECK":
                await SendAsync($"{tag} OK CHECK completed", ct).ConfigureAwait(false);
                return true;

            case "SUBSCRIBE":
            case "UNSUBSCRIBE":
                await SendAsync($"{tag} OK {command} completed", ct).ConfigureAwait(false);
                return true;

            case "CREATE":
            case "DELETE":
            case "RENAME":
                await SendAsync($"{tag} NO Mailboxes are managed automatically by Hermex", ct).ConfigureAwait(false);
                return true;

            case "APPEND":
                await SendAsync($"{tag} NO APPEND is not supported by Hermex", ct).ConfigureAwait(false);
                return true;

            default:
                await SendAsync($"{tag} BAD Command '{command}' not recognized", ct).ConfigureAwait(false);
                return true;
        }
    }

    private async Task<bool> HandleAuthenticateAsync(string tag, string args, CancellationToken ct)
    {
        var mechanism = (args.Split(' ', 2)[0]).ToUpperInvariant();
        if (mechanism != "PLAIN")
        {
            await SendAsync($"{tag} NO Unsupported authentication mechanism", ct).ConfigureAwait(false);
            return true;
        }

        // If no initial response was supplied, request one and discard it.
        if (args.IndexOf(' ') < 0)
        {
            await SendAsync("+ ", ct).ConfigureAwait(false);
            var response = await ReadLineAsync(ct).ConfigureAwait(false);
            if (response is null)
                return false;
        }

        _authenticated = true;
        await SendAsync($"{tag} OK AUTHENTICATE completed", ct).ConfigureAwait(false);
        return true;
    }

    private async Task HandleListAsync(string tag, string command, string args, CancellationToken ct)
    {
        if (!await RequireAuthAsync(tag, ct).ConfigureAwait(false))
            return;

        var tokens = Tokenize(args);
        var pattern = tokens.Count >= 2 ? tokens[1] : "*";

        if (pattern.Length == 0)
        {
            // A request for the hierarchy delimiter.
            await SendAsync($"* {command} (\\Noselect) \"/\" \"\"", ct).ConfigureAwait(false);
        }
        else
        {
            foreach (var mailbox in await _store.GetMailboxesAsync(ct).ConfigureAwait(false))
                await SendAsync($"* {command} () \"/\" {ImapFormatter.QuoteOrNil(mailbox.Address)}", ct)
                    .ConfigureAwait(false);
        }

        await SendAsync($"{tag} OK {command} completed", ct).ConfigureAwait(false);
    }

    private async Task HandleSelectAsync(string tag, string command, string args, CancellationToken ct)
    {
        if (!await RequireAuthAsync(tag, ct).ConfigureAwait(false))
            return;

        var name = FirstToken(args);
        var mailbox = await _store.GetMailboxAsync(name, ct).ConfigureAwait(false);
        if (mailbox is null)
        {
            _selected = null;
            await SendAsync($"{tag} NO Mailbox '{name}' does not exist", ct).ConfigureAwait(false);
            return;
        }

        _selected = mailbox;
        _readOnly = command == "EXAMINE";
        _messages = await _store.GetMailboxMessagesAsync(mailbox.Id, ct).ConfigureAwait(false);

        var unseen = _messages.Count(m => !m.IsSeen);
        await SendAsync($"* {_messages.Count} EXISTS", ct).ConfigureAwait(false);
        await SendAsync("* 0 RECENT", ct).ConfigureAwait(false);
        await SendAsync($"* OK [UIDVALIDITY {mailbox.UidValidity}] UIDs valid", ct).ConfigureAwait(false);
        await SendAsync($"* OK [UIDNEXT {mailbox.UidNext}] Predicted next UID", ct).ConfigureAwait(false);
        if (unseen > 0)
            await SendAsync($"* OK [UNSEEN {_messages.First(m => !m.IsSeen).SequenceNumber}] First unseen", ct)
                .ConfigureAwait(false);
        await SendAsync("* FLAGS (\\Seen \\Deleted)", ct).ConfigureAwait(false);
        await SendAsync("* OK [PERMANENTFLAGS (\\Seen \\Deleted)] Limited", ct).ConfigureAwait(false);
        await SendAsync($"{tag} OK [{(_readOnly ? "READ-ONLY" : "READ-WRITE")}] {command} completed", ct)
            .ConfigureAwait(false);
    }

    private async Task HandleStatusAsync(string tag, string args, CancellationToken ct)
    {
        if (!await RequireAuthAsync(tag, ct).ConfigureAwait(false))
            return;

        var name = FirstToken(args);
        var mailbox = await _store.GetMailboxAsync(name, ct).ConfigureAwait(false);
        if (mailbox is null)
        {
            await SendAsync($"{tag} NO Mailbox '{name}' does not exist", ct).ConfigureAwait(false);
            return;
        }

        var status =
            $"MESSAGES {mailbox.MessageCount} RECENT 0 UIDNEXT {mailbox.UidNext} " +
            $"UIDVALIDITY {mailbox.UidValidity} UNSEEN {mailbox.UnreadCount}";
        await SendAsync($"* STATUS {ImapFormatter.QuoteOrNil(mailbox.Address)} ({status})", ct).ConfigureAwait(false);
        await SendAsync($"{tag} OK STATUS completed", ct).ConfigureAwait(false);
    }

    private async Task HandleUidAsync(string tag, string args, CancellationToken ct)
    {
        var space = args.IndexOf(' ');
        var sub = (space < 0 ? args : args[..space]).ToUpperInvariant();
        var rest = space < 0 ? string.Empty : args[(space + 1)..].Trim();

        switch (sub)
        {
            case "FETCH":
                await HandleFetchAsync(tag, rest, byUid: true, ct).ConfigureAwait(false);
                break;
            case "SEARCH":
                await HandleSearchAsync(tag, rest, byUid: true, ct).ConfigureAwait(false);
                break;
            case "STORE":
                await HandleStoreAsync(tag, rest, byUid: true, ct).ConfigureAwait(false);
                break;
            default:
                await SendAsync($"{tag} BAD Unsupported UID subcommand", ct).ConfigureAwait(false);
                break;
        }
    }

    // -------------------------------------------------------------- FETCH

    private async Task HandleFetchAsync(string tag, string args, bool byUid, CancellationToken ct)
    {
        if (!await RequireSelectedAsync(tag, ct).ConfigureAwait(false))
            return;

        var firstSpace = args.IndexOf(' ');
        if (firstSpace < 0)
        {
            await SendAsync($"{tag} BAD FETCH requires a set and items", ct).ConfigureAwait(false);
            return;
        }

        var set = args[..firstSpace];
        var items = ExpandFetchItems(SplitItems(args[(firstSpace + 1)..]));
        if (byUid && !items.Contains("UID", StringComparer.OrdinalIgnoreCase))
            items.Add("UID");

        var targets = ImapFormatter.SelectMessages(set, _messages, byUid);

        foreach (var message in targets)
        {
            byte[]? raw = null;
            MimeMessage? parsed = null;
            if (items.Any(NeedsMessageBody))
            {
                raw = await _store.GetRawMessageAsync(message.Uid, ct).ConfigureAwait(false);
                if (raw is not null)
                    parsed = MimeParser.Parse(raw);
            }

            var willMarkSeen = !_readOnly && !message.IsSeen && items.Any(IsNonPeekBodyItem);
            var seen = message.IsSeen || willMarkSeen;

            using var buffer = new MemoryStream();
            WriteAscii(buffer, $"* {message.SequenceNumber} FETCH (");
            for (var i = 0; i < items.Count; i++)
            {
                if (i > 0)
                    WriteAscii(buffer, " ");
                WriteFetchItem(buffer, items[i], message, seen, raw, parsed);
            }
            WriteAscii(buffer, ")\r\n");
            await WriteRawAsync(buffer.ToArray(), ct).ConfigureAwait(false);

            if (willMarkSeen)
                await _store.SetReadAsync(message.Uid, true, ct).ConfigureAwait(false);
        }

        if (willAnyMarkSeen(targets, items))
            await RefreshSelectedAsync(ct).ConfigureAwait(false);

        await SendAsync($"{tag} OK {(byUid ? "UID " : string.Empty)}FETCH completed", ct).ConfigureAwait(false);
    }

    private bool willAnyMarkSeen(IReadOnlyList<MailboxMessageInfo> targets, List<string> items)
        => !_readOnly && items.Any(IsNonPeekBodyItem) && targets.Any(m => !m.IsSeen);

    private void WriteFetchItem(MemoryStream buffer, string item, MailboxMessageInfo message, bool seen,
        byte[]? raw, MimeMessage? parsed)
    {
        var upper = item.ToUpperInvariant();

        if (upper == "UID")
        {
            WriteAscii(buffer, $"UID {message.Uid}");
        }
        else if (upper == "FLAGS")
        {
            WriteAscii(buffer, $"FLAGS ({FormatFlags(message, seen)})");
        }
        else if (upper == "RFC822.SIZE")
        {
            WriteAscii(buffer, $"RFC822.SIZE {message.Size}");
        }
        else if (upper == "INTERNALDATE")
        {
            WriteAscii(buffer, $"INTERNALDATE {ImapFormatter.FormatInternalDate(message.ReceivedAtUtc)}");
        }
        else if (upper == "ENVELOPE")
        {
            WriteAscii(buffer, "ENVELOPE " + (parsed is null ? "NIL" : ImapFormatter.FormatEnvelope(parsed)));
        }
        else if (upper is "BODYSTRUCTURE" or "BODY")
        {
            WriteAscii(buffer, upper + " " + (parsed is null ? "NIL" : ImapFormatter.FormatBodyStructure(parsed.Root)));
        }
        else if (upper == "RFC822")
        {
            WriteLiteralItem(buffer, "RFC822", raw ?? Array.Empty<byte>());
        }
        else if (upper == "RFC822.HEADER")
        {
            WriteLiteralItem(buffer, "RFC822.HEADER", GetSection(raw, parsed, "HEADER"));
        }
        else if (upper == "RFC822.TEXT")
        {
            WriteLiteralItem(buffer, "RFC822.TEXT", GetSection(raw, parsed, "TEXT"));
        }
        else if (upper.StartsWith("BODY", StringComparison.Ordinal) && item.Contains('['))
        {
            var label = item; // preserve the section spec casing the client sent
            var open = item.IndexOf('[');
            var close = item.IndexOf(']');
            var section = close > open ? item[(open + 1)..close] : string.Empty;
            var data = GetSection(raw, parsed, section);

            // The response label uses BODY[...] even when the client asked for BODY.PEEK[...].
            var responseLabel = "BODY[" + section + "]";
            WriteLiteralItem(buffer, responseLabel, data);
        }
        else
        {
            // Unknown item — emit NIL so the response stays well-formed.
            WriteAscii(buffer, item + " NIL");
        }
    }

    // -------------------------------------------------------------- SEARCH

    private async Task HandleSearchAsync(string tag, string args, bool byUid, CancellationToken ct)
    {
        if (!await RequireSelectedAsync(tag, ct).ConfigureAwait(false))
            return;

        var matches = SearchMessages(args);
        var ids = matches.Select(m => byUid ? m.Uid : m.SequenceNumber);
        await SendAsync("* SEARCH" + string.Concat(ids.Select(id => " " + id)), ct).ConfigureAwait(false);
        await SendAsync($"{tag} OK {(byUid ? "UID " : string.Empty)}SEARCH completed", ct).ConfigureAwait(false);
    }

    private List<MailboxMessageInfo> SearchMessages(string criteria)
    {
        var tokens = criteria.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        IEnumerable<MailboxMessageInfo> result = _messages;

        for (var i = 0; i < tokens.Length; i++)
        {
            switch (tokens[i].ToUpperInvariant())
            {
                case "ALL":
                case "RECENT":
                case "NEW":
                    break;
                case "SEEN":
                    result = result.Where(m => m.IsSeen);
                    break;
                case "UNSEEN":
                    result = result.Where(m => !m.IsSeen);
                    break;
                case "DELETED":
                    result = result.Where(m => m.IsDeleted);
                    break;
                case "UNDELETED":
                    result = result.Where(m => !m.IsDeleted);
                    break;
                case "UID":
                    if (i + 1 < tokens.Length)
                    {
                        var selected = ImapFormatter.SelectMessages(tokens[++i], _messages, byUid: true);
                        result = result.Where(selected.Contains);
                    }
                    break;
                // Unrecognised criteria are treated permissively (no-op).
            }
        }

        return result.OrderBy(m => m.SequenceNumber).ToList();
    }

    // -------------------------------------------------------------- STORE

    private async Task HandleStoreAsync(string tag, string args, bool byUid, CancellationToken ct)
    {
        if (!await RequireSelectedAsync(tag, ct).ConfigureAwait(false))
            return;
        if (_readOnly)
        {
            await SendAsync($"{tag} NO Mailbox is read-only", ct).ConfigureAwait(false);
            return;
        }

        var parts = args.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            await SendAsync($"{tag} BAD STORE requires a set, an operation and flags", ct).ConfigureAwait(false);
            return;
        }

        var targets = ImapFormatter.SelectMessages(parts[0], _messages, byUid);
        var operation = parts[1].ToUpperInvariant();
        var silent = operation.Contains(".SILENT");
        var add = operation.StartsWith('+');
        var remove = operation.StartsWith('-');
        var flags = parts[2];
        var setSeen = flags.Contains("\\Seen", StringComparison.OrdinalIgnoreCase);
        var setDeleted = flags.Contains("\\Deleted", StringComparison.OrdinalIgnoreCase);

        foreach (var message in targets)
        {
            if (setSeen)
            {
                var value = remove ? false : true; // FLAGS or +FLAGS set it; -FLAGS clears it
                await _store.SetReadAsync(message.Uid, value, ct).ConfigureAwait(false);
            }
            else if (!add && !remove)
            {
                // Plain FLAGS replaces the set — \Seen absent means clear it.
                await _store.SetReadAsync(message.Uid, false, ct).ConfigureAwait(false);
            }

            if (setDeleted)
            {
                var value = !remove;
                await _store.SetMailboxMessageDeletedAsync(_selected!.Id, message.Uid, value, ct).ConfigureAwait(false);
            }
            else if (!add && !remove)
            {
                await _store.SetMailboxMessageDeletedAsync(_selected!.Id, message.Uid, false, ct).ConfigureAwait(false);
            }
        }

        await RefreshSelectedAsync(ct).ConfigureAwait(false);

        if (!silent)
        {
            foreach (var message in targets)
            {
                var current = _messages.FirstOrDefault(m => m.Uid == message.Uid);
                if (current is not null)
                {
                    var flagsText = FormatFlags(current, current.IsSeen);
                    var uidSuffix = byUid ? $" UID {current.Uid}" : string.Empty;
                    await SendAsync($"* {current.SequenceNumber} FETCH (FLAGS ({flagsText}){uidSuffix})", ct)
                        .ConfigureAwait(false);
                }
            }
        }

        await SendAsync($"{tag} OK {(byUid ? "UID " : string.Empty)}STORE completed", ct).ConfigureAwait(false);
    }

    // -------------------------------------------------------------- EXPUNGE / CLOSE

    private async Task HandleExpungeAsync(string tag, CancellationToken ct)
    {
        if (!await RequireSelectedAsync(tag, ct).ConfigureAwait(false))
            return;
        if (_readOnly)
        {
            await SendAsync($"{tag} NO Mailbox is read-only", ct).ConfigureAwait(false);
            return;
        }

        var expungedUids = await _store.ExpungeMailboxAsync(_selected!.Id, ct).ConfigureAwait(false);

        // Send * <seq> EXPUNGE in descending order so lower sequence numbers stay valid.
        var sequences = expungedUids
            .Select(uid => _messages.FirstOrDefault(m => m.Uid == uid)?.SequenceNumber ?? 0)
            .Where(seq => seq > 0)
            .OrderByDescending(seq => seq);
        foreach (var seq in sequences)
            await SendAsync($"* {seq} EXPUNGE", ct).ConfigureAwait(false);

        await RefreshSelectedAsync(ct).ConfigureAwait(false);
        await SendAsync($"{tag} OK EXPUNGE completed", ct).ConfigureAwait(false);
    }

    private async Task HandleCloseAsync(string tag, CancellationToken ct)
    {
        if (_selected is not null && !_readOnly)
            await _store.ExpungeMailboxAsync(_selected.Id, ct).ConfigureAwait(false);

        _selected = null;
        _messages = Array.Empty<MailboxMessageInfo>();
        await SendAsync($"{tag} OK CLOSE completed", ct).ConfigureAwait(false);
    }

    // -------------------------------------------------------------- helpers

    private async Task RefreshSelectedAsync(CancellationToken ct)
    {
        if (_selected is not null)
            _messages = await _store.GetMailboxMessagesAsync(_selected.Id, ct).ConfigureAwait(false);
    }

    private async Task<bool> RequireAuthAsync(string tag, CancellationToken ct)
    {
        if (_authenticated)
            return true;
        await SendAsync($"{tag} NO Not authenticated", ct).ConfigureAwait(false);
        return false;
    }

    private async Task<bool> RequireSelectedAsync(string tag, CancellationToken ct)
    {
        if (!await RequireAuthAsync(tag, ct).ConfigureAwait(false))
            return false;
        if (_selected is not null)
            return true;
        await SendAsync($"{tag} BAD No mailbox selected", ct).ConfigureAwait(false);
        return false;
    }

    private static string FormatFlags(MailboxMessageInfo message, bool seen)
    {
        var flags = new List<string>(2);
        if (seen)
            flags.Add("\\Seen");
        if (message.IsDeleted)
            flags.Add("\\Deleted");
        return string.Join(' ', flags);
    }

    private static bool NeedsMessageBody(string item)
    {
        var upper = item.ToUpperInvariant();
        return upper is "ENVELOPE" or "BODYSTRUCTURE" or "BODY" or "RFC822"
            or "RFC822.HEADER" or "RFC822.TEXT"
            || (upper.StartsWith("BODY", StringComparison.Ordinal) && item.Contains('['));
    }

    private static bool IsNonPeekBodyItem(string item)
    {
        var upper = item.ToUpperInvariant();
        if (upper is "RFC822" or "RFC822.TEXT")
            return true;
        return upper.StartsWith("BODY[", StringComparison.Ordinal); // BODY.PEEK[...] does not set \Seen
    }

    private static List<string> ExpandFetchItems(List<string> items)
    {
        var expanded = new List<string>();
        foreach (var item in items)
        {
            switch (item.ToUpperInvariant())
            {
                case "ALL":
                    expanded.AddRange(new[] { "FLAGS", "INTERNALDATE", "RFC822.SIZE", "ENVELOPE" });
                    break;
                case "FAST":
                    expanded.AddRange(new[] { "FLAGS", "INTERNALDATE", "RFC822.SIZE" });
                    break;
                case "FULL":
                    expanded.AddRange(new[] { "FLAGS", "INTERNALDATE", "RFC822.SIZE", "ENVELOPE", "BODY" });
                    break;
                default:
                    expanded.Add(item);
                    break;
            }
        }
        return expanded;
    }

    private static List<string> SplitItems(string items)
    {
        items = items.Trim();
        if (items.StartsWith('(') && items.EndsWith(')'))
            items = items[1..^1].Trim();

        var result = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < items.Length; i++)
        {
            var c = items[i];
            if (c is '[' or '(')
                depth++;
            else if (c is ']' or ')')
                depth--;
            else if (c == ' ' && depth == 0)
            {
                if (i > start)
                    result.Add(items[start..i]);
                start = i + 1;
            }
        }
        if (start < items.Length)
            result.Add(items[start..]);
        return result;
    }

    /// <summary>Extracts an IMAP body section (HEADER, TEXT, numeric part, ...) from a message.</summary>
    private static byte[] GetSection(byte[]? raw, MimeMessage? parsed, string section)
    {
        if (raw is null)
            return Array.Empty<byte>();

        section = section.Trim();
        if (section.Length == 0)
            return raw;

        var split = FindHeaderBodySplit(raw);
        var upper = section.ToUpperInvariant();

        if (upper == "HEADER")
            return Slice(raw, 0, split.BodyStart);
        if (upper == "TEXT")
            return Slice(raw, split.BodyStart, raw.Length);

        if (upper.StartsWith("HEADER.FIELDS.NOT", StringComparison.Ordinal))
            return FilterHeaders(raw, split, ParseFieldList(section), include: false);
        if (upper.StartsWith("HEADER.FIELDS", StringComparison.Ordinal))
            return FilterHeaders(raw, split, ParseFieldList(section), include: true);

        // Numeric part: BODY[1], BODY[1.2], BODY[1.MIME] ...
        if (char.IsDigit(section[0]) && parsed is not null)
        {
            var path = new List<int>();
            var suffix = string.Empty;
            foreach (var segment in section.Split('.'))
            {
                if (int.TryParse(segment, out var n))
                    path.Add(n);
                else
                    suffix = segment.ToUpperInvariant();
            }

            var entity = NavigatePart(parsed.Root, path);
            if (entity is null)
                return Array.Empty<byte>();
            if (suffix == "MIME")
                return Encoding.ASCII.GetBytes(BuildHeaderText(entity));
            return entity.RawContent;
        }

        return Array.Empty<byte>();
    }

    private static MimeEntity? NavigatePart(MimeEntity root, IReadOnlyList<int> path)
    {
        var current = root;
        foreach (var oneBased in path)
        {
            var index = oneBased - 1;
            if (current.IsMultipart)
            {
                if (index < 0 || index >= current.Children.Count)
                    return null;
                current = current.Children[index];
            }
            else if (index != 0)
            {
                return null;
            }
        }
        return current;
    }

    private static string BuildHeaderText(MimeEntity entity)
    {
        var sb = new StringBuilder();
        foreach (var header in entity.Headers)
            sb.Append(header.Name).Append(": ").Append(header.Value).Append("\r\n");
        sb.Append("\r\n");
        return sb.ToString();
    }

    private static HashSet<string> ParseFieldList(string section)
    {
        var open = section.IndexOf('(');
        var close = section.IndexOf(')');
        var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (open >= 0 && close > open)
        {
            foreach (var field in section[(open + 1)..close].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                fields.Add(field.Trim());
        }
        return fields;
    }

    private static byte[] FilterHeaders(byte[] raw, (int HeaderEnd, int BodyStart) split,
        HashSet<string> fields, bool include)
    {
        var headerText = Encoding.Latin1.GetString(raw, 0, split.BodyStart);
        var output = new StringBuilder();
        string? currentName = null;
        var keep = false;

        foreach (var line in headerText.Split('\n'))
        {
            var clean = line.EndsWith('\r') ? line[..^1] : line;
            if (clean.Length == 0)
                break;

            if (clean[0] is ' ' or '\t')
            {
                if (keep)
                    output.Append(clean).Append("\r\n");
                continue;
            }

            var colon = clean.IndexOf(':');
            currentName = colon > 0 ? clean[..colon].Trim() : clean;
            keep = fields.Contains(currentName) == include;
            if (keep)
                output.Append(clean).Append("\r\n");
        }

        output.Append("\r\n");
        return Encoding.ASCII.GetBytes(output.ToString());
    }

    private static (int HeaderEnd, int BodyStart) FindHeaderBodySplit(byte[] raw)
    {
        for (var i = 0; i + 1 < raw.Length; i++)
        {
            if (raw[i] == (byte)'\n')
            {
                if (raw[i + 1] == (byte)'\n')
                    return (i + 1, i + 2);
                if (i + 2 < raw.Length && raw[i + 1] == (byte)'\r' && raw[i + 2] == (byte)'\n')
                    return (i + 1, i + 3);
            }
        }
        return (raw.Length, raw.Length);
    }

    private static byte[] Slice(byte[] data, int start, int end)
    {
        var length = Math.Max(0, end - start);
        if (length == 0)
            return Array.Empty<byte>();
        var result = new byte[length];
        Array.Copy(data, start, result, 0, length);
        return result;
    }

    private static List<string> Tokenize(string input)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < input.Length)
        {
            while (i < input.Length && input[i] == ' ')
                i++;
            if (i >= input.Length)
                break;

            if (input[i] == '"')
            {
                i++;
                var sb = new StringBuilder();
                while (i < input.Length && input[i] != '"')
                {
                    if (input[i] == '\\' && i + 1 < input.Length)
                    {
                        sb.Append(input[i + 1]);
                        i += 2;
                    }
                    else
                    {
                        sb.Append(input[i]);
                        i++;
                    }
                }
                if (i < input.Length)
                    i++;
                tokens.Add(sb.ToString());
            }
            else
            {
                var start = i;
                while (i < input.Length && input[i] != ' ')
                    i++;
                tokens.Add(input[start..i]);
            }
        }
        return tokens;
    }

    private static string FirstToken(string input)
    {
        var tokens = Tokenize(input);
        return tokens.Count > 0 ? tokens[0] : string.Empty;
    }

    private static void WriteAscii(MemoryStream buffer, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        buffer.Write(bytes, 0, bytes.Length);
    }

    private static void WriteLiteralItem(MemoryStream buffer, string label, byte[] data)
    {
        WriteAscii(buffer, $"{label} {{{data.Length}}}\r\n");
        buffer.Write(data, 0, data.Length);
    }

    private async Task SendAsync(string line, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\r\n");
        await _stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private async Task WriteRawAsync(byte[] data, CancellationToken ct)
    {
        await _stream.WriteAsync(data, ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
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
