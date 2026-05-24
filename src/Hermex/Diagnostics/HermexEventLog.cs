using Hermex.Storage;
using Microsoft.Extensions.Logging;

namespace Hermex.Diagnostics;

/// <summary>
/// Default <see cref="IHermexEventLog"/>. Events are queued onto the ingestion buffer (so
/// the SMTP path is never blocked by disk I/O) and mirrored to the host's <see cref="ILogger"/>.
/// The persistence service drains the queue, stores the entries and broadcasts them live.
/// </summary>
internal sealed class HermexEventLog : IHermexEventLog
{
    private readonly MailWriteQueue _queue;
    private readonly ILogger<HermexEventLog> _logger;

    public HermexEventLog(MailWriteQueue queue, ILogger<HermexEventLog> logger)
    {
        _queue = queue;
        _logger = logger;
    }

    public void Write(HermexLogLevel level, string category, string message,
        string? sessionId = null, string? remoteEndPoint = null)
    {
        _queue.EnqueueLog(new HermexLogEntry
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            Level = level,
            Category = category,
            Message = message,
            SessionId = sessionId,
            RemoteEndPoint = remoteEndPoint,
        });

        _logger.Log(ToLogLevel(level), "Hermex[{Category}] {Message}", category, message);
    }

    private static LogLevel ToLogLevel(HermexLogLevel level) => level switch
    {
        HermexLogLevel.Debug => LogLevel.Debug,
        HermexLogLevel.Info => LogLevel.Information,
        HermexLogLevel.Warning => LogLevel.Warning,
        HermexLogLevel.Error => LogLevel.Error,
        _ => LogLevel.Information,
    };
}
