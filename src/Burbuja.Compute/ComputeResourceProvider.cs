using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Compute.Abstractions;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Arm;

namespace Burbuja.Compute;

public sealed class ComputeResourceProvider : IResourceProvider
{
    public ComputeResourceProvider(BackendRegistry backends)
    {
        _ = backends;
        ResourceTypes =
        [
            DescriptorBuilder.For("workspaces")
                .Properties<ComputeWorkspaceProperties>()
                .Schema(SchemaBuilder.Object())
                .Capabilities(
                    ResourceCapabilities.Get
                    | ResourceCapabilities.Put
                    | ResourceCapabilities.Patch
                    | ResourceCapabilities.Delete
                    | ResourceCapabilities.ListByResourceGroup
                    | ResourceCapabilities.List
                    | ResourceCapabilities.Actions)
                .AsyncPut()
                .AsyncDelete()
                .Actions(
                    new ResourceActionDescriptor("start", true),
                    new ResourceActionDescriptor("stop", true),
                    new ResourceActionDescriptor("deallocate", true),
                    new ResourceActionDescriptor("snapshot", true, IsDataAction: true, RequestSchema: JsonSerializer.SerializeToElement(new { type = "object", required = new[] { "name" }, properties = new { name = new { type = "string" } } })),
                    new ResourceActionDescriptor("exec", false, IsDataAction: true, RequestSchema: JsonSerializer.SerializeToElement(new { type = "object", required = new[] { "command" }, properties = new { command = new { type = "string" }, cwd = new { type = "string" }, timeoutSeconds = new { type = "integer" } } })),
                    // M11 P6: a state REPORT — every call must run. See ResourceActionDescriptor.Idempotent.
                    new ResourceActionDescriptor("probe", false, Idempotent: false))
                .Children("snapshots")
                .DataPlane(
                    new DataPlaneRouteDescriptor("events", DataPlaneKind.ServerSentEvents),
                    new DataPlaneRouteDescriptor("attach", DataPlaneKind.Stream))
                .Build(),
            DescriptorBuilder.For("workspaces/snapshots")
                .Properties<ComputeSnapshotProperties>()
                .Schema(SchemaBuilder.Object())
                .Capabilities(
                    ResourceCapabilities.Get
                    | ResourceCapabilities.Delete
                    | ResourceCapabilities.ListByResourceGroup
                    | ResourceCapabilities.List)
                .Children()
                .DataPlane(new DataPlaneRouteDescriptor("download", DataPlaneKind.Download))
                .Build(),
            DescriptorBuilder.For("backends")
                .Properties<ComputeBackendProjection>()
                .Schema(SchemaBuilder.Object())
                .Tracked(false)
                .Capabilities(ResourceCapabilities.Get | ResourceCapabilities.List | ResourceCapabilities.Actions)
                .Actions(new ResourceActionDescriptor("probe", false, Idempotent: false))
                .Build(),
            DescriptorBuilder.For("images")
                .Properties<ComputeBackendProjection>()
                .Schema(SchemaBuilder.Object())
                .Tracked(false)
                .Capabilities(ResourceCapabilities.Get | ResourceCapabilities.List)
                .Build(),
            DescriptorBuilder.For("sizes")
                .Properties<ComputeBackendProjection>()
                .Schema(SchemaBuilder.Object())
                .Tracked(false)
                .Capabilities(ResourceCapabilities.Get | ResourceCapabilities.List)
                .Build(),
        ];
    }

    public string Namespace => ComputeConstants.Namespace;

    public IReadOnlyList<ResourceTypeDescriptor> ResourceTypes { get; }

    public ValueTask OnSubscriptionStateChangedAsync(SubscriptionNotification notification, CancellationToken ct) => default;
}
