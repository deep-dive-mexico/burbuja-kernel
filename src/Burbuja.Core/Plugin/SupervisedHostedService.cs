using Burbuja.Core.Abstractions.Plugin;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Burbuja.Core.Plugin;

/// <summary>
/// Track 3a: the kernel supervises every hosted service a plugin registers.
/// <para>
/// <b>Fail-fast by default.</b> A plugin whose <c>StartAsync</c> throws takes the host down, and
/// that is deliberate: launchd (macOS) and <c>docker compose</c> (Pi) are the real supervisors, and
/// a kernel that limps on with half its plugins is harder to diagnose than one that exits and is
/// restarted. <c>Plugins:&lt;Id&gt;:FailSoft=true</c> opts one plugin out: the failure is logged and
/// recorded, and the host continues without that service.
/// </para>
/// <para>
/// A <see cref="BackgroundService"/> faults <em>after</em> a successful start — there is no
/// <c>StartAsync</c> to rethrow from — so its <c>ExecuteTask</c> is observed: the exception is
/// logged (never swallowed) and the plugin is marked <see cref="PluginHealthState.Faulted"/>.
/// </para>
/// </summary>
public sealed class SupervisedHostedService(
    IHostedService inner,
    string pluginId,
    string serviceName,
    PluginHealthRegistry registry,
    ILogger log,
    bool failSoft = false) : IHostedService
{
    /// <summary>The wrapped service. Exposed so a test (and a future diagnostics surface) can see through the wrapper.</summary>
    public IHostedService Inner { get; } = inner;

    public string PluginId { get; } = pluginId;

    public string ServiceName { get; } = serviceName;

    /// <summary>True when a start failure is recorded and swallowed instead of rethrown.</summary>
    public bool FailSoft { get; } = failSoft;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        registry.NoteHostedService(PluginId, ServiceName);
        try
        {
            await Inner.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            registry.RecordStartFailed(PluginId, ServiceName, ex);
            if (!FailSoft)
                throw;

            log.LogError(
                ex,
                "plugin {PluginId} hosted service {Service} failed to start; Plugins:{PluginId}:FailSoft=true so the host continues without it",
                PluginId,
                ServiceName,
                PluginId);
            return;
        }

        registry.RecordStarted(PluginId, ServiceName);

        if (Inner is BackgroundService background && background.ExecuteTask is { } execute)
            Observe(execute);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await Inner.StopAsync(cancellationToken).ConfigureAwait(false);
        registry.RecordStopped(PluginId, ServiceName);
    }

    /// <summary>
    /// Wraps one <see cref="IHostedService"/> descriptor owned by <paramref name="pluginId"/>.
    /// The descriptor's lifetime is preserved, and for a factory descriptor the concrete type is
    /// only knowable once the factory runs — so the wrapper notes it then.
    /// </summary>
    internal static ServiceDescriptor Wrap(
        ServiceDescriptor descriptor,
        string pluginId,
        PluginHealthRegistry registry,
        ILogger fallbackLog,
        bool failSoft)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.ServiceType != typeof(IHostedService))
            throw new ArgumentException($"only {nameof(IHostedService)} descriptors are supervised", nameof(descriptor));

        if (descriptor.ImplementationInstance is IHostedService instance)
        {
            var name = TypeName(instance.GetType());
            registry.NoteHostedService(pluginId, name);
            IHostedService wrapper = new SupervisedHostedService(instance, pluginId, name, registry, fallbackLog, failSoft);
            return ServiceDescriptor.Singleton(typeof(IHostedService), wrapper);
        }

        if (descriptor.ImplementationType is { } type)
        {
            registry.NoteHostedService(pluginId, TypeName(type));
            return ServiceDescriptor.Describe(
                typeof(IHostedService),
                sp => Supervise((IHostedService)ActivatorUtilities.CreateInstance(sp, type), pluginId, registry, failSoft, sp),
                descriptor.Lifetime);
        }

        if (descriptor.ImplementationFactory is { } factory)
        {
            return ServiceDescriptor.Describe(
                typeof(IHostedService),
                sp => Supervise((IHostedService)factory(sp), pluginId, registry, failSoft, sp),
                descriptor.Lifetime);
        }

        throw new ArgumentException($"hosted service descriptor for plugin '{pluginId}' has no implementation", nameof(descriptor));
    }

    private static IHostedService Supervise(
        IHostedService inner,
        string pluginId,
        PluginHealthRegistry registry,
        bool failSoft,
        IServiceProvider services)
    {
        var name = TypeName(inner.GetType());
        registry.NoteHostedService(pluginId, name);
        var log = services.GetService<ILoggerFactory>()?.CreateLogger<SupervisedHostedService>()
                  ?? (ILogger)NullLogger<SupervisedHostedService>.Instance;
        return new SupervisedHostedService(inner, pluginId, name, registry, log, failSoft);
    }

    private void Observe(Task execute)
    {
        if (execute.IsCompleted)
        {
            HandleFault(execute);
            return;
        }

        _ = execute.ContinueWith(
            HandleFault,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void HandleFault(Task execute)
    {
        if (!execute.IsFaulted)
            return;

        var error = execute.Exception?.Flatten().InnerExceptions.FirstOrDefault() ?? execute.Exception;
        registry.RecordFaulted(PluginId, ServiceName, error);
        log.LogError(
            error,
            "plugin {PluginId} background service {Service} faulted; the plugin is marked Faulted",
            PluginId,
            ServiceName);
    }

    private static string TypeName(Type type) => type.FullName ?? type.Name;
}
