using System.Collections;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Burbuja.Core.Abstractions.Redaction;
using Burbuja.Core.Abstractions.Secrets;
using Burbuja.Core.Process;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Burbuja.Integrations;

/// <summary>One bounded MCP exchange owned by the integration action; discovery never changes declared authority.</summary>
public sealed class McpIntegrationCaller(
    McpStdioClient stdio,
    IRedactor redactor,
    IOptions<McpClientOptions> options,
    ISecretResolver? secrets = null) : IIntegrationCaller
{
    public IReadOnlyList<string> Kinds { get; } = [IntegrationConstants.KindMcp];

    public async ValueTask<IReadOnlyList<string>> ListToolsAsync(IntegrationProperties integration, CancellationToken ct)
    {
        var prefix = Prefix(integration);
        var transport = Require(integration);
        using var deadline = Deadline(ct);
        IReadOnlyList<string> tools;
        if (transport.Mode.Equals("stdio", StringComparison.OrdinalIgnoreCase))
        {
            var environment = await EnvironmentAsync(transport, deadline.Token);
            try
            {
                tools = await stdio.ListToolsAsync(transport, environment, deadline.Token,
                    isolateEnvironment: true).ConfigureAwait(false);
            }
            catch (McpTransportException ex)
            {
                throw new McpTransportException(Sanitize(ex.Message, transport, environment));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new McpTransportException("MCP discovery exceeded its deadline.");
            }
        }
        else
        {
            try
            {
                var connection = await HttpClientAsync(transport, deadline.Token);
                await using var client = connection.Client;
                var names = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                string? cursor = null;
                var pages = 0;
                do
                {
                    if (++pages > options.Value.MaxListPages || (cursor is not null && !seen.Add(cursor)))
                        throw new McpTransportException("MCP discovery exceeded its pagination bound or repeated a cursor.");
                    var page = await client.ListToolsAsync(new ListToolsRequestParams { Cursor = cursor }, deadline.Token);
                    names.AddRange(page.Tools.Select(tool => tool.Name));
                    if (names.Count > options.Value.MaxTools)
                        throw new McpTransportException("MCP discovery exceeded its tool count bound.");
                    cursor = page.NextCursor;
                } while (cursor is not null);
                tools = names;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new McpTransportException("MCP discovery exceeded its deadline.");
            }
            catch (Exception ex) when (ex is not (OperationCanceledException or McpTransportException))
            {
                throw new McpTransportException($"MCP HTTP discovery failed ({ex.GetType().Name}).");
            }
        }
        if (tools.Count > options.Value.MaxTools || tools.Any(tool =>
                !Regex.IsMatch(tool, "^[a-zA-Z0-9_.-]{1,128}$")) || tools.Distinct(StringComparer.Ordinal).Count() != tools.Count)
            throw new McpTransportException("MCP discovery returned too many, duplicate or invalid tool names.");
        return tools.Select(tool => prefix + tool).ToArray();
    }

    public async ValueTask<IntegrationCallResult> CallAsync(
        IntegrationProperties integration, string tool, JsonNode input, CancellationToken ct)
    {
        try
        {
            var prefix = Prefix(integration);
            if (!tool.StartsWith(prefix, StringComparison.Ordinal) || tool.Length == prefix.Length)
                return new(false, null, $"The tool must belong to '{prefix}'.");
            var transport = Require(integration);
            using var deadline = Deadline(ct);
            IntegrationCallResult result;
            IReadOnlyDictionary<string, string>? environment = null;
            if (transport.Mode.Equals("stdio", StringComparison.OrdinalIgnoreCase))
            {
                environment = await EnvironmentAsync(transport, deadline.Token);
                result = await stdio.CallRawAsync(transport, environment,
                    tool[prefix.Length..], input, deadline.Token, isolateEnvironment: true).ConfigureAwait(false);
            }
            else
            {
                var connection = await HttpClientAsync(transport, deadline.Token);
                await using var client = connection.Client;
                var response = await client.CallToolAsync(new CallToolRequestParams
                {
                    Name = tool[prefix.Length..],
                    Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(input.ToJsonString()),
                }, deadline.Token);
                var serialized = JsonSerializer.Serialize(response, McpJsonUtilities.DefaultOptions);
                if (!string.IsNullOrEmpty(connection.Secret))
                    serialized = serialized.Replace(connection.Secret, "[redacted]", StringComparison.Ordinal);
                result = new(response.IsError != true,
                    JsonNode.Parse(serialized),
                    response.IsError == true ? "The MCP server reported a tool execution error." : null);
            }
            return result with
            {
                Output = result.Output is null ? null : JsonNode.Parse(Sanitize(result.Output.ToJsonString(), transport, environment)),
                Error = result.Error is null ? null : Sanitize(result.Error, transport, environment),
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(false, null, "MCP call exceeded its deadline; execution may have reached the remote server.");
        }
        catch (McpTransportException ex)
        {
            return new(false, null, redactor.Redact(ex.Message));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(false, null, $"MCP call failed ({ex.GetType().Name}); execution may have reached the remote server.");
        }
    }

    private static string Prefix(IntegrationProperties integration)
    {
        var server = integration.ServerName;
        if (server is null || !Regex.IsMatch(server, "^[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}$")
            || server.Contains("__", StringComparison.Ordinal))
            throw new McpTransportException("A generic MCP integration requires a serverName of 1–64 letters, digits, '-' or '_' without '__'.");
        return $"mcp__{server}__";
    }

    private static IntegrationTransport Require(IntegrationProperties integration)
    {
        var transport = integration.Transport
            ?? throw new McpTransportException("The MCP integration requires transport.");
        transport = transport with { Mode = transport.Mode?.Trim().ToLowerInvariant() ?? "" };
        if (transport.Mode is not ("stdio" or "http"))
            throw new McpTransportException($"Unsupported MCP transport '{transport.Mode}'.");
        if (transport.Mode == "stdio" && string.IsNullOrWhiteSpace(transport.Command))
            throw new McpTransportException("The MCP stdio transport requires command.");
        if (transport.Mode == "stdio" && transport.SecretRef is not null)
            throw new McpTransportException("Use environmentSecretRefs for stdio authentication.");
        if (transport.Mode == "http" && (transport.EnvironmentSecretRefs is { Count: > 0 }
            || !Uri.TryCreate(transport.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
            throw new McpTransportException("MCP HTTP requires an absolute http(s) URL and uses secretRef for bearer authentication.");
        return transport;
    }

    private CancellationTokenSource Deadline(CancellationToken ct)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(options.Value.Timeout);
        return deadline;
    }

    private async Task<(McpClient Client, string? Secret)> HttpClientAsync(IntegrationTransport transport, CancellationToken ct)
    {
        var headers = new Dictionary<string, string>();
        string? resolvedSecret = null;
        if (transport.SecretRef is { Length: > 0 } reference)
        {
            var token = secrets is null ? null : await secrets.ResolveAsync(reference, ct);
            if (string.IsNullOrWhiteSpace(token))
                throw new McpTransportException($"MCP secret reference '{reference}' could not be resolved; no request was sent.");
            headers["Authorization"] = new AuthenticationHeaderValue("Bearer", token).ToString();
            resolvedSecret = token;
        }
        var http = new HttpClient(new McpResponseLimitHandler(options.Value.MaxLineChars))
        { Timeout = Timeout.InfiniteTimeSpan };
        var wire = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(transport.Url!),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = headers,
            ConnectionTimeout = options.Value.Timeout,
            EnableStandaloneGetStream = false,
        }, http, loggerFactory: null, ownsHttpClient: true);
        try
        {
            var client = await McpClient.CreateAsync(wire, new ModelContextProtocol.Client.McpClientOptions
            {
                ProtocolVersion = "2025-06-18",
                InitializationTimeout = options.Value.Timeout,
            }, cancellationToken: ct);
            return (client, resolvedSecret);
        }
        catch
        {
            await wire.DisposeAsync();
            throw;
        }
    }

    private string Sanitize(string text, IntegrationTransport transport, IReadOnlyDictionary<string, string>? environment)
    {
        foreach (var key in transport.EnvironmentSecretRefs?.Keys ?? [])
            if (environment?.TryGetValue(key, out var secret) == true)
                text = text.Replace(secret, "[redacted]", StringComparison.Ordinal);
        return redactor.Redact(text);
    }

    private async Task<IReadOnlyDictionary<string, string>> EnvironmentAsync(IntegrationTransport transport, CancellationToken ct)
    {
        var parent = System.Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .ToDictionary(pair => (string)pair.Key, pair => (string)pair.Value!, StringComparer.OrdinalIgnoreCase);
        var environment = ProcessEnvironmentPolicy.Build(parent, new Dictionary<string, string>());
        foreach (var (key, reference) in transport.EnvironmentSecretRefs ?? new Dictionary<string, string>())
        {
            if (string.IsNullOrWhiteSpace(key) || key.Contains('=') || key.Contains('\0')
                || ProcessEnvironmentPolicy.Allowlist.Contains(key))
                throw new McpTransportException("Invalid MCP secret environment variable.");
            var value = secrets is null ? null : await secrets.ResolveAsync(reference, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(value))
                throw new McpTransportException($"MCP secret reference '{reference}' could not be resolved; no process was started.");
            environment[key] = value;
        }
        return environment;
    }
}
