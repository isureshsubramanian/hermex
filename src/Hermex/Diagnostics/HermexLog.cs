namespace Hermex.Diagnostics;

/// <summary>Severity of a Hermex diagnostic log entry.</summary>
public enum HermexLogLevel
{
    /// <summary>Verbose protocol-level detail.</summary>
    Debug = 0,
    /// <summary>Normal operational events.</summary>
    Info = 1,
    /// <summary>Recoverable problems worth attention.</summary>
    Warning = 2,
    /// <summary>Failures.</summary>
    Error = 3,
}

/// <summary>A single diagnostic event surfaced on the dashboard "Logs" page.</summary>
public sealed class HermexLogEntry
{
    /// <summary>Database identity (0 until persisted).</summary>
    public long Id { get; set; }

    /// <summary>When the event occurred (UTC).</summary>
    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Severity.</summary>
    public HermexLogLevel Level { get; set; } = HermexLogLevel.Info;

    /// <summary>A short category such as <c>Smtp</c>, <c>Server</c>, <c>Storage</c> or <c>Retention</c>.</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>The human-readable message.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>The SMTP session this event belongs to, if any.</summary>
    public string? SessionId { get; set; }

    /// <summary>The remote endpoint involved, if any.</summary>
    public string? RemoteEndPoint { get; set; }
}

/// <summary>
/// Records Hermex diagnostic events so they appear, in real time, on the dashboard's
/// Logs page. Implementations must be cheap and non-blocking — the SMTP path calls them.
/// </summary>
public interface IHermexEventLog
{
    /// <summary>Records a diagnostic event.</summary>
    void Write(HermexLogLevel level, string category, string message,
        string? sessionId = null, string? remoteEndPoint = null);
}

/// <summary>Convenience helpers over <see cref="IHermexEventLog"/>.</summary>
public static class HermexEventLogExtensions
{
    /// <summary>Records a <see cref="HermexLogLevel.Debug"/> event.</summary>
    public static void Debug(this IHermexEventLog log, string category, string message,
        string? sessionId = null, string? remoteEndPoint = null) =>
        log.Write(HermexLogLevel.Debug, category, message, sessionId, remoteEndPoint);

    /// <summary>Records an <see cref="HermexLogLevel.Info"/> event.</summary>
    public static void Info(this IHermexEventLog log, string category, string message,
        string? sessionId = null, string? remoteEndPoint = null) =>
        log.Write(HermexLogLevel.Info, category, message, sessionId, remoteEndPoint);

    /// <summary>Records a <see cref="HermexLogLevel.Warning"/> event.</summary>
    public static void Warning(this IHermexEventLog log, string category, string message,
        string? sessionId = null, string? remoteEndPoint = null) =>
        log.Write(HermexLogLevel.Warning, category, message, sessionId, remoteEndPoint);

    /// <summary>Records an <see cref="HermexLogLevel.Error"/> event.</summary>
    public static void Error(this IHermexEventLog log, string category, string message,
        string? sessionId = null, string? remoteEndPoint = null) =>
        log.Write(HermexLogLevel.Error, category, message, sessionId, remoteEndPoint);
}
