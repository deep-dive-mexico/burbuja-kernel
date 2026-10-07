using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Applications;
using Burbuja.Core.Abstractions.Approvals;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Json;
using Burbuja.Core.Abstractions.Outbox;
using Burbuja.Core.Applications;
using Burbuja.Core.Arm;
using Burbuja.Core.Sdk.Applications;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Burbuja.Hosting.Applications;

/// <summary>Authenticated persistence interface for independently hosted applications.</summary>
public static class ConnectedKernelEndpoints
{
    public const string Protocol = "burbuja-applications/1";
    private const string Root = "/kernel/v1";
    private static readonly HashSet<string> Reads = ["resources.get", "resources.list", "events.range",
        "leases.get", "leases.expired", "idempotency.get", "approvals.get", "approvals.all", "approvals.findGrant",
        "operations.get", "operations.list", "decisions.get", "outbox.dead"];

    public static void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        var options = new ConnectedKernelOptions();
        configuration.GetSection("ConnectedKernel").Bind(options);
        services.AddSingleton(options);
        if (!options.Enabled) return;
        services.AddSingleton<IApplicationStorageProvider, InMemoryApplicationStorageProvider>();
        services.AddSingleton(sp => new ApplicationStorageFactory(options, sp.GetServices<IApplicationStorageProvider>()));
        services.AddSingleton(sp => new ConnectedControlState(options, sp.GetServices<IApplicationStorageProvider>()));
        services.AddSingleton<ScopedApplicationRuntime>();
    }

    public static void Map(WebApplication app)
    {
        if (!app.Services.GetRequiredService<ConnectedKernelOptions>().Enabled) return;
        // Validate all registrations before opening a listener, including unsupported providers.
        _ = app.Services.GetRequiredService<ApplicationStorageFactory>();
        _ = app.Services.GetRequiredService<ConnectedControlState>();
        app.MapGet(Root + "/handshake", (HttpContext context, ApplicationStorageFactory factory, ConnectedKernelOptions options) =>
        {
            var authority = Authenticate(context, factory);
            if (authority is null) return Error(ArmError.AuthenticationFailed("Application credential required."));
            var version = typeof(ApplicationStorageFactory).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
            var capabilities = new List<string> { "scoped-storage", "conditional-resources", "atomic-create", "event-chains", "event-sse", "artifacts", "write-receipts" };
            if (options.Applications.Any(a => a.Tenant == authority.Value.Scope.Tenant && a.Application == authority.Value.Scope.Application
                && a.RuntimeEnabled && a.Writable)) capabilities.AddRange(["scoped-arm", "scoped-arm-sse"]);
            return Results.Json(new ApplicationHandshake(Protocol, version, authority.Value.Scope, capabilities), JsonWire.Options);
        }).AllowAnonymous();
        app.MapPost(Root + "/storage", CallAsync).AllowAnonymous();
        app.MapGet(Root + "/receipts/{id}", async (HttpContext context, string id, ApplicationStorageFactory factory, ConnectedControlState state) =>
        {
            var authority = Authenticate(context, factory);
            if (authority is null) return Error(ArmError.AuthenticationFailed("Application credential required."));
            if (!Guid.TryParseExact(id, "N", out _)) return Error(ArmError.Validation("Invalid request identity.", []));
            var receipt = await state.Stores.Resources.GetAsync(ReceiptId(authority.Value.Scope, id), context.RequestAborted);
            return receipt is null ? Results.NotFound() : Results.Json(receipt.Properties, JsonWire.Options);
        }).AllowAnonymous();
        app.MapGet(Root + "/events", TailAsync).AllowAnonymous();
        app.MapGet(Root + "/artifacts", GetArtifactAsync).AllowAnonymous();
        app.MapPut(Root + "/artifacts", PutArtifactAsync).AllowAnonymous();
        app.MapPost(Root + "/runtime/arm", async (HttpContext context, ApplicationStorageFactory factory) =>
        {
            var authority = Authenticate(context, factory);
            if (authority is null) return Error(ArmError.AuthenticationFailed("Application credential required."));
            var call = await RuntimeCallAsync(context);
            if (call is null) return Error(ArmError.Validation("A bounded, valid runtime request is required.", []));
            var runtime = context.RequestServices.GetRequiredService<ScopedApplicationRuntime>();
            var response = await runtime.SendAsync(authority.Value.Scope, authority.Value.Stores, call, context.RequestAborted);
            return Results.Json(response, JsonWire.Options);
        }).AllowAnonymous();
        app.MapPost(Root + "/runtime/stream", async (HttpContext context, ApplicationStorageFactory factory, ScopedApplicationRuntime runtime) =>
        {
            var authority = Authenticate(context, factory);
            if (authority is null) { context.Response.StatusCode = 401; return; }
            var call = await RuntimeCallAsync(context);
            if (call is null) { context.Response.StatusCode = 400; return; }
            await runtime.StreamAsync(authority.Value.Scope, authority.Value.Stores, call, context);
        }).AllowAnonymous();

    }

    private static async Task<ApplicationRuntimeCall?> RuntimeCallAsync(HttpContext context)
    {
        if (context.Request.ContentLength is null or <= 0 or > 1_048_576) return null;
        try
        {
            var call = await context.Request.ReadFromJsonAsync<ApplicationRuntimeCall>(JsonWire.Options, context.RequestAborted);
            if (call?.Principal?.Claims is null || call.Request is null || call.Principal.PrincipalId?.Length is null or > 256
                || call.Principal.Claims.Count > 32 || call.Principal.Claims.Any(kv => kv.Key.Length > 256 || kv.Value is null || kv.Value.Length > 4096)
                || call.Request.Path is null || !call.Request.Path.StartsWith('/') || call.Request.Path.Length > 8192
                || call.Request.Method is not ("GET" or "PUT" or "POST" or "PATCH" or "DELETE" or "HEAD")) return null;
            return call;
        }
        catch (JsonException) { return null; }
    }

    private static (ApplicationScope Scope, bool Writable, ApplicationStores Stores, IReadOnlySet<string> WriteNamespaces, IReadOnlySet<string> EventPrefixes)? Authenticate(HttpContext context, ApplicationStorageFactory factory)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        var authority = authorization.StartsWith("Bearer ", StringComparison.Ordinal)
            ? factory.Authenticate(authorization[7..]) : null;
        if (authority is null) return null;
        var expected = context.Request.Headers["X-Burbuja-Application"].ToString();
        if (expected.Length == 0) return authority;
        try
        {
            var scope = JsonSerializer.Deserialize<ApplicationScope>(Convert.FromBase64String(expected), JsonWire.Options);
            return scope == authority.Value.Scope ? authority : null;
        }
        catch (Exception error) when (error is FormatException or JsonException) { return null; }
    }

    private static IResult Error(ArmError error) => Results.Json(new ApplicationStorageReply(null, error), JsonWire.Options, statusCode: error.HttpStatus);

    private static async Task<IResult> CallAsync(HttpContext context, ApplicationStorageFactory factory,
        ConnectedControlState state)
    {
        var control = state.Stores.Resources;
        var audit = state.Stores.Events;
        var authority = Authenticate(context, factory);
        if (authority is null) return Error(ArmError.AuthenticationFailed("Application credential required."));
        if (context.Request.ContentLength is null or > 1_048_576)
            return Error(ArmError.Validation("A bounded JSON request (at most 1 MiB) is required.", []));
        ApplicationStorageCall call;
        try
        {
            call = await context.Request.ReadFromJsonAsync<ApplicationStorageCall>(JsonWire.Options, context.RequestAborted)
                ?? throw new JsonException();
            if (!Guid.TryParseExact(call.RequestId, "N", out _) || string.IsNullOrWhiteSpace(call.Operation)
                || call.Operation.Length > 128 || call.Arguments is null || call.Actor?.Length > 256)
                return Error(ArmError.Validation("Invalid request or delegated actor identity.", []));
        }
        catch (JsonException) { return Error(ArmError.Validation("Invalid typed storage request.", [])); }
        var (scope, writable, stores, writeNamespaces, eventPrefixes) = authority.Value;
        await audit.AppendAsync(new EventAppend("connected.kernel/audit", scope.Application, "connected.storage.request",
            call.Operation, JsonSerializer.SerializeToNode(new { scope, call.RequestId, call.Actor }, JsonWire.Options),
            RenderLane.Log, null, null, "Burbuja.Connected"), context.RequestAborted);
        var write = !Reads.Contains(call.Operation);
        if (write && !writable) return Error(ArmError.Forbidden("Application registration is read-only."));
        // Persisting product state is not authority to forge kernel decisions or runtime resources.
        if (call.Operation.StartsWith("approvals.", StringComparison.Ordinal) && write)
            return Error(ArmError.Forbidden("Kernel approval decisions require the approval capability, not persistence authority."));
        try
        {
            if (call.Operation is "resources.create" or "resources.upsert")
            {
                var envelope = Required<ResourceEnvelope<JsonObject>>(call.Arguments, "envelope");
                if (!OwnNamespace(envelope.Type.Split('/')[0], writeNamespaces) || !OwnResourceId(envelope.Id, writeNamespaces))
                    return Error(ArmError.Forbidden("Resource is outside the registered product namespaces."));
            }
            if (call.Operation == "resources.delete" && !OwnResourceId(Required<string>(call.Arguments, "key"), writeNamespaces))
                return Error(ArmError.Forbidden("Resource is outside the registered product namespaces."));
            if (call.Operation == "operations.start" && !OwnNamespace(Required<string>(call.Arguments, "namespace"), writeNamespaces))
                return Error(ArmError.Forbidden("Operation is outside the registered product namespaces."));
            if (call.Operation.StartsWith("operations.", StringComparison.Ordinal) && write)
            {
                var operation = await stores.Operations.GetAsync(Required<string>(call.Arguments, "id"), context.RequestAborted);
                if ((operation is null && call.Operation != "operations.start") || (operation is not null && !OwnResourceId(operation.Id, writeNamespaces)))
                    return Error(ArmError.Forbidden("Operation is outside the registered product namespaces."));
            }
            if (call.Operation == "events.append" && !eventPrefixes.Any(prefix =>
                Required<EventAppend>(call.Arguments, "event").Type.StartsWith(prefix, StringComparison.Ordinal)))
                return Error(ArmError.Forbidden("Event type is outside the registered product prefixes."));
            if (call.Operation == "outbox.enqueue" && !eventPrefixes.Any(prefix =>
                Required<OutboxMessage>(call.Arguments, "message").Topic.StartsWith(prefix, StringComparison.Ordinal)))
                return Error(ArmError.Forbidden("Outbox topic is outside the registered product prefixes."));
        }
        catch (JsonException) { return Error(ArmError.Validation("Invalid typed storage arguments.", [])); }
        var receiptKey = ReceiptId(scope, call.RequestId);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(call with { RequestId = "" }, JsonWire.Options))));
        if (write)
        {
            try
            {
                await control.CreateAsync(new ResourceEnvelope<JsonObject>(receiptKey, call.RequestId,
                    "Burbuja.Connected/receipts", null, new Dictionary<string, string>(),
                    new JsonObject { ["state"] = "started", ["requestHash"] = hash }, ProvisioningState.Creating,
                    new SystemData(null, scope.Application, "Application", null, null, null), ""), context.RequestAborted);
            }
            catch (ArmException error) when (error.Error.HttpStatus == 412)
            {
                var existing = await control.GetAsync(receiptKey, context.RequestAborted);
                if (existing?.Properties["requestHash"]?.GetValue<string>() != hash)
                    return Error(ArmError.ConflictingClientRequestId("Request identity was already used for another operation."));
                return existing.Properties["reply"] is { } cached
                    ? Reply(cached.Deserialize<ApplicationStorageReply>(JsonWire.Options)!)
                    : Error(ArmError.Conflict("OutcomeUnknown", "An interrupted request requires receipt reconciliation; it will not execute again."));
            }
        }
        ApplicationStorageReply reply;
        try
        {
            reply = new(await DispatchAsync(stores, call, context.RequestAborted));
        }
        catch (ArmException error) { reply = new(null, error.Error); }
        catch (JsonException) { reply = new(null, ArmError.Validation("Invalid typed storage arguments.", [])); }
        catch (ArgumentException) { reply = new(null, ArmError.Validation("Invalid typed storage arguments.", [])); }
        // Other faults leave a durable started receipt. Retrying cannot claim the effect did not happen.
        if (write)
        {
            using var settlement = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var receipt = await control.GetAsync(receiptKey, settlement.Token)
                ?? throw new InvalidOperationException("Missing kernel request receipt.");
            var properties = receipt.Properties.DeepClone().AsObject();
            properties["state"] = "completed";
            properties["reply"] = JsonSerializer.SerializeToNode(reply, JsonWire.Options);
            await control.UpsertAsync(receipt with { Properties = properties, ProvisioningState = ProvisioningState.Succeeded }, receipt.ETag, settlement.Token);
        }
        return Reply(reply);
    }

    private static IResult Reply(ApplicationStorageReply reply) =>
        Results.Json(reply, JsonWire.Options, statusCode: reply.Error?.HttpStatus ?? 200);

    private static string ReceiptId(ApplicationScope scope, string id) => "/providers/Burbuja.Connected/receipts/" +
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(scope)))) + "-" + Guid.ParseExact(id, "N").ToString("N");

    private static T Required<T>(JsonObject args, string name) => args[name] is { } node
        ? node.Deserialize<T>(JsonWire.Options) ?? throw new JsonException("Missing " + name) : throw new JsonException("Missing " + name);
    private static string? Optional(JsonObject args, string name) => args[name]?.Deserialize<string>(JsonWire.Options);
    private static JsonNode? Node<T>(T value) => JsonSerializer.SerializeToNode(value, JsonWire.Options);
    private static bool OwnNamespace(string ns, IReadOnlySet<string> allowed) => allowed.Contains(ns);
    private static bool OwnResourceId(string id, IReadOnlySet<string> allowed)
    {
        var match = System.Text.RegularExpressions.Regex.Match(id, @"(?:^|/)providers/([^/]+)/", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? allowed.Contains(match.Groups[1].Value) : allowed.Contains(id.Split('/')[0]);
    }
    private static string AppKey(string value) => "application/" + value;
    private static JsonNode? LeaseNode(Burbuja.Core.Abstractions.Leases.LeaseAcquisition? lease) =>
        Node(lease is null ? null : lease with { Resource = lease.Resource["application/".Length..] });

    private static async ValueTask<JsonNode?> DispatchAsync(ApplicationStores s, ApplicationStorageCall call, CancellationToken ct)
    {
        var a = call.Arguments;
        switch (call.Operation)
        {
            case "resources.get": return Node(await s.Resources.GetAsync(Required<string>(a, "key"), ct));
            case "resources.create": return Node(await s.Resources.CreateAsync(Required<ResourceEnvelope<JsonObject>>(a, "envelope"), ct));
            case "resources.upsert": return Node(await s.Resources.UpsertAsync(Required<ResourceEnvelope<JsonObject>>(a, "envelope"), Optional(a, "etag"), ct));
            case "resources.delete": await s.Resources.DeleteAsync(Required<string>(a, "key"), Optional(a, "etag"), ct); return null;
            case "resources.list": return Node(await s.Resources.ListAsync(Required<ResourceStoreQuery>(a, "query"), ct));
            case "events.append": return Node(await s.Events.AppendAsync(Required<EventAppend>(a, "event"), ct));
            case "events.range":
                var limit = Required<int>(a, "limit");
                if (limit is < 1 or > 1000) throw new JsonException();
                var events = new List<LedgerEvent>();
                await foreach (var evt in s.Events.ReadRangeAsync(Required<string>(a, "stream"), Required<long>(a, "from"),
                    Required<long>(a, "to"), limit, a["lanes"]?.Deserialize<RenderLane>(JsonWire.Options), ct)) events.Add(evt);
                return Node(events);
            case "leases.acquire": return LeaseNode(await s.Leases.AcquireAsync(AppKey(Required<string>(a, "resource")), Required<string>(a, "owner"), Required<TimeSpan>(a, "ttl"), ct));
            case "leases.renew": return Node(await s.Leases.RenewAsync(AppKey(Required<string>(a, "resource")), Required<string>(a, "owner"), Required<string>(a, "fence"), Required<TimeSpan>(a, "ttl"), ct));
            case "leases.release": await s.Leases.ReleaseAsync(AppKey(Required<string>(a, "resource")), Required<string>(a, "owner"), Required<string>(a, "fence"), ct); return null;
            case "leases.get": return LeaseNode(await s.Leases.GetAsync(AppKey(Required<string>(a, "resource")), ct));
            case "leases.expired": return new JsonArray((await s.Leases.ListExpiredAsync(ct)).Where(l => l.Resource.StartsWith("application/", StringComparison.Ordinal)).Select(LeaseNode).ToArray());
            case "outbox.enqueue": await s.Outbox.EnqueueAsync(Required<OutboxMessage>(a, "message"), ct); return null;
            case "outbox.claim": return Node(await s.Outbox.ClaimAsync(Required<int>(a, "max"), Required<DateTimeOffset>(a, "now"), ct));
            case "outbox.sent": await s.Outbox.MarkSentAsync(Required<Guid>(a, "id"), ct); return null;
            case "outbox.dead": return Node(await s.Outbox.ListDeadAsync(ct));
            case "outbox.markDead": await s.Outbox.MarkDeadAsync(Required<Guid>(a, "id"), Required<string>(a, "reason"), ct); return null;
            case "outbox.retry": await s.Outbox.RetryLaterAsync(Required<Guid>(a, "id"), Required<DateTimeOffset>(a, "at"), ct); return null;
            case "idempotency.begin": return Node(s.Idempotency.TryBegin(AppKey(Required<string>(a, "key"))));
            case "idempotency.end": s.Idempotency.End(AppKey(Required<string>(a, "key"))); return null;
            case "idempotency.get": return Node(s.Idempotency.Get(AppKey(Required<string>(a, "key"))));
            case "idempotency.put": s.Idempotency.Put(AppKey(Required<string>(a, "key")), Required<IdempotencyRecord>(a, "record")); return null;
            case "approvals.get": return Node(s.Approvals.Get(Required<string>(a, "id")));
            case "approvals.put": s.Approvals.Put(Required<ApprovalRecord>(a, "record")); return null;
            case "approvals.all": return Node(s.Approvals.All());
            case "approvals.findGrant": return Node(s.Approvals.FindGrant(Required<string>(a, "key"), Optional(a, "shape"), Optional(a, "transition"), Required<DateTimeOffset>(a, "now")));
            case "approvals.putGrant": s.Approvals.PutGrant(Required<ApprovalGrant>(a, "grant")); return null;
            case "approvals.removeGrants": s.Approvals.RemoveGrantsForStream(Required<string>(a, "stream")); return null;
            case "approvals.purge": s.Approvals.PurgeGrants(Required<DateTimeOffset>(a, "now")); return null;
            case "operations.start": await s.Operations.StartAsync(Required<string>(a, "id"), Required<string>(a, "subscription"), Required<string>(a, "namespace"), Required<string>(a, "location"), Optional(a, "target"), a["properties"]?.AsObject(), ct); return null;
            case "operations.report": await s.Operations.ReportAsync(Required<string>(a, "id"), Required<int>(a, "percent"), a["properties"]?.AsObject(), ct); return null;
            case "operations.succeed": await s.Operations.SucceedAsync(Required<string>(a, "id"), a["properties"]?.AsObject(), ct); return null;
            case "operations.fail": await s.Operations.FailAsync(Required<string>(a, "id"), Required<ArmError>(a, "error"), ct); return null;
            case "operations.cancel": await s.Operations.CancelAsync(Required<string>(a, "id"), Required<string>(a, "reason"), ct); return null;
            case "operations.get": return Node(await s.Operations.GetAsync(Required<string>(a, "id"), ct));
            case "operations.list": return Node(await s.Operations.ListAsync(Optional(a, "subscription"), Optional(a, "namespace"), ct));
            case "decisions.get": return await s.Decisions.GetAsync(AppKey(Required<string>(a, "stream")), Required<string>(a, "key"), ct);
            case "decisions.set": await s.Decisions.SetAsync(AppKey(Required<string>(a, "stream")), Required<string>(a, "key"), a["value"] ?? throw new JsonException(), ct); return null;
            default: throw new ArmException(ArmError.ActionNotSupported("Unknown storage operation."));
        }
    }

    private static async Task TailAsync(HttpContext context, ApplicationStorageFactory factory)
    {
        var authority = Authenticate(context, factory);
        if (authority is null) { context.Response.StatusCode = 401; return; }
        if (!long.TryParse(context.Request.Query["after"], out var after) || after < 0)
        { context.Response.StatusCode = 400; return; }
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-store";
        var stream = context.Request.Query["stream"].ToString();
        var events = stream.Length > 0 ? authority.Value.Stores.Events.TailAsync(stream, after, context.RequestAborted)
            : authority.Value.Stores.Events.TailAllAsync(RenderLane.None, after, context.RequestAborted);
        await context.Response.StartAsync(context.RequestAborted);
        await foreach (var evt in events)
        {
            await context.Response.WriteAsync("id: " + evt.Seq + "\ndata: " + JsonSerializer.Serialize(evt, JsonWire.Options) + "\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
        }
    }

    private static async Task<IResult> GetArtifactAsync(HttpContext context, ApplicationStorageFactory factory)
    {
        var authority = Authenticate(context, factory);
        if (authority is null) return Error(ArmError.AuthenticationFailed("Application credential required."));
        var blob = await authority.Value.Stores.Artifacts.GetAsync(context.Request.Query["key"].ToString(), context.RequestAborted);
        if (blob is null) return Results.NotFound();
        context.Response.Headers["X-Artifact-Metadata"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(blob.Metadata, JsonWire.Options)));
        if (blob.Sha256 is not null) context.Response.Headers["X-Artifact-Sha256"] = blob.Sha256;
        return Results.Stream(blob.Content, blob.ContentType);
    }

    private static async Task<IResult> PutArtifactAsync(HttpContext context, ApplicationStorageFactory factory)
    {
        var authority = Authenticate(context, factory);
        if (authority is null) return Error(ArmError.AuthenticationFailed("Application credential required."));
        if (!authority.Value.Writable) return Error(ArmError.Forbidden("Application registration is read-only."));
        if (context.Request.ContentLength > 134_217_728) return Results.StatusCode(413);
        IReadOnlyDictionary<string, string>? metadata = null;
        try
        {
            if (context.Request.Headers["X-Artifact-Metadata"].ToString() is { Length: > 0 } encoded)
                metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(Convert.FromBase64String(encoded), JsonWire.Options);
        }
        catch (Exception error) when (error is FormatException or JsonException)
        { return Error(ArmError.Validation("Invalid artifact metadata.", [])); }
        // Stage a bounded upload before exposing it to the provider; a canceled or
        // oversized incoming stream cannot truncate an existing artifact.
        var temporary = Path.Combine(Path.GetTempPath(), "burbuja-upload-" + Guid.NewGuid().ToString("N"));
        await using var upload = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        var buffer = new byte[81920];
        long size = 0;
        int count;
        while ((count = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) != 0)
        {
            size += count;
            if (size > 134_217_728) return Results.StatusCode(413);
            await upload.WriteAsync(buffer.AsMemory(0, count), context.RequestAborted);
        }
        upload.Position = 0;
        await authority.Value.Stores.Artifacts.PutAsync(context.Request.Query["key"].ToString(), upload,
            context.Request.ContentType ?? "application/octet-stream", metadata, context.RequestAborted);
        return Results.NoContent();
    }
}

/// <summary>Network receipts and audits have a durable kernel-owned binding inaccessible to applications.</summary>
public sealed class ConnectedControlState : IDisposable
{
    public ConnectedControlState(ConnectedKernelOptions options, IEnumerable<IApplicationStorageProvider> providers)
    {
        var provider = providers.SingleOrDefault(p => p.Id == options.ControlProvider)
            ?? throw new InvalidOperationException("Unsupported connected control storage provider.");
        Stores = provider.Open(new ApplicationStorageBinding(new(options.Environment, "kernel", "control"),
            provider.Id, Path.Combine(Path.GetFullPath(options.StorageRoot), "_control")));
    }
    public ApplicationStores Stores { get; }
    public void Dispose() => Stores.Dispose();
}
