using Hermex;
using Hermex.Background;
using Hermex.Configuration;
using Hermex.Diagnostics;
using Hermex.Imap;
using Hermex.Realtime;
using Hermex.Smtp;
using Hermex.Storage;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.Extensions.Hosting;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registration entry point for the Hermex in-process SMTP server and dashboard.</summary>
public static class HermexServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Hermex SMTP server, SQLite mail store, background workers, SignalR
    /// hub and dashboard services. Pair this with <c>app.UseMail4Dev()</c> in the request
    /// pipeline.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional configuration callback.</param>
    public static IServiceCollection AddMail4Dev(this IServiceCollection services,
        Action<HermexOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new HermexOptions();
        configure?.Invoke(options);
        options.Validate();

        // Core singletons.
        services.AddSingleton(options);
        services.AddSingleton<HermexRuntimeState>();

        // Ingestion queue + storage.
        services.AddSingleton<MailWriteQueue>();
        services.AddSingleton<IReceivedMailSink>(sp => sp.GetRequiredService<MailWriteQueue>());
        services.AddSingleton<IMailStore, SqliteMailStore>();

        // Diagnostics.
        services.AddSingleton<IHermexEventLog, HermexEventLog>();

        // Upstream relay.
        services.AddSingleton<IMailRelayService, MailRelayService>();

        // Realtime dashboard updates.
        services.AddSingleton<IInboxNotifier, InboxNotifier>();
        services.AddSignalR();

        // Background workers. The SMTP and IMAP listeners are also registered as concrete
        // singletons so the settings service can re-bind them when a port changes at runtime.
        services.AddSingleton<SmtpServerService>();
        services.AddSingleton<ImapServerService>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<SmtpServerService>());
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<ImapServerService>());
        services.AddHostedService<MailPersistenceService>();
        services.AddHostedService<RetentionService>();

        // Runtime-editable settings.
        services.AddSingleton<HermexSettingsService>();

        // Dashboard UI (Razor Pages shipped inside this package). AddRazorPages is
        // idempotent, so this is safe even when the host application already uses it.
        var mvcBuilder = services.AddRazorPages();
        RegisterDashboardPart(mvcBuilder);

        return services;
    }

    /// <summary>
    /// Ensures the Hermex assembly is registered as an MVC application part exactly once,
    /// so its dashboard Razor Pages are discovered without producing duplicate endpoints.
    /// </summary>
    private static void RegisterDashboardPart(IMvcBuilder mvcBuilder)
    {
        var assembly = typeof(HermexServiceCollectionExtensions).Assembly;

        var alreadyRegistered = mvcBuilder.PartManager.ApplicationParts
            .OfType<AssemblyPart>()
            .Any(part => part.Assembly == assembly);

        if (!alreadyRegistered)
            mvcBuilder.AddApplicationPart(assembly);
    }
}
