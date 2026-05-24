namespace Hermex.Sample.Web;

/// <summary>The static landing page served by the sample web application.</summary>
internal static class SamplePages
{
    public const string Landing = """
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1" />
          <title>Hermex — Sample Web App</title>
          <style>
            :root { color-scheme: light dark; }
            * { box-sizing: border-box; }
            body {
              margin: 0; min-height: 100vh;
              font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
              background: #f3f4f7; color: #181b22;
              display: flex; align-items: center; justify-content: center; padding: 32px;
            }
            .card {
              background: #fff; border: 1px solid #e4e6eb; border-radius: 16px;
              max-width: 560px; width: 100%; padding: 34px 36px;
              box-shadow: 0 12px 40px rgba(16,19,28,.10);
            }
            .brand { display: flex; align-items: center; gap: 12px; margin-bottom: 20px; }
            .mark {
              width: 42px; height: 42px; border-radius: 10px; display: grid; place-items: center;
              background: linear-gradient(135deg,#4f46e5,#4338ca); color: #fff;
            }
            h1 { margin: 0; font-size: 19px; }
            .tag { color: #5d6472; font-size: 13px; margin: 2px 0 0; }
            p.lead { color: #5d6472; font-size: 14px; line-height: 1.6; }
            .grid { display: grid; grid-template-columns: 1fr 1fr; gap: 10px; margin: 22px 0 8px; }
            button {
              font: inherit; font-weight: 600; font-size: 13.5px; cursor: pointer;
              padding: 12px 14px; border-radius: 9px; border: 1px solid #d3d6dd;
              background: #f8f9fb; color: #181b22; transition: background .12s, border-color .12s;
            }
            button:hover { background: #eef0fe; border-color: #4f46e5; }
            button:disabled { opacity: .6; cursor: progress; }
            .open {
              display: block; width: 100%; text-align: center; text-decoration: none;
              margin-top: 14px; padding: 13px; border-radius: 10px; font-weight: 700;
              background: #4f46e5; color: #fff;
            }
            .open:hover { background: #4338ca; }
            #result {
              margin-top: 16px; padding: 11px 13px; border-radius: 9px; font-size: 13px;
              font-weight: 600; display: none;
            }
            #result.ok { display: block; background: #e9f7ee; color: #15a34a; }
            #result.err { display: block; background: #fdeded; color: #dc2626; }
            code { background: #f1f2f5; padding: 2px 6px; border-radius: 5px; font-size: 12.5px; }
          </style>
        </head>
        <body>
          <main class="card">
            <div class="brand">
              <span class="mark">
                <svg viewBox="0 0 24 24" width="22" height="22" fill="none" stroke="currentColor"
                     stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
                  <rect x="2" y="4" width="20" height="16" rx="3" /><path d="m3 7 9 6 9-6" />
                </svg>
              </span>
              <div>
                <h1>Hermex — Sample Web App</h1>
                <p class="tag">In-process SMTP server &amp; executive dashboard</p>
              </div>
            </div>

            <p class="lead">
              This app registered Hermex with <code>AddMail4Dev()</code> and
              <code>UseMail4Dev()</code>. An SMTP server is listening on
              <code>localhost:2525</code>. Send a test message below — it never leaves your
              machine — then open the dashboard to inspect it.
            </p>

            <div class="grid">
              <button data-kind="text">Send plain-text email</button>
              <button data-kind="html">Send HTML email</button>
              <button data-kind="attachment">Send email + attachment</button>
              <button data-kind="burst">Send 50 marketing emails</button>
            </div>

            <div id="result"></div>

            <a class="open" href="/hermex">Open the Hermex dashboard →</a>
          </main>

          <script>
            const result = document.getElementById('result');
            document.querySelectorAll('button[data-kind]').forEach(btn => {
              btn.addEventListener('click', async () => {
                const kind = btn.dataset.kind;
                const url = kind === 'burst' ? '/api/send/burst?count=50' : '/api/send/' + kind;
                document.querySelectorAll('button').forEach(b => b.disabled = true);
                result.className = '';
                try {
                  const res = await fetch(url, { method: 'POST' });
                  const data = await res.json().catch(() => ({}));
                  if (!res.ok) throw new Error(data.detail || data.error || res.statusText);
                  result.className = 'ok';
                  result.textContent = 'Sent ' + (data.sent || 0) +
                    ' message(s) to the in-process SMTP server. Open the dashboard to view them.';
                } catch (e) {
                  result.className = 'err';
                  result.textContent = 'Failed: ' + e.message;
                } finally {
                  document.querySelectorAll('button').forEach(b => b.disabled = false);
                }
              });
            });
          </script>
        </body>
        </html>
        """;
}
