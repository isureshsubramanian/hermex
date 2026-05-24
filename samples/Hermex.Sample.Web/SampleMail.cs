using System.Net.Mail;
using System.Net.Mime;
using System.Text;

namespace Hermex.Sample.Web;

/// <summary>Builds representative test messages for the sample to send.</summary>
internal static class SampleMail
{
    public static MailMessage PlainText()
    {
        return new MailMessage(
            from: "alerts@contoso-app.com",
            to: "developer@example.com",
            subject: "Plain-text notification",
            body: """
                Hello,

                This is a plain-text message sent from the Hermex sample
                application through the in-process SMTP server.

                Nothing left your machine — Hermex captured it for inspection.

                — The Contoso App
                """);
    }

    public static MailMessage Html()
    {
        var message = new MailMessage
        {
            From = new MailAddress("hello@contoso-app.com", "Contoso App"),
            // The en-dash exercises RFC 2047 encoded-word handling in the MIME parser.
            Subject = "Welcome to Contoso – your account is ready",
            SubjectEncoding = Encoding.UTF8,
        };
        message.To.Add(new MailAddress("developer@example.com", "Developer"));

        const string plain =
            "Welcome to Contoso!\r\n\r\nThanks for signing up. " +
            "Your account is ready to use.\r\n";

        message.AlternateViews.Add(
            AlternateView.CreateAlternateViewFromString(plain, Encoding.UTF8, MediaTypeNames.Text.Plain));
        message.AlternateViews.Add(
            AlternateView.CreateAlternateViewFromString(WelcomeHtml(), Encoding.UTF8, MediaTypeNames.Text.Html));

        return message;
    }

    public static MailMessage WithAttachment()
    {
        var message = new MailMessage(
            from: "billing@contoso-app.com",
            to: "developer@example.com")
        {
            Subject = "Your invoice INV-1042",
            IsBodyHtml = true,
            BodyEncoding = Encoding.UTF8,
            Body = """
                <div style="font-family:Segoe UI,Arial,sans-serif;color:#1a1d24">
                  <h2 style="margin:0 0 8px">Invoice INV-1042</h2>
                  <p>Thank you for your business. Your invoice is attached as a text file.</p>
                  <p style="color:#5d6472">Amount due: <strong>$480.00</strong></p>
                </div>
                """,
        };

        var invoice = Encoding.UTF8.GetBytes("""
            CONTOSO — INVOICE INV-1042
            ----------------------------------------
            Professional plan (monthly)      $480.00
            ----------------------------------------
            Total due                        $480.00
            """);

        message.Attachments.Add(new Attachment(new MemoryStream(invoice), "invoice-1042.txt", MediaTypeNames.Text.Plain));
        return message;
    }

    public static MailMessage Marketing(int index)
    {
        var message = new MailMessage
        {
            From = new MailAddress("news@contoso-app.com", "Contoso News"),
            Subject = $"Campaign update #{index} — fresh features inside",
            IsBodyHtml = true,
            BodyEncoding = Encoding.UTF8,
            SubjectEncoding = Encoding.UTF8,
            Body = $"""
                <div style="font-family:Segoe UI,Arial,sans-serif;max-width:520px;color:#1a1d24">
                  <h2 style="color:#4f46e5;margin:0 0 6px">Contoso Monthly</h2>
                  <p>This is marketing email <strong>#{index}</strong> in the batch — a quick
                     way to see how Hermex handles a burst of messages.</p>
                  <p><a href="https://example.com/offer/{index}"
                        style="background:#4f46e5;color:#fff;padding:9px 16px;border-radius:6px;
                               text-decoration:none">View this month's offer</a></p>
                  <p style="color:#9aa0ac;font-size:12px">You received this because you are a
                     Hermex sample recipient.</p>
                </div>
                """,
        };
        message.To.Add(new MailAddress($"subscriber{index}@example.com"));
        return message;
    }

    private static string WelcomeHtml() => """
        <!doctype html>
        <html>
          <body style="margin:0;background:#f3f4f7;padding:24px;
                       font-family:Segoe UI,Arial,sans-serif;color:#1a1d24">
            <div style="max-width:520px;margin:0 auto;background:#ffffff;border-radius:12px;
                        overflow:hidden;border:1px solid #e4e6eb">
              <div style="background:linear-gradient(135deg,#4f46e5,#4338ca);padding:28px 30px">
                <h1 style="margin:0;color:#fff;font-size:20px">Welcome to Contoso</h1>
              </div>
              <div style="padding:26px 30px">
                <p>Hi Developer,</p>
                <p>Your account is ready. This HTML email was captured by
                   <strong>Hermex</strong> — open the dashboard to inspect its
                   HTML, plain-text alternative, headers and raw source.</p>
                <p style="margin:22px 0">
                  <a href="https://example.com/get-started"
                     style="background:#4f46e5;color:#fff;padding:11px 20px;border-radius:8px;
                            text-decoration:none;font-weight:600">Get started</a>
                </p>
                <p style="color:#9aa0ac;font-size:12px;margin:0">
                  Sent by the Hermex sample application.</p>
              </div>
            </div>
          </body>
        </html>
        """;
}
