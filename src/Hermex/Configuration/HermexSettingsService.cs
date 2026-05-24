using System.Text.Json;
using Hermex.Imap;
using Hermex.Smtp;
using Hermex.Storage;
using Microsoft.Extensions.Logging;

namespace Hermex.Configuration;

/// <summary>The outcome of a settings update.</summary>
internal sealed record SettingsUpdateResult(bool Success, bool ListenerRestarted, string? Error)
{
    public static SettingsUpdateResult Ok(bool restarted) => new(true, restarted, null);
    public static SettingsUpdateResult Invalid(string error) => new(false, false, error);
}

/// <summary>
/// Reads, validates, applies and persists the runtime-editable <see cref="HermexSettings"/>.
/// Port changes re-bind the affected listener live, with no host restart.
/// </summary>
internal sealed class HermexSettingsService
{
    private const string SettingsKey = "hermex.settings";

    private readonly HermexOptions _options;
    private readonly IMailStore _store;
    private readonly SmtpServerService _smtpServer;
    private readonly ImapServerService _imapServer;
    private readonly ILogger<HermexSettingsService> _logger;
    private readonly object _gate = new();

    public HermexSettingsService(
        HermexOptions options,
        IMailStore store,
        SmtpServerService smtpServer,
        ImapServerService imapServer,
        ILogger<HermexSettingsService> logger)
    {
        _options = options;
        _store = store;
        _smtpServer = smtpServer;
        _imapServer = imapServer;
        _logger = logger;
    }

    /// <summary>Returns the current effective settings.</summary>
    public HermexSettings GetCurrent()
    {
        lock (_gate)
        {
            return new HermexSettings
            {
                SmtpPort = _options.SmtpPort,
                ImapPort = _options.ImapPort,
                EnableImap = _options.EnableImap,
                ServerHostName = _options.ServerHostName,
                MaxMessageSizeBytes = _options.MaxMessageSizeBytes,
                RequireAuthentication = _options.RequireAuthentication,
                CaptureSessionTranscript = _options.CaptureSessionTranscript,
                RetentionMaxMessages = _options.RetentionMaxMessages,
                RetentionMaxAgeHours = _options.RetentionMaxAge?.TotalHours ?? 0,
                DashboardTitle = _options.DashboardTitle,
                DefaultTheme = _options.DefaultTheme.ToString(),
                RelayEnabled = _options.Relay.Enabled,
                RelayAutomatic = _options.Relay.AutomaticRelay,
                RelayHost = _options.Relay.Host,
                RelayPort = _options.Relay.Port,
            };
        }
    }

    /// <summary>
    /// Loads persisted settings and applies them to the live options. Called at startup,
    /// before the listeners bind, so no restart is needed.
    /// </summary>
    public async Task LoadPersistedAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var stored = await _store.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
            if (!stored.TryGetValue(SettingsKey, out var json) || string.IsNullOrWhiteSpace(json))
                return;

            var settings = JsonSerializer.Deserialize<HermexSettings>(json);
            if (settings is null || Validate(settings) is not null)
                return;

            lock (_gate)
            {
                ApplyToOptions(settings);
            }
            _logger.LogInformation("Hermex applied persisted runtime settings.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hermex could not load persisted settings; using the configured defaults.");
        }
    }

    /// <summary>Validates, applies and persists settings, re-binding any listener whose port changed.</summary>
    public async Task<SettingsUpdateResult> UpdateAsync(HermexSettings settings,
        CancellationToken cancellationToken = default)
    {
        var error = Validate(settings);
        if (error is not null)
            return SettingsUpdateResult.Invalid(error);

        bool smtpRestart, imapRestart;
        lock (_gate)
        {
            smtpRestart = settings.SmtpPort != _options.SmtpPort;
            imapRestart = settings.ImapPort != _options.ImapPort || settings.EnableImap != _options.EnableImap;
            ApplyToOptions(settings);
        }

        await _store.SaveSettingsAsync(
            new Dictionary<string, string> { [SettingsKey] = JsonSerializer.Serialize(settings) },
            cancellationToken).ConfigureAwait(false);

        if (smtpRestart)
        {
            _logger.LogInformation("Hermex re-binding the SMTP listener after a settings change.");
            _smtpServer.RequestRestart();
        }
        if (imapRestart)
        {
            _logger.LogInformation("Hermex re-binding the IMAP listener after a settings change.");
            _imapServer.RequestRestart();
        }

        return SettingsUpdateResult.Ok(smtpRestart || imapRestart);
    }

    private void ApplyToOptions(HermexSettings settings)
    {
        _options.SmtpPort = settings.SmtpPort;
        _options.ImapPort = settings.ImapPort;
        _options.EnableImap = settings.EnableImap;
        _options.ServerHostName = settings.ServerHostName;
        _options.MaxMessageSizeBytes = settings.MaxMessageSizeBytes;
        _options.RequireAuthentication = settings.RequireAuthentication;
        _options.CaptureSessionTranscript = settings.CaptureSessionTranscript;
        _options.RetentionMaxMessages = settings.RetentionMaxMessages;
        _options.RetentionMaxAge = settings.RetentionMaxAgeHours > 0
            ? TimeSpan.FromHours(settings.RetentionMaxAgeHours)
            : null;
        _options.DashboardTitle = settings.DashboardTitle;
        _options.DefaultTheme = Enum.TryParse<HermexTheme>(settings.DefaultTheme, ignoreCase: true, out var theme)
            ? theme
            : HermexTheme.System;
        _options.Relay.Enabled = settings.RelayEnabled;
        _options.Relay.AutomaticRelay = settings.RelayAutomatic;
        _options.Relay.Host = settings.RelayHost;
        _options.Relay.Port = settings.RelayPort;
    }

    private static string? Validate(HermexSettings s)
    {
        if (s.SmtpPort is < 1 or > 65535)
            return "SMTP port must be between 1 and 65535.";
        if (s.ImapPort is < 1 or > 65535)
            return "IMAP port must be between 1 and 65535.";
        if (s.EnableImap && s.ImapPort == s.SmtpPort)
            return "The SMTP and IMAP ports must be different.";
        if (s.MaxMessageSizeBytes < 1024)
            return "Maximum message size must be at least 1024 bytes.";
        if (s.RetentionMaxMessages < 0)
            return "Retention message count cannot be negative.";
        if (s.RetentionMaxAgeHours < 0)
            return "Retention age cannot be negative.";
        if (string.IsNullOrWhiteSpace(s.ServerHostName))
            return "The server host name is required.";
        if (s.RelayEnabled && string.IsNullOrWhiteSpace(s.RelayHost))
            return "A relay host is required when relay is enabled.";
        if (s.RelayEnabled && s.RelayPort is < 1 or > 65535)
            return "The relay port must be between 1 and 65535.";
        return null;
    }
}
