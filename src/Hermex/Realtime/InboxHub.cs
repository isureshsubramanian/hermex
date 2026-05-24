using Microsoft.AspNetCore.SignalR;

namespace Hermex.Realtime;

/// <summary>
/// SignalR hub that pushes live inbox updates to the dashboard. The dashboard is a pure
/// subscriber — all server-to-client events are raised through <see cref="InboxNotifier"/>.
/// </summary>
public sealed class InboxHub : Hub
{
    /// <summary>Client method names broadcast by the server.</summary>
    public static class Events
    {
        public const string MessageReceived = "messageReceived";
        public const string MessageUpdated = "messageUpdated";
        public const string MessageDeleted = "messageDeleted";
        public const string InboxCleared = "inboxCleared";
        public const string StatsChanged = "statsChanged";
        public const string LogAdded = "logAdded";
    }
}
