using Hermex.Smtp;
using Hermex.Storage;
using Xunit;

namespace Hermex.Tests;

public class MailWriteQueueTests
{
    private static ReceivedMessage NewMessage() => new()
    {
        SessionId = "session",
        MailFrom = "a@x.com",
        Recipients = new[] { "b@y.com" },
        RawData = new byte[] { 1, 2, 3 },
        ReceivedAtUtc = DateTimeOffset.UtcNow,
        RemoteEndPoint = "127.0.0.1:5000",
    };

    [Fact]
    public void Submit_accepts_until_capacity_then_reports_queue_full()
    {
        var queue = new MailWriteQueue(new HermexOptions { WriteQueueCapacity = 2 });

        Assert.Equal(MailSubmissionResult.Accepted, queue.Submit(NewMessage()));
        Assert.Equal(MailSubmissionResult.Accepted, queue.Submit(NewMessage()));
        // The bounded queue is full — back-pressure kicks in.
        Assert.Equal(MailSubmissionResult.QueueFull, queue.Submit(NewMessage()));
    }

    [Fact]
    public void Submitted_message_is_readable_from_the_queue()
    {
        var queue = new MailWriteQueue(new HermexOptions { WriteQueueCapacity = 8 });
        queue.Submit(NewMessage());

        Assert.True(queue.MessageReader.TryRead(out var message));
        Assert.Equal("a@x.com", message!.MailFrom);
    }
}
