using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Burbuja.Cluster.Abstractions;

namespace Burbuja.Cluster;

/// <summary>An operator's view of a controller over HTTP: what the command-line tool and tests use.</summary>
public sealed class ClusterClient
{
    private readonly HttpClient _http;
    private readonly Uri _base;

    public ClusterClient(HttpClient http, Uri controller, string operatorToken)
    {
        _http = http;
        _base = controller;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", operatorToken);
    }

    public async Task<ClusterStatus> StatusAsync(CancellationToken ct = default) =>
        await ReadAsync<ClusterStatus>(await _http.GetAsync(new Uri(_base, "/cluster/v1/status"), ct), ct);

    public async Task<JoinTokenGrant> CreateJoinTokenAsync(CancellationToken ct = default) =>
        await ReadAsync<JoinTokenGrant>(await _http.PostAsync(new Uri(_base, "/cluster/v1/join-tokens"), null, ct), ct);

    /// <summary>Create or change a workload. Returns the reasons when the controller declines the specification.</summary>
    public async Task<(WorkloadView? Workload, IReadOnlyList<string> Errors)> ApplyAsync(WorkloadSpec spec, CancellationToken ct = default)
    {
        using var content = new ByteArrayContent(ClusterJson.WriteBytes(spec));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await _http.PutAsync(new Uri(_base, "/cluster/v1/workloads/" + Uri.EscapeDataString(spec.Name)), content, ct);
        var body = await response.Content.ReadAsByteArrayAsync(ct);
        if (response.IsSuccessStatusCode) return (ClusterJson.Read<WorkloadView>(body), []);
        var errors = ClusterJson.Read<JsonObject>(body)?["errors"] as JsonArray;
        return (null, errors is null ? [$"the controller answered {(int)response.StatusCode}"] : [.. errors.Select(e => e?.ToString() ?? "")]);
    }

    public async Task<bool> DeleteAsync(string workload, CancellationToken ct = default)
    {
        using var response = await _http.DeleteAsync(new Uri(_base, "/cluster/v1/workloads/" + Uri.EscapeDataString(workload)), ct);
        return response.IsSuccessStatusCode;
    }

    /// <param name="action"><c>activate</c>, <c>cordon</c> or <c>drain</c>.</param>
    public async Task<bool> SetNodeAsync(string nodeId, string action, CancellationToken ct = default)
    {
        using var response = await _http.PostAsync(new Uri(_base, $"/cluster/v1/nodes/{Uri.EscapeDataString(nodeId)}/{action}"), null, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> ForgetNodeAsync(string nodeId, CancellationToken ct = default)
    {
        using var response = await _http.DeleteAsync(new Uri(_base, "/cluster/v1/nodes/" + Uri.EscapeDataString(nodeId)), ct);
        return response.IsSuccessStatusCode;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"The controller answered {(int)response.StatusCode}.", null, response.StatusCode);
            return ClusterJson.Read<T>(await response.Content.ReadAsByteArrayAsync(ct))
                ?? throw new InvalidDataException("The controller's answer could not be read.");
        }
    }
}
