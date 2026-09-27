using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Burbuja.Mcp.Abstractions;

/// <summary>One kernel tool as the MCP client sees it.</summary>
public sealed record McpToolExport(
    string McpName,
    string ToolName,
    string Description,
    JsonNode InputSchema,
    string? RiskHint);

/// <summary>
/// Which tools a kernel MCP server offers, and under what names. The indirection exists so the
/// server can be told a catalogue instead of discovering one at request time: the tool set is
/// fixed when the host boots, and a client that listed tools must see the same names when it calls
/// them.
/// </summary>
public interface IMcpToolExporter
{
    IReadOnlyList<McpToolExport> Export();
}

/// <summary>
/// Client-side tool naming: <c>mcp__&lt;server&gt;__&lt;tool&gt;</c>, the shape Claude Code hands
/// back for an MCP tool. A name that cannot survive normalisation (a remote tool with a dot, a
/// space or something longer than the limit) keeps its identity through a 12-hex-character digest
/// suffix instead of silently colliding with a different tool.
/// </summary>
public static class McpToolName
{
    public const string Separator = "__";
    public const string Prefix = "mcp" + Separator;
    private const int DigestChars = 12;

    public static string For(string server, string tool)
    {
        var normalizedServer = Normalize(server);
        var normalizedTool = Normalize(tool);
        var lossy = !normalizedServer.Equals(server, StringComparison.Ordinal)
                    || !normalizedTool.Equals(tool, StringComparison.Ordinal);
        var name = Prefix + normalizedServer + Separator + normalizedTool;
        return lossy ? name + Separator + Digest(server + "\u0000" + tool)[..DigestChars] : name;
    }

    /// <summary>True when <paramref name="name"/> belongs to <paramref name="server"/>.</summary>
    public static bool IsFor(string name, string server)
    {
        var prefix = Prefix + Normalize(server) + Separator;
        return name.StartsWith(prefix, StringComparison.Ordinal);
    }

    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_');
        return builder.ToString();
    }

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
