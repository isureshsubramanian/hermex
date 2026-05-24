using System.Net.Mail;
using Hermex;
using Hermex.Sample.Web;

var builder = WebApplication.CreateBuilder(args);

// Register the in-process SMTP server, mail store, background workers and dashboard.
builder.Services.AddMail4Dev(options =>
{
    options.SmtpPort = 2525;
    options.ServerHostName = "hermex.sample";
    options.DashboardTitle = "Hermex";
    options.DefaultTheme = HermexTheme.System;

    // Expose captured mail over IMAP — connect a real mail client to localhost:1143.
    options.EnableImap = true;
    options.ImapPort = 1143;
});

var app = builder.Build();

// Mount the dashboard at /hermex, the API at /hermex/api and the hub at /hermex/hub.
app.UseMail4Dev();

// A tiny landing page that sends test mail through the in-process SMTP server.
app.MapGet("/", () => Results.Content(SamplePages.Landing, "text/html"));

app.MapPost("/api/send/{kind}", async (string kind, int? count, HermexRuntimeState state) =>
{
    var port = state.ListeningPort ?? 2525;
    try
    {
        var sent = kind switch
        {
            "text" => await SendOneAsync(port, SampleMail.PlainText()),
            "html" => await SendOneAsync(port, SampleMail.Html()),
            "attachment" => await SendOneAsync(port, SampleMail.WithAttachment()),
            "burst" => await SendManyAsync(port, Math.Clamp(count ?? 25, 1, 250)),
            _ => -1,
        };

        return sent < 0
            ? Results.BadRequest(new { error = $"Unknown message kind '{kind}'." })
            : Results.Ok(new { sent, port });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Could not send mail to the in-process SMTP server: {ex.Message}");
    }
});

app.Run();

// Sends a single message through the standard .NET SMTP client — exactly how a real
// application sends mail. Hermex captures it for inspection.
static async Task<int> SendOneAsync(int port, MailMessage message)
{
    using var client = new SmtpClient("127.0.0.1", port);
    using (message)
    {
        await client.SendMailAsync(message);
    }
    return 1;
}

static async Task<int> SendManyAsync(int port, int count)
{
    for (var i = 1; i <= count; i++)
    {
        await SendOneAsync(port, SampleMail.Marketing(i));
    }
    return count;
}
