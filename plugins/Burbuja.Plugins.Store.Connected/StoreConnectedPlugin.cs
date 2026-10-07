using Burbuja.Core.Abstractions.Applications;
using Burbuja.Core.Abstractions.Approvals;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Artifacts;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Leases;
using Burbuja.Core.Abstractions.Outbox;
using Burbuja.Core.Abstractions.Plugin;
using Burbuja.Core.Abstractions.Store;
using Burbuja.Core.Sdk.Applications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Burbuja.Plugins.Store.Connected;

/// <summary>
/// Serves every durable kernel store from a separate kernel service through the connected-application
/// protocol (`/kernel/v1`). The host keeps its own product handlers, workers and ARM surface, but opens no
/// database and holds no state file: the kernel service owns the binding, its provider, migrations and
/// backups. Kernel loss is explicit unavailability (<see cref="KernelUnavailableException"/>); there is no
/// local fallback store. Install this plugin instead of, or after, a local store plugin.
/// </summary>
public sealed class StoreConnectedPlugin : IBurbujaPlugin
{
    public const string Section = "ConnectedStore";

    public PluginManifest Manifest { get; } = new(
        "Store.Connected",
        "Connected Kernel Stores",
        "1.0.0",
        PluginContracts.Version,
        "Burbuja.Plugins.Store.Connected.dll",
        typeof(StoreConnectedPlugin).FullName!,
        PluginLoadKind.Dynamic,
        ["store.connected"],
        [],
        // Outbound HTTPS to the kernel service only; no filesystem, process or secret capability.
        [PluginCapability.Network],
        ["all", "control-plane", "worker"],
        Section,
        true,
        [],
        // After Store.Sqlite (100): when both are installed the remote stores win and the SQLite file is never opened.
        110);

    public void ConfigureServices(IServiceCollection services)
    {
        services.TryAddSingleton<ConnectedStoreConnection>();
        services.Replace(ServiceDescriptor.Singleton<IResourceStore>(sp => sp.GetRequiredService<ConnectedStoreConnection>().Client));
        services.Replace(ServiceDescriptor.Singleton<IEventLog>(sp => sp.GetRequiredService<ConnectedStoreConnection>().Client));
        services.Replace(ServiceDescriptor.Singleton<ILeaseStore>(sp => sp.GetRequiredService<ConnectedStoreConnection>().Client));
        services.Replace(ServiceDescriptor.Singleton<IOutbox>(sp => sp.GetRequiredService<ConnectedStoreConnection>().Client));
        services.Replace(ServiceDescriptor.Singleton<IOutboxStore>(sp => sp.GetRequiredService<ConnectedStoreConnection>().Client));
        services.Replace(ServiceDescriptor.Singleton<IIdempotencyStore>(sp => sp.GetRequiredService<ConnectedStoreConnection>().Client));
        services.Replace(ServiceDescriptor.Singleton<IApprovalStore>(sp => sp.GetRequiredService<ConnectedStoreConnection>().Client));
        services.Replace(ServiceDescriptor.Singleton<IOperationTracker>(sp => sp.GetRequiredService<ConnectedStoreConnection>().Client));
        services.Replace(ServiceDescriptor.Singleton<IArtifactStore>(sp => sp.GetRequiredService<ConnectedStoreConnection>().Client));
        services.Replace(ServiceDescriptor.Singleton(typeof(IDecisionCache<>), typeof(ConnectedDecisionCache<>)));
    }
}

/// <summary>Settings under <c>ConnectedStore</c>. The credential is private runtime configuration, never a resource.</summary>
public sealed class ConnectedStoreOptions
{
    public string Url { get; set; } = "";
    public string Credential { get; set; } = "";
    /// <summary>Alternative to <see cref="Credential"/>: a file whose trimmed content is the credential.</summary>
    public string CredentialFile { get; set; } = "";
    public string Environment { get; set; } = "";
    public string Tenant { get; set; } = "";
    public string Application { get; set; } = "";
    /// <summary>Delegated audit identity recorded by the kernel for this host's calls; not a role.</summary>
    public string Actor { get; set; } = "";
    /// <summary>Permit plain HTTP for a development environment only (loopback or private networks).</summary>
    public bool AllowInsecureDevelopment { get; set; }
    public int TimeoutSeconds { get; set; } = 30;

    public static ConnectedStoreOptions Read(IConfiguration configuration)
    {
        var options = new ConnectedStoreOptions();
        configuration.GetSection(StoreConnectedPlugin.Section).Bind(options);
        if (options.Credential.Length == 0 && options.CredentialFile.Length > 0)
            options.Credential = File.ReadAllText(options.CredentialFile).Trim();
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(options.Url)) missing.Add("Url");
        if (string.IsNullOrWhiteSpace(options.Credential)) missing.Add("Credential (or CredentialFile)");
        if (string.IsNullOrWhiteSpace(options.Environment)) missing.Add("Environment");
        if (string.IsNullOrWhiteSpace(options.Tenant)) missing.Add("Tenant");
        if (string.IsNullOrWhiteSpace(options.Application)) missing.Add("Application");
        if (missing.Count > 0)
            throw new InvalidOperationException("Store.Connected requires " + StoreConnectedPlugin.Section + ":" + string.Join(", ", missing)
                + ". The kernel service registers the application scope and issues the credential.");
        if (options.TimeoutSeconds is < 1 or > 600)
            throw new InvalidOperationException("Store.Connected TimeoutSeconds must be between 1 and 600.");
        return options;
    }

    public ApplicationScope Scope => new(Environment, Tenant, Application);
}

/// <summary>
/// One verified connection per host. The handshake runs once, on first store use, so a host whose kernel
/// is unreachable fails at startup with the kernel's identity in the message instead of serving nothing.
/// </summary>
public sealed class ConnectedStoreConnection : IDisposable
{
    private readonly Lazy<ConnectedKernelClient> _client;
    private readonly HttpClient _http = new();
    private ApplicationHandshake? _handshake;

    public ConnectedStoreConnection(IConfiguration configuration, ILoggerFactory? loggerFactory = null)
    {
        var logger = loggerFactory?.CreateLogger<ConnectedStoreConnection>();
        _client = new Lazy<ConnectedKernelClient>(() =>
        {
            var options = ConnectedStoreOptions.Read(configuration);
            _http.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            var client = new ConnectedKernelClient(_http, new Uri(options.Url, UriKind.Absolute), options.Credential, options.Scope,
                actor: options.Actor.Length > 0 ? options.Actor : null, allowInsecureDevelopment: options.AllowInsecureDevelopment);
            try
            {
                _handshake = client.ConnectAsync().GetAwaiter().GetResult();
            }
            catch (Exception error) when (error is KernelUnavailableException or HttpRequestException or OperationCanceledException)
            {
                throw new InvalidOperationException($"Store.Connected cannot reach the kernel service at {options.Url} for "
                    + $"{options.Environment}/{options.Tenant}/{options.Application}; this host has no local store to fall back to.", error);
            }
            logger?.LogInformation("Store.Connected serving {Scope} from kernel {Kernel} ({Protocol}, {Version}); capabilities {Capabilities}",
                _handshake.Scope, options.Url, _handshake.Protocol, _handshake.KernelVersion, string.Join(",", _handshake.Capabilities));
            return client;
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public ConnectedKernelClient Client => _client.Value;

    /// <summary>The negotiated scope/version after the first store use; null before.</summary>
    public ApplicationHandshake? Handshake => _handshake;

    public void Dispose() => _http.Dispose();
}

public sealed class ConnectedDecisionCache<T>(ConnectedStoreConnection connection) : IDecisionCache<T>
{
    private readonly Lazy<IDecisionCache<T>> _inner = new(() => connection.Client.DecisionCache<T>());
    public ValueTask<T?> GetAsync(string streamId, string decisionKey, CancellationToken ct) => _inner.Value.GetAsync(streamId, decisionKey, ct);
    public ValueTask SetAsync(string streamId, string decisionKey, T value, CancellationToken ct) => _inner.Value.SetAsync(streamId, decisionKey, value, ct);
}
