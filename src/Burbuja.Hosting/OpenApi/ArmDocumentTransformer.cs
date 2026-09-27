using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Arm;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Burbuja.Host.OpenApi;

public sealed class ArmDocumentTransformer(ArmRoutingTable table) : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Info ??= new OpenApiInfo { Title = "Burbuja ARM", Version = "2026-09-01" };
        document.Paths ??= [];
        foreach (var provider in table.Providers)
        {
            foreach (var type in provider.ResourceTypes)
            {
                var path = PathFor(provider.Namespace, type);
                var item = new OpenApiPathItem();
                Add(item, type.Capabilities, ResourceCapabilities.Get, HttpMethod.Get, provider.Namespace, type.Type);
                Add(item, type.Capabilities, ResourceCapabilities.Put, HttpMethod.Put, provider.Namespace, type.Type);
                Add(item, type.Capabilities, ResourceCapabilities.Patch, HttpMethod.Patch, provider.Namespace, type.Type);
                Add(item, type.Capabilities, ResourceCapabilities.Delete, HttpMethod.Delete, provider.Namespace, type.Type);
                Add(item, type.Capabilities, ResourceCapabilities.Actions, HttpMethod.Post, provider.Namespace, type.Type);
                document.Paths[path] = item;
            }
        }

        return Task.CompletedTask;
    }

    private static void Add(
        OpenApiPathItem item,
        ResourceCapabilities caps,
        ResourceCapabilities flag,
        HttpMethod method,
        string ns,
        string type)
    {
        if (!caps.HasFlag(flag))
            return;
        item.AddOperation(method, new OpenApiOperation
        {
            OperationId = $"{ns}_{type}_{method.Method}".Replace('.', '_').Replace('/', '_'),
            Summary = $"{method.Method} {ns}/{type}",
        });
    }

    private static string PathFor(string ns, ResourceTypeDescriptor type)
    {
        if (type.IsTracked)
            return $"/subscriptions/{{subscriptionId}}/resourceGroups/{{resourceGroupName}}/providers/{ns}/{type.Type}/{{name}}";
        return $"/providers/{ns}/{type.Type}/{{name}}";
    }
}
