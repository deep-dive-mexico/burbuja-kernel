using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Arm;

namespace Example.Workbench.Plugin;

public sealed record NoteProperties(string Text);

public sealed class NotesProvider : IResourceProvider
{
    public string Namespace => WorkbenchPlugin.Namespace;

    public IReadOnlyList<ResourceTypeDescriptor> ResourceTypes { get; } =
    [
        DescriptorBuilder.For("notes")
            .Properties<NoteProperties>()
            .Schema(SchemaBuilder.Parse("""
                {"type":"object","required":["text"],"additionalProperties":false,
                 "properties":{"text":{"type":"string","minLength":1,"maxLength":1000}}}
                """))
            .Capabilities(ResourceCapabilities.Get | ResourceCapabilities.Put | ResourceCapabilities.Delete
                | ResourceCapabilities.List | ResourceCapabilities.ListByResourceGroup)
            .DataPlane(new DataPlaneRouteDescriptor("events", DataPlaneKind.ServerSentEvents))
            .RbacActions(WorkbenchRoles.Read, WorkbenchRoles.Write, WorkbenchRoles.Delete)
            .Build(),
    ];

    public ValueTask OnSubscriptionStateChangedAsync(SubscriptionNotification notification, CancellationToken ct) => default;
}
