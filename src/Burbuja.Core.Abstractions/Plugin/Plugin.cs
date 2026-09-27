using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;

namespace Burbuja.Core.Abstractions.Plugin;

public static class PluginContracts
{
    public const string Version = "1.0.0";
}

public enum PluginLoadKind
{
    Static,
    Dynamic,
}

public enum ExtensionCardinality
{
    Optional,
    ExactlyOne,
    OneOrMore,
}

[Flags]
public enum PluginCapability
{
    None = 0,
    ProcessExecution = 1,
    FilesystemWrite = 2,
    Network = 4,
    Secrets = 8,
    BackgroundService = 16,
    HttpEndpoints = 32,
}

/// <summary>
/// How a plugin is doing. Ordered worst-last so a fold over several opinions is a max:
/// a plugin's health is the <em>worst</em> status any of its sources reports.
/// </summary>
public enum PluginHealthStatus
{
    Healthy = 0,
    Degraded = 1,
    Unhealthy = 2,
}

/// <summary>
/// One plugin's health as reported by the registry or by an <see cref="IPluginHealthContributor"/>.
/// <paramref name="Data"/> is an opaque JSON bag (counts, queue depths, last run) that the
/// <c>/health/plugins</c> document copies through verbatim.
/// </summary>
public sealed record PluginHealth(
    PluginHealthStatus Status,
    string? Description = null,
    JsonObject? Data = null);

/// <summary>
/// A plugin-owned opinion about that plugin's health (a queue that is backing up, a store that is
/// unreachable). Registered by the plugin's own <c>ConfigureServices</c>; the kernel runs every
/// contributor whose <see cref="PluginId"/> matches the plugin under test and keeps the worst
/// status. Contracts version: 1.0.0 (additive — a load context that does not know it never
/// contributes).
/// </summary>
public interface IPluginHealthContributor
{
    string PluginId { get; }

    ValueTask<PluginHealth> CheckAsync(CancellationToken ct);
}

public sealed record PluginManifest(
    string Id,
    string Name,
    string Version,
    string ContractsVersion,
    string Assembly,
    string EntryType,
    PluginLoadKind Load,
    IReadOnlyList<string> Provides,
    IReadOnlyList<string> ExtensionPoints,
    IReadOnlyList<PluginCapability> Requires,
    IReadOnlyList<string> Roles,
    string? ConfigSection,
    bool Trusted,
    IReadOnlyList<string> DependsOn,
    int LoadOrder);

public interface IBurbujaPlugin
{
    PluginManifest Manifest { get; }
    void ConfigureServices(IServiceCollection services);

    /// <summary>
    /// Declares the extension points this plugin owns (the plugins that may contribute to it).
    /// Runs after instantiation and before manifest validation, so a point exists as soon as the
    /// plugin defining it is loaded — a consuming plugin never depends on declaration order.
    /// Default: the plugin declares nothing.
    /// </summary>
    void DeclareExtensionPoints(IExtensionPointRegistry registry)
    {
    }
}

public interface IExtensionPointRegistry
{
    void Declare<TContract>(string name, ExtensionCardinality cardinality, PluginCapability requiredCapabilities = PluginCapability.None)
        where TContract : class;

    IReadOnlyList<ExtensionPoint> All { get; }
}

public sealed record ExtensionPoint(
    string Name,
    Type Contract,
    ExtensionCardinality Cardinality,
    PluginCapability RequiredCapabilities);

/// <summary>
/// Recorded when a plugin contributes an implementation to a named extension point. Lets the
/// host count registrations per point instead of per contract (two points may share a contract).
/// </summary>
public sealed record ExtensionRegistration(string Point, Type Contract, Type Implementation);

public static class ExtensionServiceCollectionExtensions
{
    public static IServiceCollection AddExtension<TContract, TImplementation>(
        this IServiceCollection services,
        string point)
        where TContract : class
        where TImplementation : class, TContract
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(point);
        services.AddSingleton<TContract, TImplementation>();
        services.AddSingleton(new ExtensionRegistration(point, typeof(TContract), typeof(TImplementation)));
        return services;
    }
}
