namespace Hermex;

/// <summary>Lifecycle status of the embedded SMTP server.</summary>
public enum HermexServerStatus
{
    /// <summary>The server has not started yet.</summary>
    Stopped = 0,
    /// <summary>The server is binding its listener.</summary>
    Starting = 1,
    /// <summary>The server is accepting connections.</summary>
    Listening = 2,
    /// <summary>The server could not bind to any port.</summary>
    PortConflict = 3,
    /// <summary>The server stopped because of an unexpected error.</summary>
    Faulted = 4,
}

/// <summary>
/// Live, observable state of the running Hermex server. Registered as a singleton so
/// the dashboard, API and diagnostics can all read a single source of truth. Counters are
/// updated with <see cref="System.Threading.Interlocked"/> and are safe to read at any time.
/// </summary>
public sealed class HermexRuntimeState
{
    private long _acceptedConnections;
    private long _activeConnections;
    private long _messagesReceived;
    private long _messagesRejected;
    private long _bytesReceived;

    /// <summary>Current lifecycle status.</summary>
    public HermexServerStatus Status { get; internal set; } = HermexServerStatus.Stopped;

    /// <summary>The port requested through configuration.</summary>
    public int ConfiguredPort { get; internal set; }

    /// <summary>The port the listener actually bound to (may differ if a conflict was auto-resolved).</summary>
    public int? ListeningPort { get; internal set; }

    /// <summary>The interface the listener is bound to.</summary>
    public string ListenAddress { get; internal set; } = string.Empty;

    /// <summary>The SMTP greeting host name.</summary>
    public string ServerHostName { get; internal set; } = string.Empty;

    /// <summary>UTC timestamp at which the listener started accepting connections.</summary>
    public DateTimeOffset? StartedAtUtc { get; internal set; }

    /// <summary>Absolute path of the SQLite database backing the mail store.</summary>
    public string DatabasePath { get; internal set; } = string.Empty;

    /// <summary>The most recent fatal error message, if the server faulted.</summary>
    public string? LastError { get; internal set; }

    /// <summary>Whether the IMAP server is enabled.</summary>
    public bool ImapEnabled { get; internal set; }

    /// <summary>Lifecycle status of the IMAP server.</summary>
    public HermexServerStatus ImapStatus { get; internal set; } = HermexServerStatus.Stopped;

    /// <summary>The port the IMAP listener is bound to, when running.</summary>
    public int? ImapListeningPort { get; internal set; }

    /// <summary>Total SMTP connections accepted since start.</summary>
    public long AcceptedConnections => Interlocked.Read(ref _acceptedConnections);

    /// <summary>SMTP connections currently being served.</summary>
    public long ActiveConnections => Interlocked.Read(ref _activeConnections);

    /// <summary>Total messages successfully received and queued for storage.</summary>
    public long MessagesReceived => Interlocked.Read(ref _messagesReceived);

    /// <summary>Total messages rejected (oversize, protocol error, etc.).</summary>
    public long MessagesRejected => Interlocked.Read(ref _messagesRejected);

    /// <summary>Total raw message bytes received.</summary>
    public long BytesReceived => Interlocked.Read(ref _bytesReceived);

    /// <summary>Server uptime, or <see cref="TimeSpan.Zero"/> when not running.</summary>
    public TimeSpan Uptime => StartedAtUtc is { } started
        ? DateTimeOffset.UtcNow - started
        : TimeSpan.Zero;

    internal void OnConnectionAccepted()
    {
        Interlocked.Increment(ref _acceptedConnections);
        Interlocked.Increment(ref _activeConnections);
    }

    internal void OnConnectionClosed() => Interlocked.Decrement(ref _activeConnections);

    internal void OnMessageReceived(long bytes)
    {
        Interlocked.Increment(ref _messagesReceived);
        Interlocked.Add(ref _bytesReceived, bytes);
    }

    internal void OnMessageRejected() => Interlocked.Increment(ref _messagesRejected);
}
