using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Hermex.Background;
using Hermex.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Hermex.Smtp;

/// <summary>
/// Hosts the in-process SMTP listener. Runs entirely on background threads, never crashes the
/// host on a port conflict, and can re-bind at runtime when the SMTP port is changed.
/// </summary>
internal sealed class SmtpServerService : RestartableListenerService
{
    private readonly HermexOptions _options;
    private readonly HermexRuntimeState _state;
    private readonly IReceivedMailSink _sink;
    private readonly IHermexEventLog _eventLog;
    private readonly ILogger<SmtpServerService> _logger;
    private readonly SemaphoreSlim _connectionLimiter;
    private X509Certificate2? _tlsCertificate;

    public SmtpServerService(
        HermexOptions options,
        HermexRuntimeState state,
        IReceivedMailSink sink,
        IHermexEventLog eventLog,
        ILogger<SmtpServerService> logger)
    {
        _options = options;
        _state = state;
        _sink = sink;
        _eventLog = eventLog;
        _logger = logger;
        _connectionLimiter = new SemaphoreSlim(Math.Max(1, options.MaxConcurrentConnections));
    }

    protected override async Task RunListenerAsync(CancellationToken runToken)
    {
        _state.Status = HermexServerStatus.Starting;
        _state.ConfiguredPort = _options.SmtpPort;
        _state.ServerHostName = _options.ServerHostName;

        if (_options.TlsMode != HermexTlsMode.None && _tlsCertificate is null)
        {
            try
            {
                _tlsCertificate = TlsCertificateFactory.Resolve(_options);
                if (_tlsCertificate is not null)
                    _eventLog.Info("Server", $"TLS enabled (mode: {_options.TlsMode}).");
            }
            catch (Exception ex)
            {
                _eventLog.Error("Server", $"Could not prepare the TLS certificate: {ex.Message}");
            }
        }

        var address = _options.ResolveListenAddress();
        TcpListener listener;
        try
        {
            listener = PortAllocator.Bind(address, _options.SmtpPort, _options.AutoResolvePortConflict,
                _options.MaxPortProbeAttempts, out var boundPort, out _);
            _state.ListeningPort = boundPort;
            _state.ListenAddress = address.ToString();
            _state.StartedAtUtc = DateTimeOffset.UtcNow;
            _state.Status = HermexServerStatus.Listening;

            if (boundPort != _options.SmtpPort)
            {
                _eventLog.Warning("Server",
                    $"Configured port {_options.SmtpPort} was in use — the SMTP server bound to {boundPort} instead.");
            }
            _eventLog.Info("Server", $"SMTP server listening on {address}:{boundPort}.");
            _logger.LogInformation("Hermex SMTP server listening on {Address}:{Port}.", address, boundPort);
        }
        catch (HermexPortConflictException ex)
        {
            _state.Status = HermexServerStatus.PortConflict;
            _state.LastError = ex.Message;
            _eventLog.Error("Server", ex.Message);
            _logger.LogError(ex, "Hermex SMTP server could not bind a port. The dashboard remains available.");
            return;
        }
        catch (Exception ex)
        {
            _state.Status = HermexServerStatus.Faulted;
            _state.LastError = ex.Message;
            _eventLog.Error("Server", $"SMTP server failed to start: {ex.Message}");
            _logger.LogError(ex, "Hermex SMTP server failed to start.");
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
            _state.Status = HermexServerStatus.Stopped;
            try { listener.Stop(); }
            catch { /* ignore */ }
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken runToken)
    {
        var acquired = false;
        try
        {
            await _connectionLimiter.WaitAsync(runToken).ConfigureAwait(false);
            acquired = true;
            _state.OnConnectionAccepted();

            var session = new SmtpSession(client, _options, _state, _sink, _eventLog, _tlsCertificate);
            await session.RunAsync(runToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* restarting or shutting down */ }
        catch (Exception ex)
        {
            _eventLog.Error("Smtp", $"Unhandled connection error: {ex.Message}");
        }
        finally
        {
            if (acquired)
            {
                _state.OnConnectionClosed();
                _connectionLimiter.Release();
            }
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
