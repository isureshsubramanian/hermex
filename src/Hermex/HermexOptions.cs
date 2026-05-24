using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace Hermex;

/// <summary>Theme applied to the dashboard on first load (the user can switch it at runtime).</summary>
public enum HermexTheme
{
    /// <summary>Follow the operating system / browser preference.</summary>
    System = 0,
    /// <summary>Light theme.</summary>
    Light = 1,
    /// <summary>Dark theme.</summary>
    Dark = 2,
}

/// <summary>Transport security mode for the SMTP listener.</summary>
public enum HermexTlsMode
{
    /// <summary>Plain text only (default for a local development tool).</summary>
    None = 0,
    /// <summary>Plain text initially; the client may upgrade with the <c>STARTTLS</c> command.</summary>
    StartTls = 1,
    /// <summary>The whole connection is TLS from the first byte (implicit TLS, e.g. port 465).</summary>
    Implicit = 2,
}

/// <summary>
/// Configuration for the in-process Hermex SMTP server and dashboard.
/// Configure it through the <c>AddMail4Dev(options =&gt; ...)</c> callback.
/// </summary>
public sealed class HermexOptions
{
    // ----------------------------------------------------------------- SMTP listener

    /// <summary>The TCP port the SMTP server listens on. Default <c>2525</c>.</summary>
    public int SmtpPort { get; set; } = 2525;

    /// <summary>
    /// The interface to bind to. Accepts <c>"loopback"</c>/<c>"127.0.0.1"</c> (default),
    /// <c>"any"</c>/<c>"0.0.0.0"</c>, or any explicit IPv4/IPv6 address.
    /// Loopback is the default because Hermex is a development tool and should not be
    /// exposed to the network.
    /// </summary>
    public string ListenAddress { get; set; } = "loopback";

    /// <summary>The host name announced in the SMTP greeting banner and EHLO reply.</summary>
    public string ServerHostName { get; set; } = "hermex.local";

    /// <summary>
    /// When <c>true</c> (default) and the configured port is already in use, Hermex
    /// probes subsequent ports until a free one is found. When <c>false</c> a
    /// <see cref="HermexPortConflictException"/> is thrown instead.
    /// </summary>
    public bool AutoResolvePortConflict { get; set; } = true;

    /// <summary>How many consecutive ports to probe when resolving a conflict. Default <c>25</c>.</summary>
    public int MaxPortProbeAttempts { get; set; } = 25;

    /// <summary>Maximum number of SMTP connections served concurrently. Default <c>64</c>.</summary>
    public int MaxConcurrentConnections { get; set; } = 64;

    /// <summary>Largest message accepted, in bytes. Default 25 MB. Advertised through the SMTP <c>SIZE</c> extension.</summary>
    public int MaxMessageSizeBytes { get; set; } = 25 * 1024 * 1024;

    /// <summary>Idle timeout for a single SMTP connection. Default 2 minutes.</summary>
    public TimeSpan SessionTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// When <c>true</c> the server advertises AUTH and requires the client to authenticate.
    /// Because Hermex is a development tool, <em>any</em> credentials are accepted — this
    /// switch exists only so applications configured to send authenticated mail still work.
    /// Default <c>false</c>.
    /// </summary>
    public bool RequireAuthentication { get; set; }

    /// <summary>
    /// Records the SMTP command/response conversation for each message so it can be reviewed
    /// on the dashboard. Default <c>true</c>.
    /// </summary>
    public bool CaptureSessionTranscript { get; set; } = true;

    // ----------------------------------------------------------------- Transport security

    /// <summary>Transport security mode for the SMTP listener. Default <see cref="HermexTlsMode.None"/>.</summary>
    public HermexTlsMode TlsMode { get; set; } = HermexTlsMode.None;

    /// <summary>
    /// The certificate presented for STARTTLS / implicit TLS. When <c>null</c> and
    /// <see cref="GenerateSelfSignedCertificate"/> is enabled, a self-signed certificate is
    /// generated at startup.
    /// </summary>
    public X509Certificate2? TlsCertificate { get; set; }

    /// <summary>
    /// Generate a self-signed certificate when TLS is enabled and no <see cref="TlsCertificate"/>
    /// was supplied. Default <c>true</c>.
    /// </summary>
    public bool GenerateSelfSignedCertificate { get; set; } = true;

    // ----------------------------------------------------------------- IMAP

    /// <summary>
    /// Enables the in-process IMAP server, letting a real mail client (Outlook, Thunderbird,
    /// Apple Mail) browse captured mail. Each recipient mailbox appears as an IMAP folder.
    /// Default <c>false</c>.
    /// </summary>
    public bool EnableImap { get; set; }

    /// <summary>The TCP port for the IMAP server. Default <c>1143</c> (port 143 requires elevation).</summary>
    public int ImapPort { get; set; } = 1143;

    // ----------------------------------------------------------------- Relay

    /// <summary>Optional forwarding of captured mail to a real upstream SMTP server.</summary>
    public HermexRelayOptions Relay { get; } = new();

    // ----------------------------------------------------------------- Storage

    /// <summary>
    /// Absolute path to the SQLite database file. When <c>null</c> (default) Hermex uses
    /// <c>{ContentRoot}/hermex/hermex.db</c>.
    /// </summary>
    public string? DatabasePath { get; set; }

    /// <summary>
    /// Maximum number of messages flushed to SQLite in a single transaction. Larger batches
    /// improve throughput for bursts of mail (e.g. marketing sends). Default <c>64</c>.
    /// </summary>
    public int PersistenceBatchSize { get; set; } = 64;

    /// <summary>
    /// Maximum time a received message waits in the in-memory queue before being flushed,
    /// even if a full batch has not accumulated. Default 250 ms.
    /// </summary>
    public TimeSpan PersistenceFlushInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Capacity of the in-memory write queue that decouples SMTP acceptance from disk I/O.
    /// This is the burst of messages that can be absorbed before back-pressure applies.
    /// Default <c>10000</c>.
    /// </summary>
    public int WriteQueueCapacity { get; set; } = 10_000;

    // ----------------------------------------------------------------- Retention

    /// <summary>
    /// Keep at most this many messages; the oldest are pruned beyond the cap.
    /// Set to <c>0</c> to disable count-based retention. Default <c>5000</c>.
    /// </summary>
    public int RetentionMaxMessages { get; set; } = 5_000;

    /// <summary>Delete messages older than this age. <c>null</c> (default) disables age-based retention.</summary>
    public TimeSpan? RetentionMaxAge { get; set; }

    /// <summary>How often the retention sweep runs. Default 5 minutes.</summary>
    public TimeSpan RetentionSweepInterval { get; set; } = TimeSpan.FromMinutes(5);

    // ----------------------------------------------------------------- Dashboard

    /// <summary>Whether the web dashboard, API and SignalR hub are wired up by <c>UseMail4Dev()</c>. Default <c>true</c>.</summary>
    public bool EnableDashboard { get; set; } = true;

    /// <summary>The product name shown in the dashboard header and browser title.</summary>
    public string DashboardTitle { get; set; } = "Hermex";

    /// <summary>Theme applied on first load. The user can switch and the choice is remembered in the browser.</summary>
    public HermexTheme DefaultTheme { get; set; } = HermexTheme.System;

    /// <summary>Number of messages shown per page in the dashboard inbox. Default <c>50</c>.</summary>
    public int DashboardPageSize { get; set; } = 50;

    // ----------------------------------------------------------------- Helpers

    /// <summary>Resolves <see cref="ListenAddress"/> into an <see cref="IPAddress"/>.</summary>
    public IPAddress ResolveListenAddress()
    {
        var value = (ListenAddress ?? string.Empty).Trim();
        return value.ToLowerInvariant() switch
        {
            "" or "loopback" or "localhost" => IPAddress.Loopback,
            "any" or "all" => IPAddress.Any,
            "ipv6any" or "ipv6loopback" => IPAddress.IPv6Loopback,
            _ => IPAddress.TryParse(value, out var parsed)
                ? parsed
                : throw new HermexConfigurationException($"HermexOptions.ListenAddress '{ListenAddress}' is not a valid IP address.")
        };
    }

    /// <summary>Validates the option values and throws <see cref="HermexConfigurationException"/> on the first problem.</summary>
    public void Validate()
    {
        if (SmtpPort is < 1 or > 65535)
            throw new HermexConfigurationException($"HermexOptions.SmtpPort must be between 1 and 65535 (was {SmtpPort}).");
        if (MaxPortProbeAttempts < 1)
            throw new HermexConfigurationException("HermexOptions.MaxPortProbeAttempts must be at least 1.");
        if (MaxConcurrentConnections < 1)
            throw new HermexConfigurationException("HermexOptions.MaxConcurrentConnections must be at least 1.");
        if (MaxMessageSizeBytes < 1024)
            throw new HermexConfigurationException("HermexOptions.MaxMessageSizeBytes must be at least 1024 bytes.");
        if (PersistenceBatchSize < 1)
            throw new HermexConfigurationException("HermexOptions.PersistenceBatchSize must be at least 1.");
        if (PersistenceFlushInterval <= TimeSpan.Zero)
            throw new HermexConfigurationException("HermexOptions.PersistenceFlushInterval must be greater than zero.");
        if (WriteQueueCapacity < 1)
            throw new HermexConfigurationException("HermexOptions.WriteQueueCapacity must be at least 1.");
        if (RetentionMaxMessages < 0)
            throw new HermexConfigurationException("HermexOptions.RetentionMaxMessages cannot be negative.");
        if (RetentionSweepInterval <= TimeSpan.Zero)
            throw new HermexConfigurationException("HermexOptions.RetentionSweepInterval must be greater than zero.");
        if (DashboardPageSize is < 1 or > 500)
            throw new HermexConfigurationException("HermexOptions.DashboardPageSize must be between 1 and 500.");
        if (string.IsNullOrWhiteSpace(ServerHostName))
            throw new HermexConfigurationException("HermexOptions.ServerHostName cannot be empty.");
        if (TlsMode != HermexTlsMode.None && TlsCertificate is null && !GenerateSelfSignedCertificate)
        {
            throw new HermexConfigurationException(
                "HermexOptions.TlsMode requires either a TlsCertificate or GenerateSelfSignedCertificate = true.");
        }
        if (EnableImap && ImapPort is < 1 or > 65535)
            throw new HermexConfigurationException($"HermexOptions.ImapPort must be between 1 and 65535 (was {ImapPort}).");
        if (EnableImap && ImapPort == SmtpPort)
            throw new HermexConfigurationException("HermexOptions.ImapPort and HermexOptions.SmtpPort must differ.");

        Relay.Validate();

        // ResolveListenAddress throws if the address is malformed.
        _ = ResolveListenAddress();
    }
}
