using System.Globalization;
using System.Net;
using Burbuja.Cluster.Abstractions;
using Burbuja.Cluster.Agent;
using Burbuja.Cluster.Controller;
using Burbuja.Cluster.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Burbuja.Cluster;

public static class ClusterEndpoints
{
    private const int MaxBody = 512 * 1024;

    /// <summary>
    /// Maps the cluster's HTTP surface for the roles this process has. Three audiences, three kinds of proof:
    /// nodes sign every request with their own key, operators present a bearer token, and programs on the same
    /// machine reach the node's local API over the loopback with the node's local token.
    /// </summary>
    public static IEndpointRouteBuilder MapBurbujaCluster(this IEndpointRouteBuilder app)
    {
        var options = app.ServiceProvider.GetRequiredService<ClusterOptions>();
        if (!options.Enabled) return app;
        if (options.IsController) MapController(app);
        if (options.IsAgent) MapAgent(app);
        return app;
    }

    // ─── nodes → controller ──────────────────────────────────────────────────────────────────────

    private static void MapController(IEndpointRouteBuilder app)
    {
        var controller = app.ServiceProvider.GetRequiredService<ClusterController>();
        var tokens = app.ServiceProvider.GetRequiredService<ClusterTokens>();
        var time = app.ServiceProvider.GetRequiredService<TimeProvider>();

        // A host may require authentication by default. These endpoints opt out of that because each carries
        // its own proof: a node signature, the operator token, or the node's local token.
        app.MapGet(ClusterConstants.IdentityPath, () => Json(controller.Identity)).AllowAnonymous();

        app.MapPost("/cluster/v1/join", async (HttpContext context) =>
        {
            var signed = await ReadSignedAsync(context, time);
            if (signed is null || !signed.Signer.StartsWith("join:", StringComparison.Ordinal)) return Results.StatusCode(401);
            var key = controller.JoinKeyFor(signed.Signer["join:".Length..]);
            if (key is null || !signed.Verify(key, "/cluster/v1/join")) return Results.StatusCode(401);
            if (!signed.Fresh) return Signed(context, key, signed.Nonce, 401, "{\"error\":\"clock_skew\"}"u8.ToArray());
            var request = ClusterJson.Read<JoinRequest>(signed.Body);
            var response = request is null || request.TokenId != signed.Signer["join:".Length..] ? null : controller.CompleteJoin(request);
            return response is null ? Signed(context, key, signed.Nonce, 403, []) : Signed(context, key, signed.Nonce, 200, ClusterJson.WriteBytes(response));
        }).AllowAnonymous();

        app.MapPost("/cluster/v1/nodes/{nodeId}/sync", async (string nodeId, HttpContext context) =>
        {
            var signed = await ReadSignedAsync(context, time);
            if (signed is null || signed.Signer != nodeId) return Results.StatusCode(401);
            var key = controller.NodeKeyFor(nodeId);
            if (key is null) return Results.StatusCode(404);

            // Nothing is answered with a signature until the caller has shown it holds the key, so the
            // controller cannot be used to sign over a value of somebody else's choosing.
            if (!signed.Verify(key, $"/cluster/v1/nodes/{nodeId}/sync")) return Results.StatusCode(401);
            if (!signed.Fresh) return Signed(context, key, signed.Nonce, 401, "{\"error\":\"clock_skew\"}"u8.ToArray());
            if (!controller.AcceptNonce(nodeId, signed.Nonce)) return Results.StatusCode(401);

            var request = ClusterJson.Read<NodeSyncRequest>(signed.Body);
            if (request is null) return Signed(context, key, signed.Nonce, 400, []);
            var outcome = controller.Sync(nodeId, request);
            return outcome.Result switch
            {
                SyncResult.Ok => Signed(context, key, signed.Nonce, 200, ClusterJson.WriteBytes(outcome.Response)),
                SyncResult.Superseded => Signed(context, key, signed.Nonce, 409, []),
                SyncResult.Removed => Signed(context, key, signed.Nonce, 410, []),
                SyncResult.WrongController => Signed(context, key, signed.Nonce, 412, ClusterJson.WriteBytes(controller.Identity)),
                SyncResult.Stale => Signed(context, key, signed.Nonce, 425, []),
                SyncResult.ProtocolMismatch => Signed(context, key, signed.Nonce, 426, []),
                SyncResult.UnknownNode => Results.StatusCode(404),
                _ => Signed(context, key, signed.Nonce, 400, []),
            };
        }).AllowAnonymous();

        // ─── operators → controller ──────────────────────────────────────────────────────────────
        var operators = app.MapGroup("/cluster/v1").AllowAnonymous().AddEndpointFilter(async (invocation, next) =>
            ClusterSigner.BearerMatches(tokens.OperatorToken, invocation.HttpContext.Request.Headers.Authorization.ToString())
                ? await next(invocation)
                : Results.StatusCode(401));

        operators.MapGet("/status", () => Json(controller.Status()));
        operators.MapPost("/join-tokens", () => Json(controller.CreateJoinToken()));
        operators.MapPut("/workloads/{name}", async (string name, HttpContext context) =>
        {
            var body = await ReadBodyAsync(context);
            var spec = body is null ? null : ClusterJson.Read<WorkloadSpec>(body);
            if (spec is null) return Results.Json(new { errors = new[] { "the body is not a workload specification" } }, ClusterJson.Options, statusCode: 400);
            if (spec.Name != name) return Results.Json(new { errors = new[] { "the name in the body does not match the address" } }, ClusterJson.Options, statusCode: 400);
            var outcome = controller.Apply(spec);
            return outcome.Accepted ? Json(outcome.Workload) : Results.Json(new { errors = outcome.Errors }, ClusterJson.Options, statusCode: 400);
        });
        operators.MapDelete("/workloads/{name}", (string name) => controller.Delete(name) ? Results.NoContent() : Results.NotFound());
        operators.MapPost("/nodes/{nodeId}/{action}", (string nodeId, string action) =>
        {
            NodeAvailability? availability = action switch
            {
                "activate" => NodeAvailability.Active,
                "cordon" => NodeAvailability.Cordoned,
                "drain" => NodeAvailability.Draining,
                _ => null,
            };
            if (availability is null) return Results.NotFound();
            return controller.SetAvailability(nodeId, availability.Value) ? Results.NoContent() : Results.NotFound();
        });
        operators.MapDelete("/nodes/{nodeId}", (string nodeId) => controller.Forget(nodeId) ? Results.NoContent() : Results.NotFound());
    }

    // ─── programs on this machine → node ─────────────────────────────────────────────────────────

    private static void MapAgent(IEndpointRouteBuilder app)
    {
        var agent = app.ServiceProvider.GetRequiredService<NodeAgent>();

        // Both conditions must hold: a connection from this machine, and the token only this node's account
        // can read. The first keeps the API off every network; the second keeps other local accounts out.
        var local = app.MapGroup("/node/v1").AllowAnonymous().AddEndpointFilter(async (invocation, next) =>
        {
            var http = invocation.HttpContext;
            var remote = http.Connection.RemoteIpAddress;
            var loopback = remote is null || IPAddress.IsLoopback(remote);
            return loopback && ClusterSigner.BearerMatches(agent.LocalToken, http.Request.Headers.Authorization.ToString())
                ? await next(invocation)
                : Results.StatusCode(loopback ? 401 : 403);
        });

        local.MapGet("/status", () => Json(agent.Status()));
        local.MapPut("/interactive/{holder}", (string holder, int? ttlMs) =>
        {
            if (holder.Length is 0 or > 128) return Results.BadRequest();
            agent.Gate.Hold(holder, TimeSpan.FromMilliseconds(ttlMs ?? 0));
            return Results.NoContent();
        });
        local.MapDelete("/interactive/{holder}", (string holder) =>
        {
            agent.Gate.Release(holder);
            return Results.NoContent();
        });
    }

    // ─── helpers ─────────────────────────────────────────────────────────────────────────────────

    private static IResult Json<T>(T value) => Results.Json(value, ClusterJson.Options);

    private static IResult Signed(HttpContext context, byte[] key, string requestNonce, int status, byte[] body)
    {
        context.Response.Headers[ClusterSigner.SignatureHeader] = ClusterSigner.SignResponse(key, requestNonce, status, body);
        return new BytesResult(status, body);
    }

    private sealed class BytesResult(int status, byte[] body) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = status;
            httpContext.Response.ContentType = "application/json";
            httpContext.Response.ContentLength = body.Length;
            await httpContext.Response.Body.WriteAsync(body, httpContext.RequestAborted);
        }
    }

    private static async ValueTask<byte[]?> ReadBodyAsync(HttpContext context)
    {
        if (context.Request.ContentLength > MaxBody) return null;
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
        {
            if (buffer.Length + read > MaxBody) return null;
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private sealed record SignedRequest(string Signer, string Timestamp, string Nonce, string Signature, byte[] Body, bool Fresh)
    {
        public bool Verify(byte[] key, string path) =>
            ClusterSigner.Matches(ClusterSigner.SignRequest(key, "POST", path, Timestamp, Nonce, Body), Signature);
    }

    private static async ValueTask<SignedRequest?> ReadSignedAsync(HttpContext context, TimeProvider time)
    {
        var headers = context.Request.Headers;
        var signer = headers[ClusterSigner.NodeHeader].ToString();
        var timestamp = headers[ClusterSigner.TimestampHeader].ToString();
        var nonce = headers[ClusterSigner.NonceHeader].ToString();
        var signature = headers[ClusterSigner.SignatureHeader].ToString();
        if (signer.Length is 0 or > 128 || nonce.Length is < 16 or > 128 || signature.Length is 0 or > 128
            || !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var sentMs))
            return null;
        var body = await ReadBodyAsync(context);
        if (body is null) return null;
        var skew = time.GetUtcNow() - DateTimeOffset.FromUnixTimeMilliseconds(sentMs);
        return new SignedRequest(signer, timestamp, nonce, signature, body, skew.Duration() <= ClusterSigner.MaxSkew);
    }
}
