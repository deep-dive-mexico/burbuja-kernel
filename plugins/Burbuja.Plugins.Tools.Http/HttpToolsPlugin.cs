using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Burbuja.Agents.Abstractions.Tools;
using Burbuja.Core.Abstractions.Plugin;
using Microsoft.Extensions.DependencyInjection;

namespace Burbuja.Plugins.Tools.Http;

public sealed class HttpToolsPlugin : IBurbujaPlugin
{
    public PluginManifest Manifest { get; } = new(
        "Tools.Http",
        "HTTP tools",
        "1.0.0",
        PluginContracts.Version,
        "Burbuja.Plugins.Tools.Http.dll",
        typeof(HttpToolsPlugin).FullName!,
        PluginLoadKind.Static,
        ["tools.http"],
        ["agents.tool"],
        [PluginCapability.Network],
        ["all", "worker"],
        null,
        true,
        [],
        330);

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IAgentTool, HttpGetTool>();
        services.AddSingleton<IAgentTool, HttpPostTool>();
    }
}

internal static class Ssrf
{
    public static void Guard(string url, IReadOnlyList<string> allow)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("invalid url");
        if (allow.Count > 0 && !allow.Any(h => uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("host not allowlisted");
        if (IPAddress.TryParse(uri.Host, out var ip) && IsPrivate(ip))
            throw new InvalidOperationException("ssrf");
        if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("metadata.google.internal", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("ssrf");
        try
        {
            var addresses = Dns.GetHostAddresses(uri.Host);
            if (addresses.Any(IsPrivate))
                throw new InvalidOperationException("ssrf");
        }
        catch (SocketException)
        {
            // unresolved host is not a private literal
        }
    }

    private static bool IsPrivate(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
            return true;
        var bytes = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork && bytes.Length >= 4)
        {
            return bytes[0] == 10
                   || bytes[0] == 127
                   || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                   || (bytes[0] == 192 && bytes[1] == 168)
                   || (bytes[0] == 169 && bytes[1] == 254);
        }

        return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal;
    }
}

public sealed class HttpGetTool : IAgentTool
{
    public ToolDescriptor Descriptor { get; } = new(
        "http_get",
        "HTTP GET",
        new JsonObject { ["type"] = "object", ["additionalProperties"] = true },
        true,
        false,
        true,
        "http",
        []);

    public async ValueTask<ToolResult> ExecuteAsync(ToolExecutionContext context, JsonNode input, GatePass pass, CancellationToken ct)
    {
        var url = input["url"]?.GetValue<string>() ?? "";
        Ssrf.Guard(url, []);
        using var client = new HttpClient();
        using var resp = await client.GetAsync(url, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        return new ToolResult(new JsonObject { ["status"] = (int)resp.StatusCode, ["body"] = body }, !resp.IsSuccessStatusCode);
    }
}

public sealed class HttpPostTool : IAgentTool
{
    public ToolDescriptor Descriptor { get; } = new(
        "http_post",
        "HTTP POST",
        new JsonObject { ["type"] = "object", ["additionalProperties"] = true },
        false,
        false,
        false,
        "http",
        []);

    public async ValueTask<ToolResult> ExecuteAsync(ToolExecutionContext context, JsonNode input, GatePass pass, CancellationToken ct)
    {
        var url = input["url"]?.GetValue<string>() ?? "";
        Ssrf.Guard(url, []);
        using var client = new HttpClient();
        using var resp = await client.PostAsync(url, new StringContent(input["body"]?.ToJsonString() ?? ""), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        return new ToolResult(new JsonObject { ["status"] = (int)resp.StatusCode, ["body"] = body }, !resp.IsSuccessStatusCode);
    }
}
