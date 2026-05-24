using Hermex.Diagnostics;
using Hermex.Realtime;
using Hermex.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hermex.Background;

/// <summary>
/// Periodically prunes the mail store so the SQLite file stays bounded during long-running
/// development sessions. Driven by <see cref="HermexOptions.RetentionMaxMessages"/> and
/// <see cref="HermexOptions.RetentionMaxAge"/>.
/// </summary>
internal sealed class RetentionService : BackgroundService
{
    private readonly IMailStore _store;
    private readonly IInboxNotifier _notifier;
    private readonly IHermexEventLog _eventLog;
    private readonly HermexOptions _options;
    private readonly ILogger<RetentionService> _logger;

    public RetentionService(
        IMailStore store,
        IInboxNotifier notifier,
        IHermexEventLog eventLog,
        HermexOptions options,
        ILogger<RetentionService> logger)
    {
        _store = store;
        _notifier = notifier;
        _eventLog = eventLog;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The sweep honours the current options each tick, so retention can be enabled or
        // disabled at runtime through the settings page without a restart.
        await _store.InitializeAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(_options.RetentionSweepInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    var pruned = await _store.ApplyRetentionAsync(stoppingToken).ConfigureAwait(false);
                    if (pruned > 0)
                    {
                        _eventLog.Info("Retention", $"Retention sweep pruned {pruned} message(s).");
                        var stats = await _store.GetStatsAsync(stoppingToken).ConfigureAwait(false);
                        await _notifier.StatsChangedAsync(stats).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Hermex: retention sweep failed.");
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }
}
