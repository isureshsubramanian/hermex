namespace Hermex;

/// <summary>
/// The runtime-editable settings surface. These are the values the dashboard Settings page
/// can change; they are persisted in the SQLite database and restored on the next start.
/// </summary>
public sealed class HermexSettings
{
    /// <summary>The SMTP listener port. Changing this re-binds the SMTP server live.</summary>
    public int SmtpPort { get; set; } = 2525;

    /// <summary>The IMAP listener port. Changing this re-binds the IMAP server live.</summary>
    public int ImapPort { get; set; } = 1143;

    /// <summary>Whether the IMAP server is enabled.</summary>
    public bool EnableImap { get; set; }

    /// <summary>The SMTP greeting host name.</summary>
    public string ServerHostName { get; set; } = "hermex.local";

    /// <summary>Largest accepted message, in bytes.</summary>
    public int MaxMessageSizeBytes { get; set; } = 25 * 1024 * 1024;

    /// <summary>Whether SMTP authentication is required (any credentials are accepted).</summary>
    public bool RequireAuthentication { get; set; }

    /// <summary>Whether the SMTP session transcript is captured per message.</summary>
    public bool CaptureSessionTranscript { get; set; } = true;

    /// <summary>Keep at most this many messages (0 disables count-based retention).</summary>
    public int RetentionMaxMessages { get; set; } = 5_000;

    /// <summary>Delete messages older than this many hours (0 disables age-based retention).</summary>
    public double RetentionMaxAgeHours { get; set; }

    /// <summary>The dashboard product title.</summary>
    public string DashboardTitle { get; set; } = "Hermex";

    /// <summary>The default dashboard theme: <c>System</c>, <c>Light</c> or <c>Dark</c>.</summary>
    public string DefaultTheme { get; set; } = "System";

    /// <summary>Whether upstream relay is enabled.</summary>
    public bool RelayEnabled { get; set; }

    /// <summary>Whether every captured message is relayed automatically.</summary>
    public bool RelayAutomatic { get; set; }

    /// <summary>The upstream relay host.</summary>
    public string RelayHost { get; set; } = "localhost";

    /// <summary>The upstream relay port.</summary>
    public int RelayPort { get; set; } = 25;
}
