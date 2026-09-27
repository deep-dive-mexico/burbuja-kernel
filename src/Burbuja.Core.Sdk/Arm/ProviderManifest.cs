using System.Text.Json;
using Burbuja.Core.Abstractions.Arm;

namespace Burbuja.Core.Arm;

public sealed record ProviderManifest(
    string Namespace,
    string RegistrationState,
    IReadOnlyList<ProviderResourceTypeManifest> ResourceTypes);

public sealed record ProviderResourceTypeManifest(
    string ResourceType,
    IReadOnlyList<string> ApiVersions,
    string DefaultApiVersion,
    IReadOnlyList<string> Locations,
    IReadOnlyList<string> Capabilities,
    bool IsTracked,
    bool IsAsyncPut,
    IReadOnlyList<ProviderActionManifest> Actions,
    IReadOnlyList<ProviderDataPlaneRouteManifest> DataPlaneRoutes,
    IReadOnlyList<string> Children,
    JsonElement? PropertiesSchema,
    IReadOnlyList<string> RbacActions);

public sealed record ProviderActionManifest(
    string Name,
    bool IsAsync,
    bool IsDataAction,
    JsonElement? RequestSchema);

public sealed record ProviderDataPlaneRouteManifest(string Segment, string Kind);

public static class ProviderManifestMapper
{
    public static ProviderManifest From(IResourceProvider provider, string registrationState) =>
        new(provider.Namespace, registrationState, provider.ResourceTypes.Select(MapType).ToArray());

    public static ProviderResourceTypeManifest MapType(ResourceTypeDescriptor type) =>
        new(
            type.Type,
            type.ApiVersions,
            type.ApiVersions.Count > 0 ? type.ApiVersions[^1] : ArmConstants.ApiVersion,
            type.Locations,
            CapabilityNames(type.Capabilities),
            type.IsTracked,
            type.IsAsyncPut,
            type.Actions.Select(a => new ProviderActionManifest(a.Name, a.IsAsync, a.IsDataAction, a.RequestSchema)).ToArray(),
            type.DataPlaneRoutes.Select(r => new ProviderDataPlaneRouteManifest(r.Segment, r.Kind.ToString())).ToArray(),
            type.Children,
            type.PropertiesSchema.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                ? null
                : type.PropertiesSchema.Clone(),
            type.RbacActions);

    public static string[] CapabilityNames(ResourceCapabilities caps) =>
        Enum.GetValues<ResourceCapabilities>()
            .Where(f => f != 0 && caps.HasFlag(f))
            .Select(f => f.ToString())
            .ToArray();
}
