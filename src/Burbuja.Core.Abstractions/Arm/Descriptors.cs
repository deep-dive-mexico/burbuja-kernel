using System.Text.Json;

namespace Burbuja.Core.Abstractions.Arm;

[Flags]
public enum ResourceCapabilities
{
    Get = 1,
    Put = 2,
    Patch = 4,
    Delete = 8,
    ListByResourceGroup = 16,
    ListBySubscription = 32,
    Actions = 64,
    List = 128,
}

public enum DataPlaneKind
{
    ServerSentEvents,
    Stream,
    Download,
}

/// <summary>
/// One declared verb on a resource type.
/// </summary>
/// <param name="Name">The segment a POST path ends in (<c>…/probe</c>).</param>
/// <param name="IsAsync">The verb answers 202 and tracks an operation.</param>
/// <param name="IsDataAction">The descriptor SAYS this is a data-plane act. Declared for the manifest
/// projection; <b>not</b> what authorisation reads — see <c>Middleware.cs</c> and the M10 H2 note.</param>
/// <param name="RequestSchema">The body shape, when the verb takes one.</param>
/// <param name="Idempotent">
/// <para><b>M11 P6 — false means "every call is a NEW call".</b> The ARM front door stores every 2xx of a
/// non-GET request under a key derived from the request itself, so a caller that sends the same body twice
/// gets the FIRST answer back and the second execution never happens. That is right for a create and wrong
/// for a <em>report</em>: a heartbeat or a state probe whose whole purpose is to say "still here, at this
/// cursor, as of now" would be answered from a cache for 24 hours and the resource would stop moving.</para>
/// <para>Declaring the verb non-idempotent skips the idempotency seam entirely for it — no lookup and no
/// store — which is both the correct semantics and, measured on the M10 release host, the difference
/// between 36 268 stored probe responses in ten hours and none.</para>
/// </param>
public sealed record ResourceActionDescriptor(
    string Name,
    bool IsAsync,
    bool IsDataAction = false,
    JsonElement? RequestSchema = null,
    bool Idempotent = true);

public sealed record DataPlaneRouteDescriptor(string Segment, DataPlaneKind Kind);

public sealed record ResourceTypeDescriptor(
    string Type,
    IReadOnlyList<string> ApiVersions,
    ResourceCapabilities Capabilities,
    bool IsTracked,
    bool IsAsyncPut,
    bool IsAsyncDelete,
    IReadOnlyList<string> Locations,
    IReadOnlyList<ResourceActionDescriptor> Actions,
    IReadOnlyList<DataPlaneRouteDescriptor> DataPlaneRoutes,
    IReadOnlyList<string> Children,
    Type PropertiesType,
    JsonElement PropertiesSchema,
    IReadOnlyList<string> RbacActions);

public enum SubscriptionState
{
    Enabled,
    Warned,
    Suspended,
    Deleted,
}

public enum ProviderRegistrationState
{
    NotRegistered,
    Registering,
    Registered,
    Unregistering,
    Unregistered,
}

public sealed record SubscriptionNotification(string SubscriptionId, SubscriptionState State);

public interface IResourceProvider
{
    string Namespace { get; }
    IReadOnlyList<ResourceTypeDescriptor> ResourceTypes { get; }
    ValueTask OnSubscriptionStateChangedAsync(SubscriptionNotification notification, CancellationToken ct);
}
