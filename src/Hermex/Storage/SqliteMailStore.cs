using System.Data.Common;
using System.Globalization;
using Hermex.Diagnostics;
using Hermex.Mime;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hermex.Storage;

/// <summary>
/// SQLite-backed <see cref="IMailStore"/>. Uses WAL journaling so the dashboard can read
/// while the persistence service writes, and serialises writes with a single in-process lock
/// to avoid <c>SQLITE_BUSY</c>. Inbox list rows come from indexed metadata columns; full
/// message detail is reconstructed by re-parsing the stored raw payload on demand.
/// </summary>
public sealed class SqliteMailStore : IMailStore, IDisposable
{
    private const int LogRetentionCap = 2000;

    private readonly HermexOptions _options;
    private readonly HermexRuntimeState _state;
    private readonly ILogger<SqliteMailStore> _logger;
    private readonly string _connectionString;
    private readonly string _databasePath;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public SqliteMailStore(
        HermexOptions options,
        HermexRuntimeState state,
        IHostEnvironment environment,
        ILogger<SqliteMailStore> logger)
    {
        _options = options;
        _state = state;
        _logger = logger;

        _databasePath = ResolveDatabasePath(options, environment);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Pooling = true,
            ForeignKeys = true,
        }.ToString();

        _state.DatabasePath = _databasePath;
    }

    /// <summary>Absolute path of the SQLite database file.</summary>
    public string DatabasePath => _databasePath;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
            return;

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
                return;

            var directory = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = SchemaSql;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await ApplyMigrationsAsync(connection, cancellationToken).ConfigureAwait(false);

            // Pre-create the INBOX catch-all so an IMAP client can always select it.
            await using (var seedInbox = connection.CreateCommand())
            {
                seedInbox.CommandText =
                    "INSERT OR IGNORE INTO Mailboxes (Address, CreatedAtUtc, UidValidity) " +
                    "VALUES (@Address, @Created, @Uid);";
                seedInbox.Parameters.AddWithValue("@Address", InboxMailboxName);
                seedInbox.Parameters.AddWithValue("@Created", FormatDate(DateTimeOffset.UtcNow));
                seedInbox.Parameters.AddWithValue("@Uid", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                await seedInbox.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            _initialized = true;
            _logger.LogInformation("Hermex mail store ready at {Path}.", _databasePath);
        }
        finally
        {
            _initLock.Release();
        }
    }

    // ------------------------------------------------------------------ writes

    public async Task AddMessagesAsync(IReadOnlyList<MessageRecord> records, CancellationToken cancellationToken = default)
    {
        if (records.Count == 0)
            return;

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction();

            await using var insertMessage = connection.CreateCommand();
            insertMessage.Transaction = transaction;
            insertMessage.CommandText = InsertMessageSql;

            await using var insertRaw = connection.CreateCommand();
            insertRaw.Transaction = transaction;
            insertRaw.CommandText = "INSERT INTO MessageRaw (MessageId, RawData) VALUES (@MessageId, @RawData);";

            await using var insertMailbox = connection.CreateCommand();
            insertMailbox.Transaction = transaction;
            insertMailbox.CommandText =
                "INSERT OR IGNORE INTO Mailboxes (Address, CreatedAtUtc, UidValidity) " +
                "VALUES (@Address, @CreatedAtUtc, @UidValidity);";

            await using var selectMailbox = connection.CreateCommand();
            selectMailbox.Transaction = transaction;
            selectMailbox.CommandText = "SELECT Id FROM Mailboxes WHERE Address = @Address;";

            await using var insertJunction = connection.CreateCommand();
            insertJunction.Transaction = transaction;
            insertJunction.CommandText =
                "INSERT OR IGNORE INTO MailboxMessages (MailboxId, MessageId) VALUES (@MailboxId, @MessageId);";

            var mailboxCache = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            async Task<long> ResolveMailboxAsync(string address)
            {
                if (mailboxCache.TryGetValue(address, out var cachedId))
                    return cachedId;

                insertMailbox.Parameters.Clear();
                insertMailbox.Parameters.AddWithValue("@Address", address);
                insertMailbox.Parameters.AddWithValue("@CreatedAtUtc", FormatDate(DateTimeOffset.UtcNow));
                insertMailbox.Parameters.AddWithValue("@UidValidity", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                await insertMailbox.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                selectMailbox.Parameters.Clear();
                selectMailbox.Parameters.AddWithValue("@Address", address);
                var mailboxId = Convert.ToInt64(
                    await selectMailbox.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    CultureInfo.InvariantCulture);
                mailboxCache[address] = mailboxId;
                return mailboxId;
            }

            foreach (var record in records)
            {
                insertMessage.Parameters.Clear();
                insertMessage.Parameters.AddWithValue("@SessionId", record.SessionId);
                insertMessage.Parameters.AddWithValue("@ReceivedAtUtc", FormatDate(record.ReceivedAtUtc));
                insertMessage.Parameters.AddWithValue("@EnvelopeFrom", record.EnvelopeFrom);
                insertMessage.Parameters.AddWithValue("@EnvelopeTo", record.EnvelopeTo);
                insertMessage.Parameters.AddWithValue("@FromAddress", record.FromAddress);
                insertMessage.Parameters.AddWithValue("@FromDisplay", record.FromDisplay);
                insertMessage.Parameters.AddWithValue("@ToDisplay", record.ToDisplay);
                insertMessage.Parameters.AddWithValue("@Subject", record.Subject);
                insertMessage.Parameters.AddWithValue("@HasHtml", record.HasHtml);
                insertMessage.Parameters.AddWithValue("@HasText", record.HasText);
                insertMessage.Parameters.AddWithValue("@RawSize", record.RawSize);
                insertMessage.Parameters.AddWithValue("@AttachmentCount", record.AttachmentCount);
                insertMessage.Parameters.AddWithValue("@RemoteEndPoint", record.RemoteEndPoint);
                insertMessage.Parameters.AddWithValue("@SecuredWithTls", record.SecuredWithTls);
                insertMessage.Parameters.AddWithValue("@Transcript", (object?)record.Transcript ?? DBNull.Value);

                var id = Convert.ToInt64(
                    await insertMessage.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    CultureInfo.InvariantCulture);
                record.Id = id;

                insertRaw.Parameters.Clear();
                insertRaw.Parameters.AddWithValue("@MessageId", id);
                insertRaw.Parameters.AddWithValue("@RawData", record.RawData);
                await insertRaw.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

                // Route the message into the INBOX catch-all and each recipient's mailbox.
                foreach (var recipient in RouteTargets(record.Recipients))
                {
                    var mailboxId = await ResolveMailboxAsync(recipient).ConfigureAwait(false);
                    insertJunction.Parameters.Clear();
                    insertJunction.Parameters.AddWithValue("@MailboxId", mailboxId);
                    insertJunction.Parameters.AddWithValue("@MessageId", id);
                    await insertJunction.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> SetReadAsync(long id, bool isRead, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE Messages SET IsRead = @IsRead WHERE Id = @Id;";
            command.Parameters.AddWithValue("@IsRead", isRead);
            command.Parameters.AddWithValue("@Id", id);
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> DeleteMessageAsync(long id, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM Messages WHERE Id = @Id;";
            command.Parameters.AddWithValue("@Id", id);
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<int> ClearMessagesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM Messages;";
            var removed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await using var checkpoint = connection.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await checkpoint.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return removed;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<int> ApplyRetentionAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            var deleted = 0;

            if (_options.RetentionMaxMessages > 0)
            {
                await using var byCount = connection.CreateCommand();
                byCount.CommandText =
                    "DELETE FROM Messages WHERE Id NOT IN " +
                    "(SELECT Id FROM Messages ORDER BY Id DESC LIMIT @Keep);";
                byCount.Parameters.AddWithValue("@Keep", _options.RetentionMaxMessages);
                deleted += await byCount.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (_options.RetentionMaxAge is { } maxAge && maxAge > TimeSpan.Zero)
            {
                await using var byAge = connection.CreateCommand();
                byAge.CommandText = "DELETE FROM Messages WHERE ReceivedAtUtc < @Cutoff;";
                byAge.Parameters.AddWithValue("@Cutoff", FormatDate(DateTimeOffset.UtcNow - maxAge));
                deleted += await byAge.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            return deleted;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ------------------------------------------------------------------ mailboxes

    public async Task<IReadOnlyList<Mailbox>> GetMailboxesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var uidNext = await GetUidNextAsync(connection, cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = MailboxSelectSql +
            " GROUP BY m.Id, m.Address, m.CreatedAtUtc, m.UidValidity ORDER BY m.Address;";

        var mailboxes = new List<Mailbox>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            mailboxes.Add(ReadMailbox(reader, uidNext));
        return mailboxes;
    }

    public async Task<Mailbox?> GetMailboxAsync(string address, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var uidNext = await GetUidNextAsync(connection, cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = MailboxSelectSql +
            " WHERE m.Address = @Address COLLATE NOCASE GROUP BY m.Id, m.Address, m.CreatedAtUtc, m.UidValidity;";
        command.Parameters.AddWithValue("@Address", address);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadMailbox(reader, uidNext)
            : null;
    }

    public async Task<IReadOnlyList<MailboxMessageInfo>> GetMailboxMessagesAsync(long mailboxId,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT mm.MessageId, mm.Deleted, msg.IsRead, msg.RawSize, msg.ReceivedAtUtc " +
            "FROM MailboxMessages mm JOIN Messages msg ON msg.Id = mm.MessageId " +
            "WHERE mm.MailboxId = @MailboxId ORDER BY mm.MessageId ASC;";
        command.Parameters.AddWithValue("@MailboxId", mailboxId);

        var messages = new List<MailboxMessageInfo>();
        var sequence = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            messages.Add(new MailboxMessageInfo
            {
                SequenceNumber = ++sequence,
                Uid = reader.GetInt64(0),
                IsDeleted = reader.GetBoolean(1),
                IsSeen = reader.GetBoolean(2),
                Size = reader.GetInt32(3),
                ReceivedAtUtc = ParseDate(reader.GetString(4)),
            });
        }
        return messages;
    }

    public async Task SetMailboxMessageDeletedAsync(long mailboxId, long messageId, bool deleted,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText =
                "UPDATE MailboxMessages SET Deleted = @Deleted " +
                "WHERE MailboxId = @MailboxId AND MessageId = @MessageId;";
            command.Parameters.AddWithValue("@Deleted", deleted);
            command.Parameters.AddWithValue("@MailboxId", mailboxId);
            command.Parameters.AddWithValue("@MessageId", messageId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<long>> ExpungeMailboxAsync(long mailboxId,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

            var expunged = new List<long>();
            await using (var select = connection.CreateCommand())
            {
                select.CommandText =
                    "SELECT MessageId FROM MailboxMessages " +
                    "WHERE MailboxId = @MailboxId AND Deleted = 1 ORDER BY MessageId;";
                select.Parameters.AddWithValue("@MailboxId", mailboxId);
                await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    expunged.Add(reader.GetInt64(0));
            }

            if (expunged.Count > 0)
            {
                await using var delete = connection.CreateCommand();
                delete.CommandText = "DELETE FROM MailboxMessages WHERE MailboxId = @MailboxId AND Deleted = 1;";
                delete.Parameters.AddWithValue("@MailboxId", mailboxId);
                await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            return expunged;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static Mailbox ReadMailbox(DbDataReader reader, long uidNext) => new()
    {
        Id = reader.GetInt64(0),
        Address = reader.GetString(1),
        CreatedAtUtc = ParseDate(reader.GetString(2)),
        UidValidity = reader.GetInt64(3),
        MessageCount = reader.GetInt32(4),
        UnreadCount = reader.GetInt32(5),
        UidNext = uidNext,
    };

    private static async Task<long> GetUidNextAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(Id), 0) + 1 FROM Messages;";
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    /// <summary>The catch-all mailbox name — every captured message is also filed here.</summary>
    public const string InboxMailboxName = "INBOX";

    private static IEnumerable<string> RouteTargets(IReadOnlyList<string> recipients)
    {
        // Every message lands in INBOX (a catch-all that mail clients expect to exist)...
        yield return InboxMailboxName;

        // ...as well as a mailbox per distinct recipient address.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var recipient in recipients)
        {
            var address = recipient.Trim().ToLowerInvariant();
            if (address.Length > 0 && seen.Add(address))
                yield return address;
        }
    }

    private const string MailboxSelectSql =
        "SELECT m.Id, m.Address, m.CreatedAtUtc, m.UidValidity, " +
        "COUNT(mm.MessageId), COALESCE(SUM(CASE WHEN msg.IsRead = 0 THEN 1 ELSE 0 END), 0) " +
        "FROM Mailboxes m " +
        "LEFT JOIN MailboxMessages mm ON mm.MailboxId = m.Id " +
        "LEFT JOIN Messages msg ON msg.Id = mm.MessageId";

    public async Task AddLogsAsync(IReadOnlyList<HermexLogEntry> entries, CancellationToken cancellationToken = default)
    {
        if (entries.Count == 0)
            return;

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction();

            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO Logs (TimestampUtc, Level, Category, Message, SessionId, RemoteEndPoint) " +
                "VALUES (@TimestampUtc, @Level, @Category, @Message, @SessionId, @RemoteEndPoint);";

            foreach (var entry in entries)
            {
                insert.Parameters.Clear();
                insert.Parameters.AddWithValue("@TimestampUtc", FormatDate(entry.TimestampUtc));
                insert.Parameters.AddWithValue("@Level", (int)entry.Level);
                insert.Parameters.AddWithValue("@Category", entry.Category);
                insert.Parameters.AddWithValue("@Message", entry.Message);
                insert.Parameters.AddWithValue("@SessionId", (object?)entry.SessionId ?? DBNull.Value);
                insert.Parameters.AddWithValue("@RemoteEndPoint", (object?)entry.RemoteEndPoint ?? DBNull.Value);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using var trim = connection.CreateCommand();
            trim.Transaction = transaction;
            trim.CommandText =
                "DELETE FROM Logs WHERE Id NOT IN (SELECT Id FROM Logs ORDER BY Id DESC LIMIT @Cap);";
            trim.Parameters.AddWithValue("@Cap", LogRetentionCap);
            await trim.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<int> ClearLogsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM Logs;";
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ------------------------------------------------------------------ settings

    public async Task<IReadOnlyDictionary<string, string>> GetSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Key, Value FROM Settings;";

        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            settings[reader.GetString(0)] = reader.GetString(1);
        return settings;
    }

    public async Task SaveSettingsAsync(IReadOnlyDictionary<string, string> settings,
        CancellationToken cancellationToken = default)
    {
        if (settings.Count == 0)
            return;

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "INSERT INTO Settings (Key, Value) VALUES (@Key, @Value) " +
                "ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;";

            foreach (var pair in settings)
            {
                command.Parameters.Clear();
                command.Parameters.AddWithValue("@Key", pair.Key);
                command.Parameters.AddWithValue("@Value", pair.Value);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ------------------------------------------------------------------ reads

    public async Task<PagedResult<MessageSummary>> GetMessagesAsync(MessageQuery query,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 500);
        var parameters = new List<KeyValuePair<string, object>>();
        var filter = BuildFilter(query, parameters);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        int total;
        await using (var countCommand = connection.CreateCommand())
        {
            countCommand.CommandText = "SELECT COUNT(*) FROM Messages" + filter + ";";
            foreach (var p in parameters)
                countCommand.Parameters.AddWithValue(p.Key, p.Value);
            total = Convert.ToInt32(
                await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
        }

        var items = new List<MessageSummary>();
        await using (var listCommand = connection.CreateCommand())
        {
            listCommand.CommandText =
                "SELECT Id, ReceivedAtUtc, FromDisplay, FromAddress, ToDisplay, Subject, " +
                "HasHtml, AttachmentCount, RawSize, IsRead FROM Messages" + filter +
                " ORDER BY Id DESC LIMIT @Limit OFFSET @Offset;";
            foreach (var p in parameters)
                listCommand.Parameters.AddWithValue(p.Key, p.Value);
            listCommand.Parameters.AddWithValue("@Limit", pageSize);
            listCommand.Parameters.AddWithValue("@Offset", (page - 1) * pageSize);

            await using var reader = await listCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var attachmentCount = reader.GetInt32(7);
                items.Add(new MessageSummary
                {
                    Id = reader.GetInt64(0),
                    ReceivedAtUtc = ParseDate(reader.GetString(1)),
                    From = reader.GetString(2),
                    FromAddress = reader.GetString(3),
                    To = reader.GetString(4),
                    Subject = reader.GetString(5),
                    HasHtml = reader.GetBoolean(6),
                    AttachmentCount = attachmentCount,
                    HasAttachments = attachmentCount > 0,
                    RawSize = reader.GetInt32(8),
                    IsRead = reader.GetBoolean(9),
                });
            }
        }

        return new PagedResult<MessageSummary>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = total,
        };
    }

    public async Task<MessageDetail?> GetMessageAsync(long id, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT m.Id, m.SessionId, m.ReceivedAtUtc, m.EnvelopeFrom, m.EnvelopeTo, " +
            "m.RemoteEndPoint, m.IsRead, m.SecuredWithTls, m.Transcript, r.RawData FROM Messages m " +
            "JOIN MessageRaw r ON r.MessageId = m.Id WHERE m.Id = @Id;";
        command.Parameters.AddWithValue("@Id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        return BuildDetail(
            messageId: reader.GetInt64(0),
            sessionId: reader.GetString(1),
            receivedAt: ParseDate(reader.GetString(2)),
            envelopeFrom: reader.GetString(3),
            envelopeTo: reader.GetString(4),
            remoteEndPoint: reader.GetString(5),
            isRead: reader.GetBoolean(6),
            securedWithTls: reader.GetBoolean(7),
            transcript: reader.IsDBNull(8) ? null : reader.GetString(8),
            raw: reader.GetFieldValue<byte[]>(9));
    }

    public async Task<byte[]?> GetRawMessageAsync(long id, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT RawData FROM MessageRaw WHERE MessageId = @Id;";
        command.Parameters.AddWithValue("@Id", id);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result as byte[];
    }

    public async Task<AttachmentContent?> GetAttachmentAsync(long messageId, int attachmentIndex,
        CancellationToken cancellationToken = default)
    {
        var raw = await GetRawMessageAsync(messageId, cancellationToken).ConfigureAwait(false);
        if (raw is null)
            return null;

        var message = MimeParser.Parse(raw);
        if (attachmentIndex < 0 || attachmentIndex >= message.Attachments.Count)
            return null;

        var attachment = message.Attachments[attachmentIndex];
        return new AttachmentContent
        {
            Info = new AttachmentInfo
            {
                Index = attachmentIndex,
                FileName = attachment.FileName,
                ContentType = attachment.ContentType,
                ContentId = attachment.ContentId,
                IsInline = attachment.IsInline,
                Size = attachment.Size,
            },
            Content = attachment.Content,
        };
    }

    public async Task<MailStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*), COALESCE(SUM(RawSize), 0), " +
            "COALESCE(SUM(CASE WHEN IsRead = 0 THEN 1 ELSE 0 END), 0), MAX(ReceivedAtUtc) FROM Messages;";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return new MailStats();

        return new MailStats
        {
            TotalMessages = reader.GetInt32(0),
            TotalSizeBytes = reader.GetInt64(1),
            UnreadMessages = reader.GetInt32(2),
            LastReceivedUtc = reader.IsDBNull(3) ? null : ParseDate(reader.GetString(3)),
        };
    }

    public async Task<IReadOnlyList<HermexLogEntry>> GetLogsAsync(int limit,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Id, TimestampUtc, Level, Category, Message, SessionId, RemoteEndPoint " +
            "FROM Logs ORDER BY Id DESC LIMIT @Limit;";
        command.Parameters.AddWithValue("@Limit", Math.Clamp(limit, 1, LogRetentionCap));

        var entries = new List<HermexLogEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(new HermexLogEntry
            {
                Id = reader.GetInt64(0),
                TimestampUtc = ParseDate(reader.GetString(1)),
                Level = (HermexLogLevel)reader.GetInt32(2),
                Category = reader.GetString(3),
                Message = reader.GetString(4),
                SessionId = reader.IsDBNull(5) ? null : reader.GetString(5),
                RemoteEndPoint = reader.IsDBNull(6) ? null : reader.GetString(6),
            });
        }
        return entries;
    }

    // ------------------------------------------------------------------ helpers

    private static MessageDetail BuildDetail(long messageId, string sessionId, DateTimeOffset receivedAt,
        string envelopeFrom, string envelopeTo, string remoteEndPoint, bool isRead,
        bool securedWithTls, string? transcript, byte[] raw)
    {
        var message = MimeParser.Parse(raw);

        var headers = new List<HeaderField>();
        foreach (var header in message.Root.Headers)
            headers.Add(new HeaderField(header.Name, header.Value));

        var attachments = new List<AttachmentInfo>();
        for (var i = 0; i < message.Attachments.Count; i++)
        {
            var attachment = message.Attachments[i];
            attachments.Add(new AttachmentInfo
            {
                Index = i,
                FileName = attachment.FileName,
                ContentType = attachment.ContentType,
                ContentId = attachment.ContentId,
                IsInline = attachment.IsInline,
                Size = attachment.Size,
            });
        }

        return new MessageDetail
        {
            Id = messageId,
            SessionId = sessionId,
            ReceivedAtUtc = receivedAt,
            EnvelopeFrom = envelopeFrom,
            EnvelopeTo = envelopeTo,
            RemoteEndPoint = remoteEndPoint,
            IsRead = isRead,
            SecuredWithTls = securedWithTls,
            Transcript = transcript,
            RawSize = raw.Length,
            Subject = string.IsNullOrEmpty(message.Subject) ? "(no subject)" : message.Subject!,
            FromDisplay = message.From?.DisplayName is { Length: > 0 } name ? name
                : message.From?.Address ?? envelopeFrom,
            FromAddress = message.From?.Address ?? envelopeFrom,
            ToDisplay = FormatAddresses(message.To),
            CcDisplay = FormatAddresses(message.Cc),
            TextBody = message.TextBody,
            HtmlBody = message.HtmlBody,
            MessageIdHeader = message.MessageId,
            DateHeader = message.Date,
            Headers = headers,
            Attachments = attachments,
            Structure = BuildStructure(message.Root),
            Warnings = message.Warnings,
        };
    }

    private static MimePartNode BuildStructure(MimeEntity entity)
    {
        string role;
        if (entity.IsMultipart)
            role = "container";
        else if (entity.IsAttachment)
            role = "attachment";
        else if (entity.IsInline)
            role = "inline";
        else
            role = "body";

        return new MimePartNode
        {
            MediaType = entity.MediaType,
            Role = role,
            Encoding = entity.ContentTransferEncoding,
            Charset = entity.ContentType.Charset,
            FileName = entity.FileName,
            Size = entity.IsMultipart ? 0 : entity.Content.Length,
            Children = entity.Children.Select(BuildStructure).ToList(),
        };
    }

    private static string FormatAddresses(IReadOnlyList<MimeAddress> addresses) =>
        addresses.Count == 0 ? string.Empty : string.Join(", ", addresses.Select(a => a.ToString()));

    private static string BuildFilter(MessageQuery query, List<KeyValuePair<string, object>> parameters)
    {
        var clauses = new List<string>();

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            clauses.Add("(Subject LIKE @Search OR FromDisplay LIKE @Search " +
                        "OR FromAddress LIKE @Search OR ToDisplay LIKE @Search)");
            parameters.Add(new KeyValuePair<string, object>("@Search", "%" + query.Search.Trim() + "%"));
        }

        if (query.UnreadOnly)
            clauses.Add("IsRead = 0");

        if (query.MailboxId is { } mailboxId)
        {
            clauses.Add("EXISTS (SELECT 1 FROM MailboxMessages mm " +
                        "WHERE mm.MessageId = Messages.Id AND mm.MailboxId = @MailboxId)");
            parameters.Add(new KeyValuePair<string, object>("@MailboxId", mailboxId));
        }

        return clauses.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", clauses);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;";
        await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (!_initialized)
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies additive schema migrations. Each statement is idempotent in effect — a
    /// "duplicate column" error simply means the column already exists.
    /// </summary>
    private static async Task ApplyMigrationsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        string[] migrations =
        {
            "ALTER TABLE Messages ADD COLUMN SecuredWithTls INTEGER NOT NULL DEFAULT 0;",
            "ALTER TABLE Messages ADD COLUMN Transcript TEXT;",
        };

        foreach (var sql in migrations)
        {
            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException)
            {
                // The column already exists on a database created by a newer schema.
            }
        }
    }

    private static string FormatDate(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string ResolveDatabasePath(HermexOptions options, IHostEnvironment environment)
    {
        if (!string.IsNullOrWhiteSpace(options.DatabasePath))
            return Path.GetFullPath(options.DatabasePath);

        var root = string.IsNullOrWhiteSpace(environment.ContentRootPath)
            ? AppContext.BaseDirectory
            : environment.ContentRootPath;
        return Path.GetFullPath(Path.Combine(root, "hermex", "hermex.db"));
    }

    public void Dispose()
    {
        _writeLock.Dispose();
        _initLock.Dispose();
    }

    // ------------------------------------------------------------------ SQL

    private const string SchemaSql = """
        PRAGMA journal_mode=WAL;

        CREATE TABLE IF NOT EXISTS Messages (
            Id              INTEGER PRIMARY KEY AUTOINCREMENT,
            SessionId       TEXT    NOT NULL,
            ReceivedAtUtc   TEXT    NOT NULL,
            EnvelopeFrom    TEXT    NOT NULL,
            EnvelopeTo      TEXT    NOT NULL,
            FromAddress     TEXT    NOT NULL,
            FromDisplay     TEXT    NOT NULL,
            ToDisplay       TEXT    NOT NULL,
            Subject         TEXT    NOT NULL,
            HasHtml         INTEGER NOT NULL,
            HasText         INTEGER NOT NULL,
            RawSize         INTEGER NOT NULL,
            AttachmentCount INTEGER NOT NULL,
            IsRead          INTEGER NOT NULL DEFAULT 0,
            RemoteEndPoint  TEXT    NOT NULL,
            SecuredWithTls  INTEGER NOT NULL DEFAULT 0,
            Transcript      TEXT
        );

        CREATE TABLE IF NOT EXISTS MessageRaw (
            MessageId INTEGER PRIMARY KEY REFERENCES Messages(Id) ON DELETE CASCADE,
            RawData   BLOB    NOT NULL
        );

        CREATE TABLE IF NOT EXISTS Mailboxes (
            Id           INTEGER PRIMARY KEY AUTOINCREMENT,
            Address      TEXT    NOT NULL UNIQUE,
            CreatedAtUtc TEXT    NOT NULL,
            UidValidity  INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS MailboxMessages (
            MailboxId INTEGER NOT NULL REFERENCES Mailboxes(Id) ON DELETE CASCADE,
            MessageId INTEGER NOT NULL REFERENCES Messages(Id) ON DELETE CASCADE,
            Deleted   INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (MailboxId, MessageId)
        );

        CREATE TABLE IF NOT EXISTS Logs (
            Id             INTEGER PRIMARY KEY AUTOINCREMENT,
            TimestampUtc   TEXT    NOT NULL,
            Level          INTEGER NOT NULL,
            Category       TEXT    NOT NULL,
            Message        TEXT    NOT NULL,
            SessionId      TEXT,
            RemoteEndPoint TEXT
        );

        CREATE TABLE IF NOT EXISTS Settings (
            Key   TEXT PRIMARY KEY,
            Value TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS IX_Messages_Id_Desc        ON Messages(Id DESC);
        CREATE INDEX IF NOT EXISTS IX_Messages_IsRead         ON Messages(IsRead);
        CREATE INDEX IF NOT EXISTS IX_Logs_Id_Desc            ON Logs(Id DESC);
        CREATE INDEX IF NOT EXISTS IX_MailboxMessages_Message ON MailboxMessages(MessageId);
        """;

    private const string InsertMessageSql = """
        INSERT INTO Messages
            (SessionId, ReceivedAtUtc, EnvelopeFrom, EnvelopeTo, FromAddress, FromDisplay,
             ToDisplay, Subject, HasHtml, HasText, RawSize, AttachmentCount, IsRead,
             RemoteEndPoint, SecuredWithTls, Transcript)
        VALUES
            (@SessionId, @ReceivedAtUtc, @EnvelopeFrom, @EnvelopeTo, @FromAddress, @FromDisplay,
             @ToDisplay, @Subject, @HasHtml, @HasText, @RawSize, @AttachmentCount, 0,
             @RemoteEndPoint, @SecuredWithTls, @Transcript)
        RETURNING Id;
        """;
}
