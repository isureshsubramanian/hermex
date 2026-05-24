using Hermex.Configuration;
using Hermex.Internal;
using Hermex.Realtime;
using Hermex.Smtp;
using Hermex.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Hermex.Dashboard;

/// <summary>
/// Maps the JSON/file API consumed by the dashboard. These minimal-API routes live under
/// <c>/hermex/api</c> and never collide with the host application's own endpoints.
/// </summary>
internal static class HermexApiEndpoints
{
    public static void MapHermexApi(IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup(HermexRoutes.ApiBase);

        // ---- inbox listing -------------------------------------------------
        api.MapGet("/messages", async (
            int? page, int? pageSize, string? search, bool? unread, long? mailbox,
            IMailStore store, HermexOptions options) =>
        {
            var result = await store.GetMessagesAsync(new MessageQuery
            {
                Page = page ?? 1,
                PageSize = pageSize ?? options.DashboardPageSize,
                Search = search,
                UnreadOnly = unread ?? false,
                MailboxId = mailbox,
            });
            return Results.Json(result);
        });

        // ---- mailboxes -----------------------------------------------------
        api.MapGet("/mailboxes", async (IMailStore store) =>
            Results.Json(await store.GetMailboxesAsync()));

        // ---- message detail (also marks the message read) ------------------
        api.MapGet("/messages/{id:long}", async (long id, IMailStore store, IInboxNotifier notifier) =>
        {
            var detail = await store.GetMessageAsync(id);
            if (detail is null)
                return Results.NotFound();

            if (!detail.IsRead)
            {
                await store.SetReadAsync(id, true);
                await notifier.MessageUpdatedAsync(id, true);
            }

            return Results.Json(detail);
        });

        // ---- rendered HTML body (cid: links rewritten to attachment URLs) --
        api.MapGet("/messages/{id:long}/html", async (long id, IMailStore store) =>
        {
            var detail = await store.GetMessageAsync(id);
            if (detail?.HtmlBody is null)
                return Results.NotFound();
            return Results.Content(RewriteCidLinks(id, detail), "text/html; charset=utf-8");
        });

        // ---- raw .eml download --------------------------------------------
        api.MapGet("/messages/{id:long}/raw", async (long id, IMailStore store) =>
        {
            var raw = await store.GetRawMessageAsync(id);
            return raw is null
                ? Results.NotFound()
                : Results.File(raw, "message/rfc822", $"hermex-message-{id}.eml");
        });

        // ---- attachment / inline resource download ------------------------
        api.MapGet("/messages/{id:long}/attachments/{index:int}", async (long id, int index, IMailStore store) =>
        {
            var attachment = await store.GetAttachmentAsync(id, index);
            if (attachment is null)
                return Results.NotFound();

            var contentType = string.IsNullOrWhiteSpace(attachment.Info.ContentType)
                ? "application/octet-stream"
                : attachment.Info.ContentType;
            var fileName = string.IsNullOrWhiteSpace(attachment.Info.FileName)
                ? $"attachment-{index}"
                : attachment.Info.FileName;

            return Results.File(attachment.Content, contentType, fileName);
        });

        // ---- mark read / unread -------------------------------------------
        api.MapPost("/messages/{id:long}/read", async (long id, bool? isRead, IMailStore store, IInboxNotifier notifier) =>
        {
            var target = isRead ?? true;
            if (!await store.SetReadAsync(id, target))
                return Results.NotFound();
            await notifier.MessageUpdatedAsync(id, target);
            return Results.Ok(new { id, isRead = target });
        });

        // ---- relay a message to the configured upstream SMTP server -------
        api.MapPost("/messages/{id:long}/relay", async (long id, IMailRelayService relay) =>
        {
            if (!relay.IsEnabled)
                return Results.BadRequest(new { error = "Relay is not enabled." });

            var result = await relay.RelayAsync(id);
            return result.Success
                ? Results.Ok(new { id, relayed = true })
                : Results.Problem(result.Error ?? "Relay failed.");
        });

        // ---- delete a single message --------------------------------------
        api.MapDelete("/messages/{id:long}", async (long id, IMailStore store, IInboxNotifier notifier) =>
        {
            if (!await store.DeleteMessageAsync(id))
                return Results.NotFound();
            await notifier.MessageDeletedAsync(id);
            await notifier.StatsChangedAsync(await store.GetStatsAsync());
            return Results.Ok(new { id });
        });

        // ---- clear the whole inbox ----------------------------------------
        api.MapDelete("/messages", async (IMailStore store, IInboxNotifier notifier) =>
        {
            var cleared = await store.ClearMessagesAsync();
            await notifier.InboxClearedAsync();
            await notifier.StatsChangedAsync(await store.GetStatsAsync());
            return Results.Ok(new { cleared });
        });

        // ---- server status, statistics and effective options --------------
        api.MapGet("/status", async (IMailStore store, HermexRuntimeState state, HermexOptions options) =>
        {
            var stats = await store.GetStatsAsync();
            return Results.Json(new
            {
                server = new
                {
                    status = state.Status.ToString(),
                    configuredPort = state.ConfiguredPort,
                    listeningPort = state.ListeningPort,
                    listenAddress = state.ListenAddress,
                    host = state.ServerHostName,
                    imapEnabled = state.ImapEnabled,
                    imapStatus = state.ImapStatus.ToString(),
                    imapListeningPort = state.ImapListeningPort,
                    startedAtUtc = state.StartedAtUtc,
                    uptimeSeconds = (long)state.Uptime.TotalSeconds,
                    databasePath = state.DatabasePath,
                    lastError = state.LastError,
                    acceptedConnections = state.AcceptedConnections,
                    activeConnections = state.ActiveConnections,
                    messagesReceived = state.MessagesReceived,
                    messagesRejected = state.MessagesRejected,
                    bytesReceived = state.BytesReceived,
                },
                stats,
                options = new
                {
                    maxMessageSizeBytes = options.MaxMessageSizeBytes,
                    maxConcurrentConnections = options.MaxConcurrentConnections,
                    retentionMaxMessages = options.RetentionMaxMessages,
                    requireAuthentication = options.RequireAuthentication,
                    tlsMode = options.TlsMode.ToString(),
                    relayEnabled = options.Relay.Enabled,
                    enableImap = options.EnableImap,
                    imapPort = options.ImapPort,
                },
            });
        });

        // ---- diagnostic logs ----------------------------------------------
        api.MapGet("/logs", async (int? limit, IMailStore store) =>
        {
            var logs = await store.GetLogsAsync(limit ?? 200);
            return Results.Json(logs);
        });

        api.MapDelete("/logs", async (IMailStore store) =>
        {
            var cleared = await store.ClearLogsAsync();
            return Results.Ok(new { cleared });
        });

        // ---- runtime-editable settings ------------------------------------
        api.MapGet("/settings", (HermexSettingsService settings) =>
            Results.Json(settings.GetCurrent()));

        api.MapPut("/settings", async (HermexSettings update, HermexSettingsService settings) =>
        {
            var result = await settings.UpdateAsync(update);
            return result.Success
                ? Results.Ok(new { saved = true, listenerRestarted = result.ListenerRestarted })
                : Results.BadRequest(new { error = result.Error });
        });
    }

    /// <summary>Rewrites <c>cid:</c> references in an HTML body to inline-attachment URLs.</summary>
    private static string RewriteCidLinks(long messageId, MessageDetail detail)
    {
        var html = detail.HtmlBody ?? string.Empty;
        if (html.Length == 0)
            return html;

        for (var i = 0; i < detail.Attachments.Count; i++)
        {
            var contentId = detail.Attachments[i].ContentId;
            if (string.IsNullOrEmpty(contentId))
                continue;

            var url = $"{HermexRoutes.ApiBase}/messages/{messageId}/attachments/{i}";
            html = html.Replace("cid:" + contentId, url, StringComparison.OrdinalIgnoreCase);
        }

        return html;
    }
}
