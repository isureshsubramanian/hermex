# Hermex

**An in-process SMTP + IMAP server with an executive-grade web dashboard for .NET applications.**

Hermex captures the HTML and text email your application sends — locally, inside your own
process. No Docker container, no external service, no mail ever leaving your machine. Add one
service registration and one middleware call, and every message your app sends becomes
inspectable in a polished, real-time dashboard — or in a real mail client over IMAP.

```csharp
builder.Services.AddMail4Dev();
app.UseMail4Dev();
```

Point your application's mail client at `localhost:2525` and open `/hermex`.

---

## Why Hermex

Most development SMTP servers (smtp4dev, Mailhog, Papercut) run as a *separate* process or
container. Hermex runs **inside your application** as a set of background services:

- **Zero external dependencies** — it ships as a single NuGet package.
- **Starts and stops with your app** — nothing to spin up or forget to shut down.
- **No impact on application performance** — the SMTP/IMAP listeners and storage run on
  background threads; your request pipeline never touches the mail store.

> The registration methods are `AddMail4Dev()` and `UseMail4Dev()` by design — the original,
> familiar API surface — even though the package and namespace are `Hermex`.

## Features

- **Hand-rolled SMTP server** — RFC 5321 (`HELO`/`EHLO`, `MAIL`, `RCPT`, `DATA`, `AUTH`,
  `STARTTLS`, `RSET`, `NOOP`, `QUIT`), dot-unstuffing, size limits, concurrency control.
- **Hand-rolled IMAP server** — browse captured mail from a real client (Outlook,
  Thunderbird, Apple Mail): `LOGIN`, `LIST`, `SELECT`/`EXAMINE`, `FETCH`/`UID FETCH`,
  `SEARCH`, `STORE`, `EXPUNGE` with `ENVELOPE` and `BODYSTRUCTURE`.
- **Hand-rolled MIME parser** — nested multipart, base64 / quoted-printable, RFC 2047
  encoded-words, charsets, attachments and inline resources, malformed-input tolerance.
- **Automatic mailboxes** — every recipient address becomes its own mailbox; an `INBOX`
  catch-all holds everything. The dashboard has a mailbox sidebar; IMAP exposes each as a folder.
- **SQLite mail store** — WAL journaling and batched transactional writes that comfortably
  absorb bursts of marketing mail.
- **Executive web dashboard** — Razor Pages UI: master/detail inbox, HTML / Text / Source /
  Headers / Structure / Attachments / Session views, dark & light themes, live SignalR updates.
- **Runtime-editable settings** — change ports, retention, relay and more from the dashboard;
  saved to the database and applied immediately. A port change re-binds the listener live.
- **TLS** — `STARTTLS` and implicit TLS, with a supplied or auto-generated certificate.
- **Per-message SMTP transcript**, **MIME structure tree**, **upstream relay**, **diagnostic
  logs**, **port-conflict auto-resolution** and **retention by count and age**.

## Installation

Add a project or package reference to `Hermex`, then register it.

### ASP.NET Core web application

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMail4Dev(options =>
{
    options.SmtpPort = 2525;
    options.EnableImap = true;     // optional — expose captured mail over IMAP
    options.ImapPort = 1143;
});

var app = builder.Build();

app.UseMail4Dev();   // dashboard at /hermex, API at /hermex/api, hub at /hermex/hub

app.Run();
```

Your application sends mail exactly as it always has — for example with the standard
`System.Net.Mail.SmtpClient` or MailKit pointed at `localhost:2525`. Hermex captures it.

### Console / worker application

The dashboard is ASP.NET Core middleware, so a console process hosts it through the
lightweight `WebApplication` host. The SMTP/IMAP servers are background services and run under
any generic host.

## Configuration

All configuration is done through the `AddMail4Dev` callback.

| Option | Default | Description |
| --- | --- | --- |
| `SmtpPort` | `2525` | TCP port for the SMTP server. |
| `EnableImap` / `ImapPort` | `false` / `1143` | Enable the IMAP server and choose its port. |
| `ListenAddress` | `loopback` | Interface to bind (`loopback`, `any`, or an IP). |
| `ServerHostName` | `hermex.local` | SMTP greeting host name. |
| `AutoResolvePortConflict` | `true` | Probe subsequent ports when one is busy. |
| `MaxConcurrentConnections` | `64` | Concurrency cap for connections. |
| `MaxMessageSizeBytes` | `25 MB` | Largest message accepted. |
| `RequireAuthentication` | `false` | Require `AUTH` (any credentials are accepted). |
| `CaptureSessionTranscript` | `true` | Record the SMTP conversation per message. |
| `TlsMode` | `None` | `None`, `StartTls`, or `Implicit`. |
| `DatabasePath` | `{ContentRoot}/hermex/hermex.db` | SQLite file location. |
| `RetentionMaxMessages` / `RetentionMaxAge` | `5000` / _none_ | Retention policy. |
| `DefaultTheme` | `System` | `System`, `Light`, or `Dark`. |
| `Relay` | _disabled_ | Upstream relay settings. |

Most of these are also editable at runtime from the dashboard's **Settings** page — including
the SMTP and IMAP ports, which re-bind their listener live.

## Browsing captured mail over IMAP

With `EnableImap = true`, point any mail client at `localhost:1143` (any credentials are
accepted). Each recipient address appears as a folder, plus an `INBOX` that holds everything.
Clients can read, search, flag and expunge messages.

## Mailboxes

Every captured message is filed into the `INBOX` catch-all **and** a mailbox for each of its
recipient addresses. The dashboard's mailbox sidebar filters the inbox; over IMAP each mailbox
is a selectable folder.

## Architecture

```
   your app sends mail
          |
          v
  +------------------+   raw bytes    +------------------+
  |  SMTP server     | -------------> |  write queue     |  (bounded Channel)
  |  (background)    |   250 OK fast  |  (in memory)     |
  +------------------+                +--------+---------+
                                               | batches
                                               v
                                      +------------------+      +--------------+
                                      |  persistence     |----->|  SQLite      |<--- IMAP server
                                      |  service         |      |  mail store  |<--- dashboard
                                      +------------------+      +--------------+
```

The SMTP path does the minimum — accept the bytes, return `250 OK`, hand off to an in-memory
queue. MIME parsing and disk I/O happen on a background worker in batched transactions, so
mail capture stays fast and the host application is never blocked.

### Will SQLite handle a thousand emails?

Comfortably. A 1,000-message marketing burst is a small workload for SQLite. WAL journaling
plus batched transactions (default 64 messages per transaction) sustain thousands of inserts
per second, and the queue absorbs the burst so the SMTP path stays fast. Retention keeps the
database file bounded.

## Samples

| Sample | Description |
| --- | --- |
| `samples/Hermex.Sample.Web` | An ASP.NET Core app with a page that sends test HTML, text, attachment and bulk emails. |
| `samples/Hermex.Sample.Console` | A console/worker process that hosts Hermex and sends sample mail on startup. |

```bash
dotnet run --project samples/Hermex.Sample.Web
# then browse to http://localhost:5170 and http://localhost:5170/hermex
```

## Building and testing

```bash
dotnet build Hermex.slnx
dotnet test  tests/Hermex.Tests/Hermex.Tests.csproj
```

The library multi-targets **net8.0**, **net9.0** and **net10.0**.

## License

MIT.
