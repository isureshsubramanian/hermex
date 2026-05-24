using Hermex;
using Hermex.Sample.ConsoleApp;

// A console / worker-style process that hosts Hermex.
//
// The Hermex dashboard is served as middleware, so even a "console" application uses the
// lightweight WebApplication host to expose it. The SMTP server itself is just a background
// service and would run under any generic host.
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5180");

builder.Services.AddMail4Dev(options =>
{
    options.SmtpPort = 2530;
    options.ServerHostName = "hermex.console";
    options.DashboardTitle = "Hermex Console";
    options.DefaultTheme = HermexTheme.Dark;

    // Expose captured mail over IMAP — connect a real mail client to localhost:1144.
    options.EnableImap = true;
    options.ImapPort = 1144;
});

// Sends a handful of sample messages a few seconds after startup.
builder.Services.AddHostedService<SampleMailSender>();

var app = builder.Build();
app.UseMail4Dev();
app.MapGet("/", () => Results.Redirect("/hermex"));

Console.WriteLine();
Console.WriteLine("  Hermex — console sample");
Console.WriteLine("  ---------------------------------------------");
Console.WriteLine("  SMTP server : localhost:2530");
Console.WriteLine("  IMAP server : localhost:1144");
Console.WriteLine("  Dashboard   : http://localhost:5180/hermex");
Console.WriteLine("  Sample mail will be sent automatically in a moment.");
Console.WriteLine();

app.Run();
