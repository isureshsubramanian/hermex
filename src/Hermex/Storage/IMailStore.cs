using Hermex.Diagnostics;

namespace Hermex.Storage;

/// <summary>Persistent store for captured messages and diagnostic logs.</summary>
public interface IMailStore
{
    /// <summary>Creates the database schema if it does not yet exist. Safe to call repeatedly.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>Inserts a batch of messages in a single transaction; assigns <see cref="MessageRecord.Id"/>.</summary>
    Task AddMessagesAsync(IReadOnlyList<MessageRecord> records, CancellationToken cancellationToken = default);

    /// <summary>Returns a page of inbox summaries, newest first.</summary>
    Task<PagedResult<MessageSummary>> GetMessagesAsync(MessageQuery query, CancellationToken cancellationToken = default);

    /// <summary>Returns the full detail of a message, or <c>null</c> when it does not exist.</summary>
    Task<MessageDetail?> GetMessageAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Returns the raw RFC 5322 bytes of a message, or <c>null</c> when it does not exist.</summary>
    Task<byte[]?> GetRawMessageAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Returns an attachment by message id and attachment index, or <c>null</c>.</summary>
    Task<AttachmentContent?> GetAttachmentAsync(long messageId, int attachmentIndex,
        CancellationToken cancellationToken = default);

    /// <summary>Marks a message read or unread. Returns <c>false</c> when the message is missing.</summary>
    Task<bool> SetReadAsync(long id, bool isRead, CancellationToken cancellationToken = default);

    /// <summary>Deletes a single message. Returns <c>false</c> when the message is missing.</summary>
    Task<bool> DeleteMessageAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Deletes every message; returns the number removed.</summary>
    Task<int> ClearMessagesAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns aggregate statistics.</summary>
    Task<MailStats> GetStatsAsync(CancellationToken cancellationToken = default);

    /// <summary>Applies the configured retention policy; returns the number of messages pruned.</summary>
    Task<int> ApplyRetentionAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns every mailbox with its message and unread counts.</summary>
    Task<IReadOnlyList<Mailbox>> GetMailboxesAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns a mailbox by address (case-insensitive), or <c>null</c>.</summary>
    Task<Mailbox?> GetMailboxAsync(string address, CancellationToken cancellationToken = default);

    /// <summary>Returns the messages in a mailbox ordered by UID ascending, with sequence numbers.</summary>
    Task<IReadOnlyList<MailboxMessageInfo>> GetMailboxMessagesAsync(long mailboxId,
        CancellationToken cancellationToken = default);

    /// <summary>Sets the IMAP <c>\Deleted</c> flag for a message within a mailbox.</summary>
    Task SetMailboxMessageDeletedAsync(long mailboxId, long messageId, bool deleted,
        CancellationToken cancellationToken = default);

    /// <summary>Removes <c>\Deleted</c>-flagged messages from a mailbox; returns the removed UIDs.</summary>
    Task<IReadOnlyList<long>> ExpungeMailboxAsync(long mailboxId, CancellationToken cancellationToken = default);

    /// <summary>Inserts a batch of diagnostic log entries.</summary>
    Task AddLogsAsync(IReadOnlyList<HermexLogEntry> entries, CancellationToken cancellationToken = default);

    /// <summary>Returns the most recent diagnostic log entries, newest first.</summary>
    Task<IReadOnlyList<HermexLogEntry>> GetLogsAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>Deletes every diagnostic log entry; returns the number removed.</summary>
    Task<int> ClearLogsAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns the persisted runtime settings as a key/value map.</summary>
    Task<IReadOnlyDictionary<string, string>> GetSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>Persists (inserts or updates) the supplied runtime settings.</summary>
    Task SaveSettingsAsync(IReadOnlyDictionary<string, string> settings, CancellationToken cancellationToken = default);
}
