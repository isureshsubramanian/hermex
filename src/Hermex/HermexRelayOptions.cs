namespace Hermex;

/// <summary>
/// Configures forwarding of captured messages to a real upstream SMTP server. Relay is off
/// by default — Hermex only captures unless this is explicitly enabled.
/// </summary>
public sealed class HermexRelayOptions
{
    /// <summary>Enables the relay feature (the dashboard "Relay" action and the relay API).</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// When <c>true</c>, every captured message is forwarded automatically as soon as it is
    /// stored. When <c>false</c>, messages are only relayed on demand from the dashboard.
    /// </summary>
    public bool AutomaticRelay { get; set; }

    /// <summary>Upstream SMTP host.</summary>
    public string Host { get; set; } = "localhost";

    /// <summary>Upstream SMTP port. Default <c>25</c>.</summary>
    public int Port { get; set; } = 25;

    /// <summary>Upgrade the upstream connection with <c>STARTTLS</c> when offered.</summary>
    public bool UseStartTls { get; set; }

    /// <summary>Optional upstream AUTH user name.</summary>
    public string? Username { get; set; }

    /// <summary>Optional upstream AUTH password.</summary>
    public string? Password { get; set; }

    /// <summary>Connection / command timeout for the upstream conversation. Default 30 seconds.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Validates the relay configuration.</summary>
    public void Validate()
    {
        if (!Enabled)
            return;

        if (string.IsNullOrWhiteSpace(Host))
            throw new HermexConfigurationException("HermexRelayOptions.Host cannot be empty when relay is enabled.");
        if (Port is < 1 or > 65535)
            throw new HermexConfigurationException($"HermexRelayOptions.Port must be between 1 and 65535 (was {Port}).");
        if (Timeout <= TimeSpan.Zero)
            throw new HermexConfigurationException("HermexRelayOptions.Timeout must be greater than zero.");
    }
}
