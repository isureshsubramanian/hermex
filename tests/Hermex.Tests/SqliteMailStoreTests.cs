using Hermex.Diagnostics;
using Hermex.Storage;
using Xunit;

namespace Hermex.Tests;

public class SqliteMailStoreTests
{
    private static MessageRecord BuildRecord(string subject, string body, params string[] recipients)
    {
        var raw = TestSupport.Raw(
            "From: sender@example.com\n" +
            "To: recipient@example.com\n" +
            "Subject: " + subject + "\n" +
            "\n" + body + "\n");

        return new MessageRecord
        {
            SessionId = "session-1",
            ReceivedAtUtc = DateTimeOffset.UtcNow,
            EnvelopeFrom = "sender@example.com",
            EnvelopeTo = "recipient@example.com",
            FromAddress = "sender@example.com",
            FromDisplay = "sender@example.com",
            ToDisplay = "recipient@example.com",
            Subject = subject,
            HasText = true,
            RawSize = raw.Length,
            RemoteEndPoint = "127.0.0.1:50000",
            Recipients = recipients,
            RawData = raw,
        };
    }

    [Fact]
    public async Task Add_and_retrieve_message_round_trips()
    {
        await using var context = await TestStore.CreateAsync();
        var record = BuildRecord("Round trip", "Hello stored world");

        await context.Store.AddMessagesAsync(new[] { record });
        Assert.True(record.Id > 0);

        var page = await context.Store.GetMessagesAsync(new MessageQuery());
        Assert.Equal(1, page.TotalCount);
        Assert.Equal("Round trip", page.Items[0].Subject);

        var detail = await context.Store.GetMessageAsync(record.Id);
        Assert.NotNull(detail);
        Assert.Equal("Round trip", detail!.Subject);
        Assert.Contains("Hello stored world", detail.TextBody);
    }

    [Fact]
    public async Task Search_filters_by_subject()
    {
        await using var context = await TestStore.CreateAsync();
        await context.Store.AddMessagesAsync(new[]
        {
            BuildRecord("Invoice 2026", "body"),
            BuildRecord("Weekly newsletter", "body"),
        });

        var result = await context.Store.GetMessagesAsync(new MessageQuery { Search = "invoice" });

        Assert.Equal(1, result.TotalCount);
        Assert.Equal("Invoice 2026", result.Items[0].Subject);
    }

    [Fact]
    public async Task Mark_read_then_delete()
    {
        await using var context = await TestStore.CreateAsync();
        var record = BuildRecord("Readable", "body");
        await context.Store.AddMessagesAsync(new[] { record });

        Assert.True(await context.Store.SetReadAsync(record.Id, true));
        var detail = await context.Store.GetMessageAsync(record.Id);
        Assert.True(detail!.IsRead);

        Assert.True(await context.Store.DeleteMessageAsync(record.Id));
        Assert.Null(await context.Store.GetMessageAsync(record.Id));
    }

    [Fact]
    public async Task Retention_prunes_oldest_messages()
    {
        await using var context = await TestStore.CreateAsync(o => o.RetentionMaxMessages = 3);
        for (var i = 0; i < 8; i++)
            await context.Store.AddMessagesAsync(new[] { BuildRecord("Message " + i, "body") });

        var pruned = await context.Store.ApplyRetentionAsync();

        Assert.Equal(5, pruned);
        var stats = await context.Store.GetStatsAsync();
        Assert.Equal(3, stats.TotalMessages);
    }

    [Fact]
    public async Task Persists_a_burst_in_a_single_batch()
    {
        await using var context = await TestStore.CreateAsync();
        var batch = Enumerable.Range(0, 250)
            .Select(i => BuildRecord("Bulk " + i, "marketing body " + i))
            .ToArray();

        await context.Store.AddMessagesAsync(batch);

        var stats = await context.Store.GetStatsAsync();
        Assert.Equal(250, stats.TotalMessages);
    }

    [Fact]
    public async Task Logs_round_trip()
    {
        await using var context = await TestStore.CreateAsync();
        await context.Store.AddLogsAsync(new[]
        {
            new HermexLogEntry { Level = HermexLogLevel.Info, Category = "Test", Message = "first" },
            new HermexLogEntry { Level = HermexLogLevel.Error, Category = "Test", Message = "second" },
        });

        var logs = await context.Store.GetLogsAsync(100);

        Assert.Equal(2, logs.Count);
    }

    [Fact]
    public async Task Messages_route_to_inbox_and_recipient_mailboxes()
    {
        await using var context = await TestStore.CreateAsync();
        await context.Store.AddMessagesAsync(new[]
        {
            BuildRecord("Hello", "body", "alice@example.com", "bob@example.com"),
        });

        var mailboxes = await context.Store.GetMailboxesAsync();
        Assert.Contains(mailboxes, m => m.Address == "INBOX");
        Assert.Contains(mailboxes, m => m.Address == "alice@example.com");
        Assert.Contains(mailboxes, m => m.Address == "bob@example.com");

        var alice = mailboxes.First(m => m.Address == "alice@example.com");
        Assert.Equal(1, alice.MessageCount);

        var aliceMessages = await context.Store.GetMailboxMessagesAsync(alice.Id);
        Assert.Single(aliceMessages);

        // Filtering the inbox listing by mailbox returns only that mailbox's messages.
        var filtered = await context.Store.GetMessagesAsync(new MessageQuery { MailboxId = alice.Id });
        Assert.Equal(1, filtered.TotalCount);
    }

    [Fact]
    public async Task Settings_round_trip()
    {
        await using var context = await TestStore.CreateAsync();
        await context.Store.SaveSettingsAsync(new Dictionary<string, string>
        {
            ["hermex.settings"] = "{\"SmtpPort\":2599}",
        });

        var settings = await context.Store.GetSettingsAsync();

        Assert.Equal("{\"SmtpPort\":2599}", settings["hermex.settings"]);
    }
}
