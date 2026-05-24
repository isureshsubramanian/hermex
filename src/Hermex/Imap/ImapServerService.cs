using System.Net.Sockets;
using Hermex.Background;
using Hermex.Diagnostics;
using Hermex.Smtp;
using Hermex.Storage;
using Microsoft.Extensions.Logging;

namespace Hermex.Imap;

/// <summary>
/// Hosts the in-process IMAP server. It runs only when <see cref="HermexOptions.EnableImap"/>
/// is set, never crashes the host on a port conflict, and can re-bind at runtime.
/// </summary>
internal sealed class ImapServerService : RestartableListenerService
{
    private readonly HermexOptions _options;
    private readonly HermexRuntimeState _state;
    private readonly IMailStore _store;
    private readonly IHermexEventLog _eventLog;
    private readonly ILogger<ImapServerService> _logger;
    private readonly SemaphoreSlim _connectionLimiter;

    public ImapServerService(
        HermexOptions options,
        HermexRuntimeState state,
        IMailStore store,
        IHermexEventLog eventLog,
        ILogger<ImapServerService> logger)
    {
        _options = options;
        _state = state;
        _store = store;
        _eventLog = eventLog;
        _logger = logger;
        _connectionLimiter = new SemaphoreSlim(Math.Max(1, options.MaxConcurrentConnections));
    }

    protected override async Task RunListenerAsync(CancellationToken runToken)
    {
        _state.ImapEnabled = _options.EnableImap;
        if (!_options.EnableImap)
        {
            _state.ImapStatus = HermexServerStatus.Stopped;
            _state.ImapListeningPort = null;
            return;
        }

        _state.ImapStatus = HermexServerStatus.Starting;
        var address = _options.ResolveListenAddress();

        TcpListener listener;
        try
        {
            listener = PortAllocator.Bind(address, _options.ImapPort, _options.AutoResolvePortConflict,
                _options.MaxPortProbeAttempts, out var boundPort, out _);
            _state.ImapListeningPort = boundPort;
            _state.ImapStatus = HermexServerStatus.Listening;

            if (boundPort != _options.ImapPort)
            {
                _eventLog.Warning("Imap",
                    $"Configured IMAP port {_options.ImapPort} was in use — bound to {boundPort} instead.");
            }
            _eventLog.Info("Imap", $"IMAP server listening on {address}:{boundPort}.");
            _logger.LogInformation("Hermex IMAP server listening on {Address}:{Port}.", address, boundPort);
        }
        catch (HermexPortConflictException ex)
        {
            _state.ImapStatus = HermexServerStatus.PortConflict;
            _eventLog.Error("Imap", ex.Message);
            _logger.LogError(ex, "Hermex IMAP server could not bind a port.");
            return;
        }
        catch (Exception ex)
        {
            _state.ImapStatus = HermexServerStatus.Faulted;
            _eventLog.Error("Imap", $"IMAP server failed to start: {ex.Message}");
            _logger.LogError(ex, "Hermex IMAP server failed to start.");
            return;
        }

        try
        {
            while (!runToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(runToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException)
                {
                    continue;
                }

                _ = HandleConnectionAsync(client, runToken);
            }
        }
        finally
        {
            _state.ImapStatus = HermexServerStatus.Stopped;
            try { listener.Stop(); }
            catch { /* ignore */ }
            _eventLog.Info("Imap", "IMAP server stopped.");
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken runToken)
    {
        var acquired = false;
        try
        {
            await _connectionLimiter.WaitAsync(runToken).ConfigureAwait(false);
            acquired = true;

            var session = new ImapSession(client, _options, _store, _eventLog);
            await session.RunAsync(runToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* restarting or shutting down */ }
        catch (Exception ex)
        {
            _eventLog.Error("Imap", $"Unhandled connection error: {ex.Message}");
        }
        finally
        {
            if (acquired)
                _connectionLimiter.Release();
            try { client.Dispose(); }
            catch { /* ignore */ }
        }
    }

    public override void Dispose()
    {
        _connectionLimiter.Dispose();
        base.Dispose();
    }
}
