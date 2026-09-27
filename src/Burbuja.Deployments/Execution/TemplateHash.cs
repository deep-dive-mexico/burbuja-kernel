using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Json;
using Burbuja.Deployments.Abstractions;

namespace Burbuja.Deployments.Execution;

public static class TemplateHash
{
    public static string Compute(DeploymentProperties props)
    {
        var authored = new JsonObject
        {
            ["template"] = JsonSerializer.SerializeToNode(props.Template, JsonWire.Options),
            ["parameters"] = props.Parameters.DeepClone(),
            ["mode"] = JsonSerializer.SerializeToNode(props.Mode, JsonWire.Options),
            ["requiresApproval"] = props.RequiresApproval,
        };
        var json = authored.ToJsonString(JsonWire.Options);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
