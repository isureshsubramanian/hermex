using Hermex.Diagnostics;
using Hermex.Storage;

namespace Hermex.Smtp;

/// <summary>The outcome of a relay attempt.</summary>
public sealed record MailRelayResult(bool Success, string? Error)
{
    public static MailRelayResult Ok() => new(true, null);
    public static MailRelayResult Fail(string error) => new(false, error);
}

/// <summary>Forwards captured messages to a configured upstream SMTP server.</summary>
public interface IMailRelayService
{
    /// <summary>Whether relay is enabled in configuration.</summary>
    bool IsEnabled { get; }

    /// <summary>Forwards a stored message to the upstream SMTP server.</summary>
    Task<MailRelayResult> RelayAsync(long messageId, CancellationToken cancellationToken = default);
}

/// <summary>Default <see cref="IMailRelayService"/> backed by <see cref="SmtpRelayClient"/>.</summary>
internal sealed class MailRelayService : IMailRelayService
{
    private readonly IMailStore _store;
    private readonly HermexOptions _options;
    private readonly IHermexEventLog _eventLog;

    public MailRelayService(IMailStore store, HermexOptions options, IHermexEventLog eventLog)
    {
        _store = store;
        _options = options;
        _eventLog = eventLog;
    }

    public bool IsEnabled => _options.Relay.Enabled;

    public async Task<MailRelayResult> RelayAsync(long messageId, CancellationToken cancellationToken = default)
    {
        if (!_options.Relay.Enabled)
            return MailRelayResult.Fail("Relay is not enabled.");

        var detail = await _store.GetMessageAsync(messageId, cancellationToken).ConfigureAwait(false);
        if (detail is null)
            return MailRelayResult.Fail($"Message #{messageId} was not found.");

        var raw = await _store.GetRawMessageAsync(messageId, cancellationToken).ConfigureAwait(false);
        if (raw is null)
            return MailRelayResult.Fail($"The raw content of message #{messageId} was not found.");

        var recipients = SplitRecipients(detail.EnvelopeTo);
        if (recipients.Count == 0)
            return MailRelayResult.Fail("The message has no envelope recipients to relay to.");

        try
        {
            await SmtpRelayClient.SendAsync(_options.Relay, detail.EnvelopeFrom, recipients, raw, cancellationToken)
                .ConfigureAwait(false);
            _eventLog.Info("Relay",
                $"Message #{messageId} relayed to {_options.Relay.Host}:{_options.Relay.Port}.");
            return MailRelayResult.Ok();
        }
        catch (Exception ex)
        {
            _eventLog.Error("Relay", $"Failed to relay message #{messageId}: {ex.Message}");
            return MailRelayResult.Fail(ex.Message);
        }
    }

    private static List<string> SplitRecipients(string envelopeTo)
    {
        var result = new List<string>();
        foreach (var part in envelopeTo.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            result.Add(part);
        return result;
    }
}
