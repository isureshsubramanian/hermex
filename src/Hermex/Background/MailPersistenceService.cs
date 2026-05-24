using Hermex.Diagnostics;
using Hermex.Mime;
using Hermex.Realtime;
using Hermex.Smtp;
using Hermex.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hermex.Background;

/// <summary>
/// Drains the ingestion queue, parses each message off the SMTP path, persists batches in a
/// single transaction and broadcasts the result live. Running here — rather than on the SMTP
/// connection thread — is what keeps mail capture fast and the host application unaffected.
/// </summary>
internal sealed class MailPersistenceService : BackgroundService
{
    private const int LogBatchSize = 128;

    private readonly MailWriteQueue _queue;
    private readonly IMailStore _store;
    private readonly IInboxNotifier _notifier;
    private readonly IMailRelayService _relay;
    private readonly IHermexEventLog _eventLog;
    private readonly HermexOptions _options;
    private readonly ILogger<MailPersistenceService> _logger;

    public MailPersistenceService(
        MailWriteQueue queue,
        IMailStore store,
        IInboxNotifier notifier,
        IMailRelayService relay,
        IHermexEventLog eventLog,
        HermexOptions options,
        ILogger<MailPersistenceService> logger)
    {
        _queue = queue;
        _store = store;
        _notifier = notifier;
        _relay = relay;
        _eventLog = eventLog;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _store.InitializeAsync(stoppingToken).ConfigureAwait(false);

        // Messages and logs are drained on independent loops so a slow batch of one never
        // stalls the other.
        await Task.WhenAll(
            ProcessMessagesAsync(stoppingToken),
            ProcessLogsAsync(stoppingToken)).ConfigureAwait(false);
    }

    private async Task ProcessMessagesAsync(CancellationToken stoppingToken)
    {
        var reader = _queue.MessageReader;
        var batch = new List<ReceivedMessage>(Math.Max(1, _options.PersistenceBatchSize));

        try
        {
            while (await reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
            {
                batch.Clear();
                while (batch.Count < _options.PersistenceBatchSize && reader.TryRead(out var message))
                    batch.Add(message);

                if (batch.Count > 0)
                    await PersistMessageBatchAsync(batch, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }

        // Flush whatever is still queued so nothing is lost on shutdown.
        var remaining = new List<ReceivedMessage>();
        while (reader.TryRead(out var message))
            remaining.Add(message);
        if (remaining.Count > 0)
        {
            try { await PersistMessageBatchAsync(remaining, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogError(ex, "Hermex: failed to flush messages during shutdown."); }
        }
    }

    private async Task PersistMessageBatchAsync(List<ReceivedMessage> batch, CancellationToken cancellationToken)
    {
        var records = new List<MessageRecord>(batch.Count);

        foreach (var received in batch)
        {
            MimeMessage mime;
            try
            {
                mime = MimeParser.Parse(received.RawData);
            }
            catch (Exception ex)
            {
                // Parsing should never throw, but never lose a message if it does.
                _eventLog.Error("Storage",
                    $"Could not parse a message from {received.RemoteEndPoint}: {ex.Message}", received.SessionId);
                mime = MimeParser.Parse(Array.Empty<byte>());
            }

            records.Add(BuildRecord(received, mime));
        }

        try
        {
            await _store.AddMessagesAsync(records, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hermex: failed to persist a batch of {Count} message(s).", records.Count);
            _eventLog.Error("Storage", $"Failed to persist {records.Count} message(s): {ex.Message}");
            return;
        }

        foreach (var record in records)
            await _notifier.MessageReceivedAsync(ToSummary(record)).ConfigureAwait(false);

        try
        {
            var stats = await _store.GetStatsAsync(cancellationToken).ConfigureAwait(false);
            await _notifier.StatsChangedAsync(stats).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Hermex: could not refresh statistics after a batch.");
        }

        // Automatic relay runs off the persistence path; RelayAsync logs its own failures.
        if (_options.Relay is { Enabled: true, AutomaticRelay: true })
        {
            foreach (var record in records)
            {
                var id = record.Id;
                _ = _relay.RelayAsync(id, CancellationToken.None);
            }
        }
    }

    private async Task ProcessLogsAsync(CancellationToken stoppingToken)
    {
        var reader = _queue.LogReader;
        var batch = new List<HermexLogEntry>(LogBatchSize);

        try
        {
            while (await reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
            {
                batch.Clear();
                while (batch.Count < LogBatchSize && reader.TryRead(out var entry))
                    batch.Add(entry);

                if (batch.Count == 0)
                    continue;

                try
                {
                    await _store.AddLogsAsync(batch, stoppingToken).ConfigureAwait(false);
                    foreach (var entry in batch)
                        await _notifier.LogAddedAsync(entry).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Hermex: failed to persist diagnostic log entries.");
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    private static MessageRecord BuildRecord(ReceivedMessage received, MimeMessage mime)
    {
        var fromDisplay = !string.IsNullOrWhiteSpace(mime.From?.DisplayName)
            ? mime.From!.DisplayName
            : mime.From?.Address ?? received.MailFrom;

        var toDisplay = mime.To.Count > 0
            ? string.Join(", ", mime.To.Select(a => a.ToString()))
            : string.Join(", ", received.Recipients);

        return new MessageRecord
        {
            SessionId = received.SessionId,
            ReceivedAtUtc = received.ReceivedAtUtc,
            EnvelopeFrom = received.MailFrom,
            EnvelopeTo = string.Join(", ", received.Recipients),
            FromAddress = mime.From?.Address ?? received.MailFrom,
            FromDisplay = string.IsNullOrWhiteSpace(fromDisplay) ? "(unknown sender)" : fromDisplay,
            ToDisplay = toDisplay,
            Subject = string.IsNullOrEmpty(mime.Subject) ? "(no subject)" : mime.Subject!,
            HasHtml = mime.HasHtmlBody,
            HasText = mime.HasTextBody,
            RawSize = received.RawData.Length,
            AttachmentCount = mime.Attachments.Count,
            RemoteEndPoint = received.RemoteEndPoint,
            Recipients = received.Recipients,
            SecuredWithTls = received.SecuredWithTls,
            Transcript = received.Transcript,
            RawData = received.RawData,
        };
    }

    private static MessageSummary ToSummary(MessageRecord record) => new()
    {
        Id = record.Id,
        ReceivedAtUtc = record.ReceivedAtUtc,
        From = record.FromDisplay,
        FromAddress = record.FromAddress,
        To = record.ToDisplay,
        Subject = record.Subject,
        HasHtml = record.HasHtml,
        AttachmentCount = record.AttachmentCount,
        HasAttachments = record.AttachmentCount > 0,
        RawSize = record.RawSize,
        IsRead = false,
    };
}
