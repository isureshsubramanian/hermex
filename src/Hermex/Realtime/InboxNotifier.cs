using Hermex.Diagnostics;
using Hermex.Storage;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Hermex.Realtime;

/// <summary>Broadcasts live dashboard events to connected SignalR clients.</summary>
public interface IInboxNotifier
{
    /// <summary>A new message was stored.</summary>
    Task MessageReceivedAsync(MessageSummary summary);

    /// <summary>A message's read state changed.</summary>
    Task MessageUpdatedAsync(long id, bool isRead);

    /// <summary>A message was deleted.</summary>
    Task MessageDeletedAsync(long id);

    /// <summary>Every message was deleted.</summary>
    Task InboxClearedAsync();

    /// <summary>Aggregate statistics changed.</summary>
    Task StatsChangedAsync(MailStats stats);

    /// <summary>A diagnostic log entry was recorded.</summary>
    Task LogAddedAsync(HermexLogEntry entry);
}

/// <summary>
/// <see cref="IInboxNotifier"/> backed by SignalR. Every broadcast is best-effort: realtime
/// delivery problems must never disrupt mail capture or persistence.
/// </summary>
internal sealed class InboxNotifier : IInboxNotifier
{
    private readonly IHubContext<InboxHub> _hub;
    private readonly ILogger<InboxNotifier> _logger;

    public InboxNotifier(IHubContext<InboxHub> hub, ILogger<InboxNotifier> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    public Task MessageReceivedAsync(MessageSummary summary) =>
        SafeSendAsync(InboxHub.Events.MessageReceived, summary);

    public Task MessageUpdatedAsync(long id, bool isRead) =>
        SafeSendAsync(InboxHub.Events.MessageUpdated, new { id, isRead });

    public Task MessageDeletedAsync(long id) =>
        SafeSendAsync(InboxHub.Events.MessageDeleted, new { id });

    public Task InboxClearedAsync() =>
        SafeSendAsync(InboxHub.Events.InboxCleared, new { });

    public Task StatsChangedAsync(MailStats stats) =>
        SafeSendAsync(InboxHub.Events.StatsChanged, stats);

    public Task LogAddedAsync(HermexLogEntry entry) =>
        SafeSendAsync(InboxHub.Events.LogAdded, entry);

    private async Task SafeSendAsync(string eventName, object payload)
    {
        try
        {
            await _hub.Clients.All.SendAsync(eventName, payload).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Hermex: failed to broadcast '{Event}'.", eventName);
        }
    }
}
