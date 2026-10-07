using Burbuja.Core.Abstractions.Approvals;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Artifacts;
using Burbuja.Core.Abstractions.Clock;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Leases;
using Burbuja.Core.Abstractions.Outbox;
using Burbuja.Core.Abstractions.Plugin;
using Burbuja.Core.Abstractions.Redaction;
using Burbuja.Core.Abstractions.Store;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Burbuja.Plugins.Store.Sqlite;

/// <summary>
/// Replaces the kernel's in-memory stores with durable SQLite equivalents so a single-node host
/// survives a restart with its events, resources, leases and outbox intact.
/// </summary>
public sealed class StoreSqlitePlugin : IBurbujaPlugin
{
    public PluginManifest Manifest { get; } = new(
        "Store.Sqlite",
        "SQLite Stores",
        "1.0.0",
        PluginContracts.Version,
        "Burbuja.Plugins.Store.Sqlite.dll",
        typeof(StoreSqlitePlugin).FullName!,
        PluginLoadKind.Dynamic,
        ["store.sqlite"],
        [],
        // No hosted service: the stores are passive, so this plugin never needs a trust entry.
        [PluginCapability.FilesystemWrite],
        ["all", "control-plane", "worker"],
        "Sqlite",
        true,
        [],
        100);

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<Burbuja.Core.Abstractions.Applications.IApplicationStorageProvider, SqliteApplicationStorageProvider>();
        services.TryAddSingleton(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var path = config["Sqlite:Path"];
            if (string.IsNullOrWhiteSpace(path))
                path = Path.Combine(DefaultStateDirectory(), "burbuja.db");
            return new SqliteDatabase(ExpandHome(path), sp.GetRequiredService<IClock>());
        });

        services.Replace(ServiceDescriptor.Singleton<IResourceStore>(sp => new SqliteResourceStore(
            sp.GetRequiredService<SqliteDatabase>(),
            sp.GetRequiredService<IClock>())));
        services.Replace(ServiceDescriptor.Singleton<IEventLog>(sp => new SqliteEventLog(
            sp.GetRequiredService<SqliteDatabase>(),
            sp.GetRequiredService<IRedactor>(),
            sp.GetRequiredService<IClock>())));
        services.Replace(ServiceDescriptor.Singleton<ILeaseStore>(sp => new SqliteLeaseStore(
            sp.GetRequiredService<SqliteDatabase>(),
            sp.GetRequiredService<IClock>())));

        services.Replace(ServiceDescriptor.Singleton<SqliteOutbox>(sp => new SqliteOutbox(
            sp.GetRequiredService<SqliteDatabase>())));
        services.Replace(ServiceDescriptor.Singleton<IOutbox>(sp => sp.GetRequiredService<SqliteOutbox>()));
        services.Replace(ServiceDescriptor.Singleton<IOutboxStore>(sp => sp.GetRequiredService<SqliteOutbox>()));

        services.Replace(ServiceDescriptor.Singleton(typeof(IDecisionCache<>), typeof(SqliteDecisionCache<>)));

        // M4.1: idempotency keys, approvals + grants, and operation polling are durable too.
        services.Replace(ServiceDescriptor.Singleton<IIdempotencyStore>(sp => new SqliteIdempotencyStore(
            sp.GetRequiredService<SqliteDatabase>(),
            sp.GetRequiredService<IClock>())));
        services.Replace(ServiceDescriptor.Singleton<IApprovalStore>(sp => new SqliteApprovalStore(
            sp.GetRequiredService<SqliteDatabase>())));
        services.Replace(ServiceDescriptor.Singleton<IOperationTracker>(sp => new SqliteOperationTracker(
            sp.GetRequiredService<SqliteDatabase>(),
            sp.GetRequiredService<IClock>())));

        services.Replace(ServiceDescriptor.Singleton<IArtifactStore>(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var root = config["Sqlite:ArtifactRoot"];
            if (string.IsNullOrWhiteSpace(root))
                root = Path.Combine(DefaultStateDirectory(), "artifacts");
            return new FileArtifactStore(ExpandHome(root), sp.GetRequiredService<IRedactor>());
        }));
    }

    private static string DefaultStateDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local",
        "state",
        "burbuja");

    private static string ExpandHome(string path) => path.StartsWith("~/", StringComparison.Ordinal)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..])
        : path;
}
