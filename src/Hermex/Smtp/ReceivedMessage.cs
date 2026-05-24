namespace Hermex.Smtp;

/// <summary>
/// A message captured by the SMTP server, carrying the raw payload and envelope. The
/// expensive work (MIME parsing, persistence) happens later, off the SMTP path.
/// </summary>
public sealed class ReceivedMessage
{
    /// <summary>Identifier of the SMTP session that delivered the message.</summary>
    public required string SessionId { get; init; }

    /// <summary>The SMTP <c>MAIL FROM</c> envelope sender (empty for the null sender).</summary>
    public required string MailFrom { get; init; }

    /// <summary>The SMTP <c>RCPT TO</c> envelope recipients.</summary>
    public required IReadOnlyList<string> Recipients { get; init; }

    /// <summary>The raw DATA payload — headers and body, dot-unstuffed, exactly as received.</summary>
    public required byte[] RawData { get; init; }

    /// <summary>When the message was accepted (UTC).</summary>
    public required DateTimeOffset ReceivedAtUtc { get; init; }

    /// <summary>The remote endpoint that sent the message.</summary>
    public required string RemoteEndPoint { get; init; }

    /// <summary>The name the client announced through HELO/EHLO, if any.</summary>
    public string? ClientId { get; init; }

    /// <summary>The user name supplied via AUTH, if the session authenticated.</summary>
    public string? AuthenticatedUser { get; init; }

    /// <summary>Whether the message was delivered over a TLS-secured connection.</summary>
    public bool SecuredWithTls { get; init; }

    /// <summary>The SMTP command/response transcript of the delivering session, if captured.</summary>
    public string? Transcript { get; init; }
}

/// <summary>Outcome of handing a <see cref="ReceivedMessage"/> to the ingestion pipeline.</summary>
public enum MailSubmissionResult
{
    /// <summary>The message was queued for storage.</summary>
    Accepted = 0,
    /// <summary>The ingestion queue is full; the client should retry later.</summary>
    QueueFull = 1,
    /// <summary>The message was rejected outright.</summary>
    Rejected = 2,
}

/// <summary>
/// Receives messages captured by the SMTP server. Implemented by the write queue so the
/// SMTP layer never touches storage directly.
/// </summary>
public interface IReceivedMailSink
{
    /// <summary>
    /// Hands a captured message to the ingestion pipeline. Must be fast and non-blocking —
    /// it is called on the SMTP connection thread.
    /// </summary>
    MailSubmissionResult Submit(ReceivedMessage message);
}
