using Hermex;
using Hermex.Configuration;
using Hermex.Dashboard;
using Hermex.Internal;
using Hermex.Realtime;
using Hermex.Storage;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Builder;

/// <summary>Pipeline wiring for the Hermex dashboard, API and SignalR hub.</summary>
public static class HermexApplicationBuilderExtensions
{
    /// <summary>
    /// Wires up the Hermex dashboard. Mounts the executive UI at <c>/hermex</c>, the JSON
    /// API at <c>/hermex/api</c> and the SignalR hub at <c>/hermex/hub</c>. The SMTP
    /// server itself runs as a background service and needs no pipeline registration.
    /// </summary>
    public static WebApplication UseMail4Dev(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var options = app.Services.GetRequiredService<HermexOptions>();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Hermex");

        // Prepare the database up front so the dashboard is usable on the first request.
        try
        {
            app.Services.GetRequiredService<IMailStore>()
                .InitializeAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Hermex: the mail store could not be initialised.");
        }

        // Apply any persisted runtime settings before the listeners bind.
        try
        {
            app.Services.GetRequiredService<HermexSettingsService>()
                .LoadPersistedAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Hermex: persisted runtime settings could not be applied.");
        }

        if (!options.EnableDashboard)
        {
            logger.LogInformation("Hermex dashboard is disabled; the SMTP server still runs.");
            return app;
        }

        // Dashboard assets (embedded CSS/JS), realtime hub and JSON/file API.
        HermexAssetEndpoints.MapHermexAssets(app);
        app.MapHub<InboxHub>(HermexRoutes.Hub);
        HermexApiEndpoints.MapHermexApi(app);

        // Dashboard Razor Pages — map them only when the host has not already done so,
        // otherwise the pages would be registered twice and routing would be ambiguous.
        if (!RazorPagesAlreadyMapped(app))
            app.MapRazorPages();

        logger.LogInformation("Hermex dashboard available at {Path}.", HermexRoutes.Base);
        return app;
    }

    private static bool RazorPagesAlreadyMapped(IEndpointRouteBuilder endpoints)
    {
        foreach (var dataSource in endpoints.DataSources)
        {
            foreach (var endpoint in dataSource.Endpoints)
            {
                if (endpoint.Metadata.GetMetadata<PageActionDescriptor>() is not null)
                    return true;
            }
        }
        return false;
    }
}
