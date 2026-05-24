using System.Net.Mail;
using System.Net.Mime;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hermex.Sample.ConsoleApp;

/// <summary>Sends a few representative messages shortly after the host starts.</summary>
internal sealed class SampleMailSender : BackgroundService
{
    private readonly HermexRuntimeState _state;
    private readonly ILogger<SampleMailSender> _logger;

    public SampleMailSender(HermexRuntimeState state, ILogger<SampleMailSender> logger)
    {
        _state = state;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give the SMTP listener a moment to bind its port.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var port = _state.ListeningPort ?? 2530;
        _logger.LogInformation("Sending sample messages to the in-process SMTP server on port {Port}.", port);

        MailMessage[] messages = { CreatePlainText(), CreateHtml(), CreateWithAttachment() };

        foreach (var message in messages)
        {
            if (stoppingToken.IsCancellationRequested)
                return;

            var subject = message.Subject;
            try
            {
                using var client = new SmtpClient("127.0.0.1", port);
                using (message)
                {
                    await client.SendMailAsync(message, stoppingToken);
                }
                _logger.LogInformation("Sent sample message: {Subject}", subject);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send a sample message.");
            }
        }

        _logger.LogInformation("Done. Open http://localhost:5180/hermex to inspect the captured mail.");
    }

    private static MailMessage CreatePlainText() => new(
        from: "worker@contoso-service.com",
        to: "ops@example.com",
        subject: "Nightly job completed",
        body: "The nightly job finished successfully.\r\n\r\nProcessed 1,284 records in 47 seconds.\r\n");

    private static MailMessage CreateHtml()
    {
        var message = new MailMessage("reports@contoso-service.com", "ops@example.com")
        {
            Subject = "Daily report is ready",
            IsBodyHtml = true,
            BodyEncoding = Encoding.UTF8,
            Body = """
                <div style="font-family:Segoe UI,Arial,sans-serif;color:#1a1d24">
                  <h2 style="color:#4f46e5;margin:0 0 8px">Daily report</h2>
                  <p>All systems nominal. This HTML message was captured by Hermex.</p>
                </div>
                """,
        };
        return message;
    }

    private static MailMessage CreateWithAttachment()
    {
        var message = new MailMessage("worker@contoso-service.com", "ops@example.com")
        {
            Subject = "Export attached",
            Body = "The requested export is attached as a CSV file.",
        };

        var csv = Encoding.UTF8.GetBytes("Id,Status,Duration\r\n1,OK,47s\r\n2,OK,51s\r\n3,OK,44s\r\n");
        message.Attachments.Add(new Attachment(new MemoryStream(csv), "export.csv", "text/csv"));
        return message;
    }
}
