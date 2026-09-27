using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Burbuja.Core.Arm;

/// <summary>
/// N9: an in-process <see cref="IArmClient"/> that runs the real <see cref="ArmPipeline"/> (the
/// same middleware chain the HTTP front door uses). There is no principal-bypassing system client —
/// the caller supplies <c>x-api-key</c> and full RBAC applies. SSE tails through the namespace's
/// <see cref="IEventStreamResolver"/> (C1), so stream-id shapes stay provider-owned.
/// </summary>
public sealed class ArmPipelineClient(IServiceScopeFactory scopes, string apiVersion) : IArmClient
{
    public async ValueTask<ArmClientResponse> SendAsync(ArmClientRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var scope = scopes.CreateScope();
        var provider = scope.ServiceProvider;
        var pipeline = provider.GetRequiredService<ArmPipeline>();

        var query = request.Query is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(request.Query, StringComparer.OrdinalIgnoreCase);
        // Mirror HttpArmClient.BuildUri: append api-version when the caller did not supply one.
        if (!query.Keys.Any(k => k.Equals("api-version", StringComparison.OrdinalIgnoreCase)))
            query["api-version"] = apiVersion;
        var headers = request.Headers is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(request.Headers, StringComparer.OrdinalIgnoreCase);
        Stream? body = request.Body is null
            ? null
            : new MemoryStream(Encoding.UTF8.GetBytes(request.Body.ToJsonString(JsonWire.Options)));

        var context = new ArmRequestContext
        {
            Method = request.Method.ToUpperInvariant(),
            Path = request.Path,
            Query = query,
            Headers = headers,
            Body = body,
            Services = provider,
        };

        await pipeline.RunAsync(context, ct);

        var responseHeaders = new Dictionary<string, string>(context.ResponseHeaders, StringComparer.OrdinalIgnoreCase);
        if (context.RetryAfter is { } retry && !responseHeaders.ContainsKey("Retry-After"))
            responseHeaders["Retry-After"] = ((int)retry.TotalSeconds).ToString(CultureInfo.InvariantCulture);

        return new ArmClientResponse(context.StatusCode, responseHeaders, ToJsonNode(context.Result), context.Error);
    }

    private static JsonNode? ToJsonNode(object? result) => result switch
    {
        null => null,
        JsonNode node => node,
        _ => JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(result, JsonWire.Options)),
    };

    public async IAsyncEnumerable<ArmSseEvent> StreamAsync(
        string path,
        string? lastEventId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        string streamId;
        IEventLog events;
        using (var scope = scopes.CreateScope())
        {
            var provider = scope.ServiceProvider;
            var table = provider.GetRequiredService<ArmRoutingTable>();
            if (!ResourceId.TryParse(path, table, out var id, out _) || id.DataPlaneSegment is null)
                yield break;

            var resolver = provider.GetServices<IEventStreamResolver>()
                .FirstOrDefault(r => string.Equals(r.Namespace, id.Namespace, StringComparison.OrdinalIgnoreCase));
            var canonical = (id with { Action = null, DataPlaneSegment = null, DataPlanePath = null }).Canonical;
            streamId = resolver is not null && resolver.TryResolve(id, out var resolved)
                ? resolved
                : EventStreamIds.Resource(canonical);
            events = provider.GetRequiredService<IEventLog>();
        }

        var last = long.TryParse(lastEventId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        await foreach (var ev in events.TailAsync(streamId, last, ct))
        {
            var data = ev.Payload?.ToJsonString() ?? "{}";
            yield return new ArmSseEvent(ev.Seq.ToString(CultureInfo.InvariantCulture), ev.Type, data);
        }
    }
}
