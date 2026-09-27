using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Arm;

namespace Burbuja.Core.Arm;

internal static class ArmBody
{
    public static JsonNode? Parse(ArmRequestContext context)
    {
        if (context.BufferedBody.IsEmpty)
            return null;
        return JsonNode.Parse(context.BufferedBody.Span);
    }

    public static JsonObject? ParseObject(ArmRequestContext context) => Parse(context) as JsonObject;

    public static string Sha256Hex(ArmRequestContext context)
    {
        if (context.BufferedBody.IsEmpty)
            return "empty";
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(context.BufferedBody.Span)).ToLowerInvariant();
    }
}
