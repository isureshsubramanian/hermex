using System.Threading.Channels;
using Hermex.Diagnostics;
using Hermex.Smtp;

namespace Hermex.Storage;

/// <summary>
/// The in-memory ingestion buffer that decouples SMTP acceptance from disk I/O. The SMTP
/// session hands a captured message here and returns immediately; the persistence service
/// drains the queue and writes in batches. A bounded capacity provides natural back-pressure:
/// when the queue is full, <see cref="Submit"/> reports <see cref="MailSubmissionResult.QueueFull"/>
/// and the SMTP client receives a "try again later" reply.
/// </summary>
public sealed class MailWriteQueue : IReceivedMailSink
{
    private readonly Channel<ReceivedMessage> _messages;
    private readonly Channel<HermexLogEntry> _logs;

    public MailWriteQueue(HermexOptions options)
    {
        _messages = Channel.CreateBounded<ReceivedMessage>(
            new BoundedChannelOptions(Math.Max(1, options.WriteQueueCapacity))
            {
                // Wait mode: synchronous TryWrite fails fast when full instead of blocking.
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            });

        _logs = Channel.CreateUnbounded<HermexLogEntry>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
            });
    }

    /// <summary>Reader drained by the persistence service for messages.</summary>
    public ChannelReader<ReceivedMessage> MessageReader => _messages.Reader;

    /// <summary>Reader drained by the persistence service for diagnostic logs.</summary>
    public ChannelReader<HermexLogEntry> LogReader => _logs.Reader;

    /// <summary>Approximate number of messages waiting to be persisted.</summary>
    public int PendingMessages => _messages.Reader.CanCount ? _messages.Reader.Count : 0;

    /// <inheritdoc />
    public MailSubmissionResult Submit(ReceivedMessage message) =>
        _messages.Writer.TryWrite(message)
            ? MailSubmissionResult.Accepted
            : MailSubmissionResult.QueueFull;

    /// <summary>Enqueues a diagnostic log entry (never blocks, never fails).</summary>
    public void EnqueueLog(HermexLogEntry entry) => _logs.Writer.TryWrite(entry);

    /// <summary>Marks both channels complete so the persistence service can drain and stop.</summary>
    public void Complete()
    {
        _messages.Writer.TryComplete();
        _logs.Writer.TryComplete();
    }
}
