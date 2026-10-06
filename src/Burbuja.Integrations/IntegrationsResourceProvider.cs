using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Arm;
using Burbuja.Core.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Burbuja.Integrations;

/// <summary>
/// I0: the <c>integrations</c> resource type — the descriptor a portal renders and a CLI reads.
///
/// <para><b>Two actions, and both are declared because both are implemented.</b> A descriptor that
/// advertised an action nobody wrote is a button that fails, and this repository has removed that shape
/// often enough to check it with a fact.</para>
/// </summary>
public sealed class IntegrationsResourceProvider : IResourceProvider
{
    public IntegrationsResourceProvider()
    {
        ResourceTypes =
        [
            DescriptorBuilder.For("integrations")
                .Properties<IntegrationProperties>()
                .Capabilities(
                    ResourceCapabilities.Get
                    | ResourceCapabilities.Put
                    | ResourceCapabilities.Delete
                    | ResourceCapabilities.ListByResourceGroup
                    | ResourceCapabilities.List
                    | ResourceCapabilities.Actions)
                .Actions(
                    // Reaches the surface and records what it offers. Changes no authority: `tools` is what
                    // may be called, and only a human writes it.
                    // M11 P6: a state REPORT — every call must run, never replay from the 24 h response cache.
                    new ResourceActionDescriptor(IntegrationHandler.Probe, false, IsDataAction: true, Idempotent: false),
                    // The gated one. `approvalId` is in the schema because a 202 hands one back and the
                    // caller retries with it; omitting it from the schema would make the retry look
                    // undocumented.
                    new ResourceActionDescriptor(IntegrationHandler.Call, false, IsDataAction: true,
                        RequestSchema: Schema("tool", "input", "approvalId")))
                .DataPlane(new DataPlaneRouteDescriptor("events", DataPlaneKind.ServerSentEvents))
                .Build(),
        ];
    }

    public string Namespace => IntegrationConstants.Namespace;

    public IReadOnlyList<ResourceTypeDescriptor> ResourceTypes { get; }

    /// <summary>
    /// Nothing to do when the subscription moves. An integration is declared by a human and called by a
    /// request; a provider that reacted to a subscription state would be a surface that acts at a moment
    /// nobody chose.
    /// </summary>
    public ValueTask OnSubscriptionStateChangedAsync(SubscriptionNotification notification, CancellationToken ct) =>
        ValueTask.CompletedTask;

    /// <summary>The hint a form is rendered from. Loose on purpose: a schema that over-specified would
    /// reject a request the handler would have understood.</summary>
    private static System.Text.Json.JsonElement Schema(params string[] names)
    {
        var properties = new Dictionary<string, object>();
        foreach (var name in names)
            properties[name] = name is "input" ? new { type = "object" } : new { type = "string" };
        return System.Text.Json.JsonSerializer.SerializeToElement(new { type = "object", properties });
    }
}

/// <summary>
/// Registration. Deliberately NOT a plugin: the ARM router discovers every registered
/// <see cref="IResourceProvider"/>, so a module reaches the front door without a trust list, a manifest or a
/// staged assembly — and this is kernel infrastructure (the same reason the MCP lane is registered this
/// way), not a surface a deployment opts into.
/// </summary>
public static class IntegrationsServiceCollectionExtensions
{
    public static IServiceCollection AddBurbujaMcpIntegration(this IServiceCollection services)
    {
        services.AddOptions<McpClientOptions>()
            .Configure<IConfiguration>((o, cfg) => cfg.GetSection(McpClientOptions.Section).Bind(o));
        services.TryAddSingleton<McpStdioClient>();
        services.AddSingleton<IIntegrationCaller, McpIntegrationCaller>();
        return services;
    }

    public static IServiceCollection AddBurbujaIntegrations(this IServiceCollection services)
    {
        services.AddResourceProvider<IntegrationsResourceProvider>();
        services.AddResourceType<IntegrationProperties, IntegrationHandler>(
            IntegrationConstants.Namespace,
            IntegrationConstants.ResourceType("integrations"));
        return services;
    }

    /// <summary>
    /// I1: the mailbox caller, registered beside the resource type.
    ///
    /// <para>Registered unconditionally, and it refuses rather than pretending: with no secret resolver or no
    /// resolvable token, a mail call answers "nothing was sent" and names the secret it looked for. That is
    /// the honest default for a host whose owner has not attached a mailbox — and it is observable in a lane,
    /// which is what makes the refusal a fact rather than a promise.</para>
    /// </summary>
    public static IServiceCollection AddBurbujaMailIntegration(this IServiceCollection services)
    {
        services.AddOptions<MailIntegrationOptions>()
            .Configure<IConfiguration>((o, cfg) => cfg.GetSection(MailIntegrationOptions.Section).Bind(o));
        services.AddSingleton<IIntegrationCaller, GraphMailCaller>();
        return services;
    }

    /// <summary>
    /// I3: the Notion workspace, the third caller and the same shape as the other two.
    ///
    /// <para>Registered unconditionally like the mailbox: with no token resolved, a call answers "nothing was
    /// sent" and names the secret it looked for, which is what an owner who has not attached a workspace
    /// should see — and what a lane can observe.</para>
    /// </summary>
    public static IServiceCollection AddBurbujaNotionIntegration(this IServiceCollection services)
    {
        services.AddOptions<NotionIntegrationOptions>()
            .Configure<IConfiguration>((o, cfg) => cfg.GetSection(NotionIntegrationOptions.Section).Bind(o));
        services.AddSingleton<IIntegrationCaller, NotionCaller>();
        return services;
    }

    /// <summary>
    /// I2: the browser bridge, registered beside the mailbox.
    ///
    /// <para><b>Both callers are registered, and the KIND is what separates them</b> — a resource with
    /// <c>kind: "browser"</c> reaches the bridge and a <c>kind: "mail"</c> one reaches Graph, with no
    /// resolution order to depend on. Registering only one of them is a valid host: the other kind is
    /// refused with a message that says which kinds ARE served, which is the difference between a
    /// configuration an operator can fix and one they have to guess at.</para>
    ///
    /// <para>Unconditional like the mailbox, and for the same reason: with no bridge socket the probe
    /// answers "no live bridge socket in …; open the reconnect page" rather than pretending, and that is a
    /// fact about the machine rather than a reason not to register the caller.</para>
    /// </summary>
    public static IServiceCollection AddBurbujaBrowserIntegration(this IServiceCollection services)
    {
        services.AddOptions<BrowserBridgeOptions>()
            .Configure<IConfiguration>((o, cfg) => cfg.GetSection(BrowserBridgeOptions.Section).Bind(o));
        services.AddOptions<McpClientOptions>()
            .Configure<IConfiguration>((o, cfg) => cfg.GetSection(McpClientOptions.Section).Bind(o));
        services.AddSingleton<McpStdioClient>();
        services.AddSingleton<IIntegrationCaller, BrowserBridgeCaller>();
        return services;
    }
}
