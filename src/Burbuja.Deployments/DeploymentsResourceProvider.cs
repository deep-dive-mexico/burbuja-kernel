using System.Text.Json;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Arm;
using Burbuja.Deployments.Abstractions;
using Burbuja.Deployments.Handlers;
using Burbuja.Deployments.Targets;

namespace Burbuja.Deployments;

public sealed class DeploymentsResourceProvider : IResourceProvider
{
    public DeploymentsResourceProvider(TargetRegistry registry)
    {
        var kinds = registry.All.Select(t => t.Kind).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        ResourceTypes =
        [
            DescriptorBuilder.For("environments")
                .Properties<EnvironmentProperties>()
                .Schema(LoadSchema("environment.schema.json", kinds))
                .Capabilities(
                    ResourceCapabilities.Get
                    | ResourceCapabilities.Put
                    | ResourceCapabilities.Patch
                    | ResourceCapabilities.Delete
                    | ResourceCapabilities.ListByResourceGroup
                    | ResourceCapabilities.ListBySubscription
                    | ResourceCapabilities.Actions)
                .Actions(new ResourceActionDescriptor("probe", false, Idempotent: false))
                .Children("deployments")
                .Build(),
            DescriptorBuilder.For("environments/deployments")
                .Properties<DeploymentProperties>()
                .Schema(LoadSchema("deployment.schema.json", []))
                .Capabilities(
                    ResourceCapabilities.Get
                    | ResourceCapabilities.Put
                    | ResourceCapabilities.Delete
                    | ResourceCapabilities.ListByResourceGroup
                    | ResourceCapabilities.Actions)
                .AsyncPut()
                .AsyncDelete()
                .Actions(
                    new ResourceActionDescriptor("validate", false),
                    new ResourceActionDescriptor("whatIf", false),
                    new ResourceActionDescriptor("cancel", false))
                .DataPlane(new DataPlaneRouteDescriptor("events", DataPlaneKind.ServerSentEvents))
                .Build(),
            DescriptorBuilder.For("targets")
                .Properties<TargetProjection>()
                .Tracked(false)
                .Capabilities(ResourceCapabilities.Get | ResourceCapabilities.List)
                .Build(),
        ];
    }

    public string Namespace => DeploymentsConstants.Namespace;

    public IReadOnlyList<ResourceTypeDescriptor> ResourceTypes { get; }

    public ValueTask OnSubscriptionStateChangedAsync(SubscriptionNotification notification, CancellationToken ct) => default;

    internal static JsonElement LoadSchema(string fileName, IReadOnlyList<string> kinds)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Schemas", fileName);
        if (!File.Exists(path))
            path = Path.Combine(AppContext.BaseDirectory, fileName);
        string json;
        if (File.Exists(path))
            json = File.ReadAllText(path);
        else
        {
            var asm = typeof(DeploymentsResourceProvider).Assembly;
            var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
            if (name is null)
                return SchemaBuilder.Object();
            using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            json = reader.ReadToEnd();
        }

        if (kinds.Count > 0)
        {
            var enumJson = string.Join(',', kinds.Select(k => "\"" + k + "\""));
            json = json.Replace("\"enum\": [\"in-memory\", \"local-process\"]", "\"enum\": [" + enumJson + "]", StringComparison.Ordinal);
        }

        return SchemaBuilder.Parse(json);
    }
}
