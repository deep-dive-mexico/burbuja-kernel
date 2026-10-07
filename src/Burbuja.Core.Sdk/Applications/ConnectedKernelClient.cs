using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Applications;
using Burbuja.Core.Abstractions.Approvals;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Artifacts;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Json;
using Burbuja.Core.Abstractions.Leases;
using Burbuja.Core.Abstractions.Outbox;
using Burbuja.Core.Abstractions.Store;
using Burbuja.Core.Arm;

namespace Burbuja.Core.Sdk.Applications;

public sealed class KernelUnavailableException(string operation, string? requestId, Exception inner)
    : Exception("Kernel unavailable; reconcile the request receipt before repeating a write.", inner)
{
    public string Operation { get; } = operation;
    public string? RequestId { get; } = requestId;
}

public sealed class KernelRequestCanceledException(string operation, string requestId, Exception inner, CancellationToken ct)
    : OperationCanceledException("Kernel request canceled; reconcile its receipt before repeating a write.", inner, ct)
{
    public string Operation { get; } = operation;
    public string RequestId { get; } = requestId;
}

/// <summary>Typed remote stores. It never opens a database or starts a local kernel runtime.</summary>
public sealed partial class ConnectedKernelClient : IResourceStore, IEventLog, ILeaseStore,
    IOutbox, IOutboxStore, IIdempotencyStore, IApprovalStore, IOperationTracker, IArtifactStore
{
    private readonly HttpClient _http;
    private readonly Uri _root;
    private readonly string _credential;
    private readonly ApplicationScope _expected;
    private readonly string? _actor;
    private bool _verified;

    public ConnectedKernelClient(HttpClient http, Uri kernel, string credential, ApplicationScope expected,
        string? actor = null, bool allowInsecureDevelopment = false)
    {
        if (!kernel.IsAbsoluteUri || kernel.UserInfo.Length != 0 || kernel.Query.Length != 0 || kernel.Fragment.Length != 0
            || (kernel.Scheme != "https" && !(allowInsecureDevelopment && kernel.Scheme == "http" && expected.Environment == "development")))
            throw new ArgumentException("Use HTTPS, or explicitly allow HTTP in development.", nameof(kernel));
        _http = http;
        _root = new Uri(kernel.AbsoluteUri.TrimEnd('/') + "/kernel/v1/");
        _credential = credential;
        _expected = expected;
        _actor = actor;
    }

    public async Task<ApplicationHandshake> ConnectAsync(CancellationToken ct = default)
    {
        using var message = Message(HttpMethod.Get, "handshake");
        using var response = await _http.SendAsync(message, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var handshake = await JsonSerializer.DeserializeAsync<ApplicationHandshake>(
            await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), JsonWire.Options, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Missing kernel handshake.");
        string[] required = ["scoped-storage", "conditional-resources", "atomic-create", "event-chains", "event-sse", "artifacts", "write-receipts"];
        if (handshake.Protocol != "burbuja-applications/1" || handshake.Scope != _expected
            || required.Any(capability => !handshake.Capabilities.Contains(capability, StringComparer.Ordinal)))
            throw new InvalidOperationException("Kernel protocol or registered application scope differs from this client.");
        _verified = true;
        return handshake;
    }

    private HttpRequestMessage Message(HttpMethod method, string path)
    {
        var message = new HttpRequestMessage(method, new Uri(_root, path));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _credential);
        message.Headers.Add("X-Burbuja-Application", Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(_expected, JsonWire.Options))));
        return message;
    }

    private static JsonObject Args(params (string Key, object? Value)[] values)
    {
        var args = new JsonObject();
        foreach (var (key, value) in values)
            args[key] = value is JsonNode node ? node.DeepClone() : JsonSerializer.SerializeToNode(value, JsonWire.Options);
        return args;
    }

    public async ValueTask<ApplicationStorageReply> ExecuteAsync(ApplicationStorageCall call, CancellationToken ct = default)
    {
        if (!_verified) throw new InvalidOperationException("Connect and negotiate the application scope first.");
        using var message = Message(HttpMethod.Post, "storage");
        message.Content = new StringContent(JsonSerializer.Serialize(call, JsonWire.Options), Encoding.UTF8, "application/json");
        try
        {
            using var response = await _http.SendAsync(message, ct).ConfigureAwait(false);
            var reply = await JsonSerializer.DeserializeAsync<ApplicationStorageReply>(
                await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), JsonWire.Options, ct).ConfigureAwait(false)
                ?? throw new JsonException("Missing storage reply.");
            if (reply.Error is not null) throw new ArmException(reply.Error);
            response.EnsureSuccessStatusCode();
            return reply;
        }
        catch (HttpRequestException error) { throw new KernelUnavailableException(call.Operation, call.RequestId, error); }
        catch (JsonException error) { throw new KernelUnavailableException(call.Operation, call.RequestId, error); }
        catch (OperationCanceledException error) when (!ct.IsCancellationRequested)
        { throw new KernelUnavailableException(call.Operation, call.RequestId, error); }
        catch (OperationCanceledException error)
        { throw new KernelRequestCanceledException(call.Operation, call.RequestId, error, ct); }
    }

    public async ValueTask<JsonObject?> ReceiptAsync(string requestId, CancellationToken ct = default)
    {
        using var message = Message(HttpMethod.Get, "receipts/" + Uri.EscapeDataString(requestId));
        using var response = await _http.SendAsync(message, ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await JsonSerializer.DeserializeAsync<JsonObject>(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), JsonWire.Options, ct).ConfigureAwait(false);
    }

    private async ValueTask<T?> Call<T>(string operation, JsonObject args, CancellationToken ct)
    {
        var reply = await ExecuteAsync(new ApplicationStorageCall(operation, args, Guid.NewGuid().ToString("N"), _actor), ct).ConfigureAwait(false);
        return reply.Value is null ? default : reply.Value.Deserialize<T>(JsonWire.Options);
    }
    private async ValueTask Void(string operation, JsonObject args, CancellationToken ct) =>
        _ = await Call<JsonNode>(operation, args, ct).ConfigureAwait(false);
    private T? Sync<T>(string operation, JsonObject args) => Call<T>(operation, args, CancellationToken.None).AsTask().GetAwaiter().GetResult();
    private void SyncVoid(string operation, JsonObject args) => Void(operation, args, CancellationToken.None).AsTask().GetAwaiter().GetResult();

    ValueTask<ResourceEnvelope<JsonObject>?> IResourceStore.GetAsync(string key, CancellationToken ct) => Call<ResourceEnvelope<JsonObject>>("resources.get", Args(("key", key)), ct);
    public async ValueTask<ResourceEnvelope<JsonObject>> CreateAsync(ResourceEnvelope<JsonObject> envelope, CancellationToken ct) =>
        await Call<ResourceEnvelope<JsonObject>>("resources.create", Args(("envelope", envelope)), ct).ConfigureAwait(false) ?? throw new JsonException();
    public async ValueTask<ResourceEnvelope<JsonObject>> UpsertAsync(ResourceEnvelope<JsonObject> envelope, string? ifMatchETag, CancellationToken ct) =>
        await Call<ResourceEnvelope<JsonObject>>("resources.upsert", Args(("envelope", envelope), ("etag", ifMatchETag)), ct).ConfigureAwait(false) ?? throw new JsonException();
    public ValueTask DeleteAsync(string key, string? ifMatchETag, CancellationToken ct) => Void("resources.delete", Args(("key", key), ("etag", ifMatchETag)), ct);
    public async ValueTask<PagedResult<ResourceEnvelope<JsonObject>>> ListAsync(ResourceStoreQuery query, CancellationToken ct) =>
        await Call<PagedResult<ResourceEnvelope<JsonObject>>>("resources.list", Args(("query", query)), ct).ConfigureAwait(false) ?? throw new JsonException();

    public ValueTask<long> AppendAsync(EventAppend evt, CancellationToken ct) => Call<long>("events.append", Args(("event", evt)), ct);
    public async IAsyncEnumerable<LedgerEvent> ReadRangeAsync(string streamId, long fromSeq, long toSeqExclusive, int limit,
        RenderLane? lanes, [EnumeratorCancellation] CancellationToken ct)
    {
        var remaining = limit <= 0 ? int.MaxValue : limit;
        while (remaining > 0)
        {
            var take = Math.Min(remaining, 1000);
            var page = await Call<List<LedgerEvent>>("events.range", Args(("stream", streamId), ("from", fromSeq),
                ("to", toSeqExclusive), ("limit", take), ("lanes", lanes)), ct).ConfigureAwait(false) ?? [];
            foreach (var evt in page) { yield return evt; remaining--; fromSeq = checked(evt.Seq + 1); }
            if (page.Count < take) yield break;
        }
    }
    public IAsyncEnumerable<LedgerEvent> TailAsync(string streamId, long afterSeq, CancellationToken ct) => Tail(streamId, afterSeq, RenderLane.None, ct);
    public IAsyncEnumerable<LedgerEvent> TailAllAsync(RenderLane lanes, long afterSeq, CancellationToken ct) => Tail(null, afterSeq, lanes, ct);
    private async IAsyncEnumerable<LedgerEvent> Tail(string? stream, long after, RenderLane lanes, [EnumeratorCancellation] CancellationToken ct)
    {
        if (!_verified) throw new InvalidOperationException("Connect first.");
        using var message = Message(HttpMethod.Get, "events?after=" + after + "&stream=" + Uri.EscapeDataString(stream ?? ""));
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                var evt = JsonSerializer.Deserialize<LedgerEvent>(line[6..], JsonWire.Options) ?? throw new JsonException();
                if (lanes == RenderLane.None || (evt.Lanes & lanes) != 0) yield return evt;
            }
    }

    public ValueTask<LeaseAcquisition?> AcquireAsync(string resource, string owner, TimeSpan ttl, CancellationToken ct) =>
        Call<LeaseAcquisition>("leases.acquire", Args(("resource", resource), ("owner", owner), ("ttl", ttl)), ct);
    public ValueTask<bool> RenewAsync(string resource, string owner, string fencingToken, TimeSpan ttl, CancellationToken ct) =>
        Call<bool>("leases.renew", Args(("resource", resource), ("owner", owner), ("fence", fencingToken), ("ttl", ttl)), ct);
    public ValueTask ReleaseAsync(string resource, string owner, string fencingToken, CancellationToken ct) =>
        Void("leases.release", Args(("resource", resource), ("owner", owner), ("fence", fencingToken)), ct);
    ValueTask<LeaseAcquisition?> ILeaseStore.GetAsync(string resource, CancellationToken ct) => Call<LeaseAcquisition>("leases.get", Args(("resource", resource)), ct);
    public async ValueTask<IReadOnlyList<LeaseAcquisition>> ListExpiredAsync(CancellationToken ct) =>
        await Call<List<LeaseAcquisition>>("leases.expired", Args(), ct).ConfigureAwait(false) ?? [];

    public ValueTask EnqueueAsync(OutboxMessage message, CancellationToken ct) => Void("outbox.enqueue", Args(("message", message)), ct);
    public async ValueTask<IReadOnlyList<OutboxRecord>> ClaimAsync(int max, DateTimeOffset now, CancellationToken ct) =>
        await Call<List<OutboxRecord>>("outbox.claim", Args(("max", max), ("now", now)), ct).ConfigureAwait(false) ?? [];
    public ValueTask MarkSentAsync(Guid messageId, CancellationToken ct) => Void("outbox.sent", Args(("id", messageId)), ct);
    public ValueTask MarkDeadAsync(Guid messageId, string reason, CancellationToken ct) => Void("outbox.markDead", Args(("id", messageId), ("reason", reason)), ct);
    public ValueTask RetryLaterAsync(Guid messageId, DateTimeOffset nextAttemptAt, CancellationToken ct) => Void("outbox.retry", Args(("id", messageId), ("at", nextAttemptAt)), ct);
    public async ValueTask<IReadOnlyList<OutboxRecord>> ListDeadAsync(CancellationToken ct) => await Call<List<OutboxRecord>>("outbox.dead", Args(), ct).ConfigureAwait(false) ?? [];

    public bool TryBegin(string key) => Sync<bool>("idempotency.begin", Args(("key", key)));
    public void End(string key) => SyncVoid("idempotency.end", Args(("key", key)));
    IdempotencyRecord? IIdempotencyStore.Get(string key) => Sync<IdempotencyRecord>("idempotency.get", Args(("key", key)));
    public void Put(string key, IdempotencyRecord record) => SyncVoid("idempotency.put", Args(("key", key), ("record", record)));
    ApprovalRecord? IApprovalStore.Get(string id) => Sync<ApprovalRecord>("approvals.get", Args(("id", id)));
    public void Put(ApprovalRecord record) => SyncVoid("approvals.put", Args(("record", record)));
    public IReadOnlyCollection<ApprovalRecord> All() => Sync<List<ApprovalRecord>>("approvals.all", Args()) ?? [];
    public ApprovalGrant? FindGrant(string decisionKey, string? shapeKey, string? transition, DateTimeOffset now) =>
        Sync<ApprovalGrant>("approvals.findGrant", Args(("key", decisionKey), ("shape", shapeKey), ("transition", transition), ("now", now)));
    public void PutGrant(ApprovalGrant grant) => SyncVoid("approvals.putGrant", Args(("grant", grant)));
    public void RemoveGrantsForStream(string streamId) => SyncVoid("approvals.removeGrants", Args(("stream", streamId)));
    public void PurgeGrants(DateTimeOffset now) => SyncVoid("approvals.purge", Args(("now", now)));

    public ValueTask StartAsync(string operationId, string subscriptionId, string ns, string location, string? targetResourceId, JsonObject? props, CancellationToken ct) =>
        Void("operations.start", Args(("id", operationId), ("subscription", subscriptionId), ("namespace", ns), ("location", location), ("target", targetResourceId), ("properties", props)), ct);
    public ValueTask ReportAsync(string operationId, int percent, JsonObject? props, CancellationToken ct) => Void("operations.report", Args(("id", operationId), ("percent", percent), ("properties", props)), ct);
    public ValueTask SucceedAsync(string operationId, JsonObject? result, CancellationToken ct) => Void("operations.succeed", Args(("id", operationId), ("properties", result)), ct);
    public ValueTask FailAsync(string operationId, ArmError error, CancellationToken ct) => Void("operations.fail", Args(("id", operationId), ("error", error)), ct);
    public ValueTask CancelAsync(string operationId, string reason, CancellationToken ct) => Void("operations.cancel", Args(("id", operationId), ("reason", reason)), ct);
    ValueTask<OperationResource?> IOperationTracker.GetAsync(string operationId, CancellationToken ct) => Call<OperationResource>("operations.get", Args(("id", operationId)), ct);
    public async ValueTask<IReadOnlyList<OperationResource>> ListAsync(string? subscriptionId, string? ns, CancellationToken ct) =>
        await Call<List<OperationResource>>("operations.list", Args(("subscription", subscriptionId), ("namespace", ns)), ct).ConfigureAwait(false) ?? [];

    public IDecisionCache<T> DecisionCache<T>() => new RemoteDecisionCache<T>(this);
    private sealed class RemoteDecisionCache<T>(ConnectedKernelClient client) : IDecisionCache<T>
    {
        public ValueTask<T?> GetAsync(string streamId, string decisionKey, CancellationToken ct) => client.Call<T>("decisions.get", Args(("stream", streamId), ("key", decisionKey)), ct);
        public ValueTask SetAsync(string streamId, string decisionKey, T value, CancellationToken ct) => client.Void("decisions.set", Args(("stream", streamId), ("key", decisionKey), ("value", value)), ct);
    }

    public async ValueTask PutAsync(string key, Stream content, string contentType, IReadOnlyDictionary<string, string>? metadata, CancellationToken ct)
    {
        if (!_verified) throw new InvalidOperationException("Connect first.");
        using var message = Message(HttpMethod.Put, "artifacts?key=" + Uri.EscapeDataString(key));
        // StreamContent disposal must not take ownership of the caller's stream.
        message.Content = new StreamContent(new BorrowedStream(content));
        message.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        if (metadata is not null) message.Headers.Add("X-Artifact-Metadata", Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(metadata, JsonWire.Options))));
        using var response = await _http.SendAsync(message, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }
    ValueTask<ArtifactBlob?> IArtifactStore.GetAsync(string key, CancellationToken ct) => GetArtifactAsync(key, ct);
    private async ValueTask<ArtifactBlob?> GetArtifactAsync(string key, CancellationToken ct)
    {
        if (!_verified) throw new InvalidOperationException("Connect first.");
        using var message = Message(HttpMethod.Get, "artifacts?key=" + Uri.EscapeDataString(key));
        var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) { response.Dispose(); return null; }
        response.EnsureSuccessStatusCode();
        var metadata = response.Headers.TryGetValues("X-Artifact-Metadata", out var values)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(Convert.FromBase64String(values.Single()), JsonWire.Options) ?? [] : [];
        var sha = response.Headers.TryGetValues("X-Artifact-Sha256", out var hashes) ? hashes.Single() : null;
        return new ArtifactBlob(key, new OwnedResponseStream(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), response),
            response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream", sha, metadata);
    }
    public async ValueTask<bool> ExistsAsync(string key, CancellationToken ct)
    {
        var blob = await GetArtifactAsync(key, ct).ConfigureAwait(false);
        if (blob is null) return false;
        await blob.Content.DisposeAsync().ConfigureAwait(false);
        return true;
    }

    private class BorrowedStream(Stream inner) : Stream
    {
        protected Stream Inner { get; } = inner;
        public override bool CanRead => Inner.CanRead;
        public override bool CanSeek => Inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => Inner.Length;
        public override long Position { get => Inner.Position; set => Inner.Position = value; }
        public override void Flush() => Inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => Inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => Inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => Inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class OwnedResponseStream(Stream inner, HttpResponseMessage response) : BorrowedStream(inner)
    {
        protected override void Dispose(bool disposing) { if (disposing) { Inner.Dispose(); response.Dispose(); } base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { await Inner.DisposeAsync().ConfigureAwait(false); response.Dispose(); GC.SuppressFinalize(this); }
    }
}
