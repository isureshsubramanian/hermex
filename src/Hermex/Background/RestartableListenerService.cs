using Microsoft.Extensions.Hosting;

namespace Hermex.Background;

/// <summary>
/// Base class for a TCP listener hosted service that can re-bind at runtime — used so the
/// SMTP and IMAP listeners can move to a new port when settings change, without restarting
/// the host application.
/// </summary>
internal abstract class RestartableListenerService : BackgroundService
{
    private readonly SemaphoreSlim _restartSignal = new(0);
    private CancellationTokenSource? _runCts;

    /// <summary>Runs one bind/accept cycle; returns when <paramref name="runToken"/> is cancelled.</summary>
    protected abstract Task RunListenerAsync(CancellationToken runToken);

    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var first = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!first)
            {
                // Park until a restart is requested (also unblocks when the host shuts down).
                try { await _restartSignal.WaitAsync(stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            first = false;
            if (stoppingToken.IsCancellationRequested)
                break;

            using var runCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _runCts = runCts;
            try
            {
                await RunListenerAsync(runCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* restart or shutdown */ }
            finally
            {
                _runCts = null;
            }
        }
    }

    /// <summary>Stops the current listener and re-binds (e.g. after a port change).</summary>
    public void RequestRestart()
    {
        try { _runCts?.Cancel(); }
        catch { /* ignore */ }
        _restartSignal.Release();
    }

    public override void Dispose()
    {
        _restartSignal.Dispose();
        base.Dispose();
    }
}
