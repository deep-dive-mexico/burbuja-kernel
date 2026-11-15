using System.Net;
using System.Net.Http.Headers;
using Burbuja.Cluster.Abstractions;
using Burbuja.Cluster.Controller;
using Burbuja.Cluster.Security;
using Microsoft.Extensions.Logging;

namespace Burbuja.Cluster.Networks;

internal enum LinkStatus
{
    Ok,

    /// <summary>No route answered as the cluster. Transient: the agent backs off and tries again.</summary>
    Unreachable,

    /// <summary>A newer process serves this node id. Final for this process.</summary>
    Superseded,

    /// <summary>The controller does not know this node (never joined, or forgotten).</summary>
    NotMember,

    /// <summary>The controller answered and declined. Retrying unchanged will not help.</summary>
    Rejected,

    /// <summary>An operator removed this node, and the controller proved it. Final for this process.</summary>
    Removed,
}

internal sealed record LinkResult(LinkStatus Status, NodeSyncResponse? Response = null, string? Detail = null);

/// <summary>How a node agent reaches its controller. One attempt per call; the agent loop is the only place that retries.</summary>
internal interface IControllerLink
{
    string Route { get; }

    ValueTask<LinkResult> SyncAsync(NodeSyncRequest request, CancellationToken ct);
}

/// <summary>The controller is this same process (<c>Cluster:Role=all</c>): no network, no credential.</summary>
internal sealed class LocalControllerLink(ClusterController controller) : IControllerLink
{
    private bool _registered;
    private long _sequence;

    public string Route => "in-process";

    public ValueTask<LinkResult> SyncAsync(NodeSyncRequest request, CancellationToken ct)
    {
        if (!_registered)
        {
            controller.RegisterLocalNode(request.Descriptor);
            _registered = true;
        }

        var outcome = controller.Sync(request.Descriptor.NodeId, request with
        {
            ControllerInstanceId = controller.Identity.ControllerInstanceId,
            Sequence = ++_sequence,
        });
        return ValueTask.FromResult(outcome.Result switch
        {
            SyncResult.Ok => new LinkResult(LinkStatus.Ok, outcome.Response),
            SyncResult.Superseded => new LinkResult(LinkStatus.Superseded),
            SyncResult.UnknownNode => Forgotten(),
            _ => new LinkResult(LinkStatus.Rejected, Detail: outcome.Result.ToString()),
        });

        LinkResult Forgotten()
        {
            _registered = false;
            return new LinkResult(LinkStatus.NotMember);
        }
    }
}

internal sealed class NodeCredential
{
    public string ClusterId { get; set; } = "";
    public string NodeId { get; set; } = "";
    public string Key { get; set; } = "";

    /// <summary>The id of the join token this credential came from, so the same token is never tried twice.</summary>
    public string TokenId { get; set; } = "";

    /// <summary>Addresses the controller reported for itself the last time it answered.</summary>
    public List<AdvertisedEndpoint> Learned { get; set; } = [];
}

/// <summary>
/// Reaches a controller in another process over whichever network answers.
///
/// <para><b>An identity, and an ordered list of ways to reach it.</b> The node's credential names the cluster;
/// the routes are only addresses. Configured addresses and addresses the controller reports for itself are
/// tried nearest first (loopback, LAN, private network, public, tunnel). Before any credential or signed
/// request is sent to an address, that address must answer the public identity document as the expected
/// cluster. Every response must then carry a signature made with the node's key, so an address that merely
/// forwards or imitates the identity document gets nothing it can use and is dropped.</para>
///
/// <para><b>Failures cool down.</b> A route that fails is set aside for a while, doubling up to five minutes,
/// and a better route that starts answering again is taken up on a later pass. None of this retries inside a
/// call: one call is one attempt.</para>
/// </summary>
internal sealed class HttpControllerLink : IControllerLink, IAsyncDisposable
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan UpgradeEvery = TimeSpan.FromSeconds(60);

    private readonly ClusterOptions _options;
    private readonly INetworkProvider[] _providers;
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly ClusterFileStore<NodeCredential> _store;
    private readonly Dictionary<string, (DateTimeOffset Until, int Failures)> _cooldown = new(StringComparer.Ordinal);
    private NodeCredential? _credential;
    private PreparedRoute? _current;
    private DateTimeOffset _lastUpgradeCheck;
    private string _controllerInstance = "";
    private long _sequence;
    private bool _notMember;
    private string? _refusedTokenId;

    public HttpControllerLink(ClusterOptions options, IEnumerable<INetworkProvider> providers, HttpClient http, TimeProvider time, ILogger<HttpControllerLink> log)
    {
        _options = options;
        _providers = [.. providers];
        _http = http;
        _time = time;
        _log = log;
        _store = new ClusterFileStore<NodeCredential>(options.StateDirectory.Length > 0 ? Path.Combine(options.StateDirectory, "node.json") : null);
        if (options.StateDirectory.Length > 0) PrivateFiles.EnsureDirectory(options.StateDirectory);
        _credential = _store.Load();
        if (_credential is not null && _credential.NodeId != options.ResolvedNodeId) _credential = null;
    }

    public string Route => _current is null ? "none" : $"{_current.Route.Network} {_current.Route.Url}";

    /// <summary>Configured addresses first, then learned ones, ordered nearest first and without duplicates.</summary>
    internal List<NodeRoute> Routes()
    {
        var routes = new List<NodeRoute>();
        foreach (var text in _options.Agent.Controllers)
        {
            if (Uri.TryCreate(text, UriKind.Absolute, out var url) && url.Scheme is "http" or "https") routes.Add(Classify(url, learned: false));
        }

        foreach (var endpoint in _credential?.Learned ?? [])
        {
            // The controller's loopback address is its own machine, never this one.
            if (endpoint.Reachability == EndpointReachability.Loopback || endpoint.Status == EndpointStatus.Unavailable) continue;
            if (routes.Any(r => r.Url == endpoint.Url)) continue;
            routes.Add(new NodeRoute(endpoint.Network, endpoint.Url, endpoint.Reachability, Learned: true));
        }

        return [.. routes.Select((route, index) => (route, index)).OrderBy(r => r.route.Reachability).ThenBy(r => r.index).Select(r => r.route)];
    }

    private NodeRoute Classify(Uri url, bool learned)
    {
        foreach (var provider in _providers)
        {
            if (provider.Classify(url) is { } reachability) return new NodeRoute(provider.Id, url, reachability, learned);
        }

        return new NodeRoute(ClusterConstants.Networks.Static, url, AddressRanges.Fallback(url), learned);
    }

    public async ValueTask<LinkResult> SyncAsync(NodeSyncRequest request, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        if (_current is not null && now - _lastUpgradeCheck > UpgradeEvery)
        {
            _lastUpgradeCheck = now;
            await TryUpgradeAsync(ct);
        }

        _current ??= await SelectAsync(Routes(), ct);
        if (_current is null) return new LinkResult(LinkStatus.Unreachable, Detail: "no controller address answered as this cluster");

        try
        {
            // No credential yet, or the controller no longer knows this node: join, if there is a token that
            // has not been tried. An unsigned "unknown node" never discards the credential this node has.
            if (_credential is null || _notMember)
            {
                var joined = await JoinAsync(_current, request.Descriptor, ct);
                if (joined is not null) return joined;
            }

            return await SignedSyncAsync(_current, request, ct, retry: true);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested)
        {
            await FailCurrentAsync(e.GetType().Name);
            return new LinkResult(LinkStatus.Unreachable, Detail: "the controller stopped answering");
        }
    }

    private async ValueTask<PreparedRoute?> SelectAsync(IReadOnlyList<NodeRoute> routes, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var candidates = routes.Where(r => !_cooldown.TryGetValue(r.Url.ToString(), out var c) || c.Until <= now).ToArray();
        if (candidates.Length == 0) candidates = [.. routes]; // Everything is cooling down: try again rather than stay dark.

        // Probe them together and take the nearest that answered, so one dead address costs no extra time.
        var probes = candidates.Select(route => ProbeAsync(route, ct)).ToArray();
        var prepared = await Task.WhenAll(probes);
        PreparedRoute? chosen = null;
        for (var i = 0; i < candidates.Length; i++)
        {
            if (prepared[i] is null)
            {
                Penalize(candidates[i]);
                continue;
            }

            if (chosen is null) chosen = prepared[i];
            else await prepared[i]!.DisposeAsync();
        }

        if (chosen is not null) _log.LogInformation("Controller reached over {Network} at {Url}", chosen.Route.Network, chosen.Route.Url);
        return chosen;
    }

    private async Task<PreparedRoute?> ProbeAsync(NodeRoute route, CancellationToken ct)
    {
        PreparedRoute? prepared = null;
        try
        {
            var provider = _providers.FirstOrDefault(p => p.Id == route.Network);
            prepared = provider is null ? new PreparedRoute(route, route.Url) : await provider.PrepareAsync(route, ct);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(ProbeTimeout);
            using var response = await _http.GetAsync(new Uri(prepared.DialUrl, ClusterConstants.IdentityPath), deadline.Token);
            var identity = response.IsSuccessStatusCode ? ClusterJson.Read<ClusterIdentity>(await response.Content.ReadAsByteArrayAsync(deadline.Token)) : null;
            if (identity is not null && identity.Protocol == ClusterConstants.ProtocolVersion && (_credential is null || identity.ClusterId == _credential.ClusterId))
                return prepared;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException or InvalidOperationException)
        {
            if (ct.IsCancellationRequested) throw;
        }

        if (prepared is not null) await prepared.DisposeAsync();
        return null;
    }

    private async ValueTask TryUpgradeAsync(CancellationToken ct)
    {
        var better = Routes().Where(r => r.Reachability < _current!.Route.Reachability).ToArray();
        if (better.Length == 0) return;
        var upgraded = await SelectAsync(better, ct);
        if (upgraded is null) return;
        await _current!.DisposeAsync();
        _current = upgraded;
    }

    private void Penalize(NodeRoute route)
    {
        var key = route.Url.ToString();
        var failures = _cooldown.TryGetValue(key, out var previous) ? previous.Failures + 1 : 1;
        var seconds = Math.Min(300, 15 * Math.Pow(2, Math.Min(failures - 1, 5)));
        _cooldown[key] = (_time.GetUtcNow().AddSeconds(seconds), failures);
    }

    private async ValueTask FailCurrentAsync(string cause)
    {
        if (_current is null) return;
        _log.LogWarning("Lost the controller over {Network} ({Cause})", _current.Route.Network, cause);
        Penalize(_current.Route);
        await _current.DisposeAsync();
        _current = null;
    }

    /// <summary>Joins with the token on disk. Returns null when the caller should go on to a signed sync.</summary>
    private async ValueTask<LinkResult?> JoinAsync(PreparedRoute route, NodeDescriptor descriptor, CancellationToken ct)
    {
        var have = _credential is not null;
        var file = _options.Agent.JoinTokenFile;
        if (file.Length == 0 || !File.Exists(file))
            return have ? null : new LinkResult(LinkStatus.Rejected, Detail: "this node has no credential and Cluster:Agent:JoinTokenFile names no token");
        if (!ClusterSigner.TryParseJoinToken(await File.ReadAllTextAsync(file, ct), out var tokenId, out var joinKey))
            return have ? null : new LinkResult(LinkStatus.Rejected, Detail: "the join token is malformed");
        if (tokenId == _refusedTokenId || tokenId == _credential?.TokenId)
            return have ? null : new LinkResult(LinkStatus.Rejected, Detail: "the controller refused the join token (used, expired or unknown)");

        var clientNonce = ClusterSigner.NewSecret(16);
        var body = ClusterJson.WriteBytes(new JoinRequest(tokenId, clientNonce, descriptor, ClusterConstants.ProtocolVersion));
        var (status, payload, verified) = await SendAsync(route, "/cluster/v1/join", "join:" + tokenId, joinKey, body, ct);
        if (!verified) return await Distrust(status);
        if (status == HttpStatusCode.Unauthorized)
            return new LinkResult(LinkStatus.Rejected, Detail: "the controller rejected the request time; check this machine's clock");
        var response = status == HttpStatusCode.OK ? ClusterJson.Read<JoinResponse>(payload) : null;
        if (response is null)
        {
            _refusedTokenId = tokenId;
            return have ? null : new LinkResult(LinkStatus.Rejected, Detail: "the controller refused the join token (used, expired or unknown)");
        }

        _credential = new NodeCredential
        {
            ClusterId = response.ClusterId,
            NodeId = response.NodeId,
            Key = Convert.ToBase64String(ClusterSigner.NodeKey(joinKey, response.ClusterId, response.NodeId, clientNonce, response.ServerNonce)),
            TokenId = tokenId,
        };
        _notMember = false;
        _store.Save(_credential);
        _log.LogInformation("Joined cluster {ClusterId} as {NodeId}", response.ClusterId, response.NodeId);
        return null;
    }

    private async ValueTask<LinkResult> SignedSyncAsync(PreparedRoute route, NodeSyncRequest request, CancellationToken ct, bool retry)
    {
        var credential = _credential!;
        var key = Convert.FromBase64String(credential.Key);
        var addressed = request with { ControllerInstanceId = _controllerInstance, Sequence = ++_sequence };
        var (status, payload, verified) = await SendAsync(
            route, $"/cluster/v1/nodes/{credential.NodeId}/sync", credential.NodeId, key, ClusterJson.WriteBytes(addressed), ct);

        // An unknown node cannot be answered with a signature, because the controller holds no key for it.
        // That also means anything can say it, so it is not believed: the credential is kept, the address is
        // set aside like any other that failed to prove itself, and only a fresh join token changes anything.
        if (status == HttpStatusCode.NotFound && !verified)
        {
            _notMember = true;
            await FailCurrentAsync("unknown node");
            return new LinkResult(LinkStatus.NotMember, Detail: "an address answered that it does not know this node");
        }

        if (!verified) return await Distrust(status);
        _notMember = false;
        switch (status)
        {
            case HttpStatusCode.OK when ClusterJson.Read<NodeSyncResponse>(payload) is { } response:
                Learn(response.ControllerEndpoints);
                return new LinkResult(LinkStatus.Ok, response);
            case HttpStatusCode.PreconditionFailed when retry && ClusterJson.Read<ClusterIdentity>(payload) is { } identity:
                // The controller restarted (or this is first contact). Its signed answer names the process
                // that is serving now; address the request to it and send again.
                _controllerInstance = identity.ControllerInstanceId;
                return await SignedSyncAsync(route, request, ct, retry: false);
            case HttpStatusCode.Conflict:
                return new LinkResult(LinkStatus.Superseded);
            case HttpStatusCode.Gone:
                return new LinkResult(LinkStatus.Removed);
            case HttpStatusCode.Unauthorized:
                return new LinkResult(LinkStatus.Rejected, Detail: "the controller rejected the request time; check this machine's clock");
            case (HttpStatusCode)425:
                return new LinkResult(LinkStatus.Unreachable, Detail: "the controller had already applied a newer request from this node");
            default:
                return new LinkResult(LinkStatus.Rejected, Detail: $"the controller answered {(int)status}");
        }
    }

    /// <summary>An answer without a valid signature is not from the controller, whatever it says.</summary>
    private async ValueTask<LinkResult> Distrust(HttpStatusCode status)
    {
        await FailCurrentAsync($"unsigned answer {(int)status}");
        return new LinkResult(LinkStatus.Unreachable, Detail: "an address answered without proving it is the controller");
    }

    private async ValueTask<(HttpStatusCode Status, byte[] Body, bool Verified)> SendAsync(
        PreparedRoute route, string path, string signer, byte[] key, byte[] body, CancellationToken ct)
    {
        var timestamp = _time.GetUtcNow().ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var nonce = ClusterSigner.NewSecret(16);
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(route.DialUrl, path)) { Content = new ByteArrayContent(body) };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        message.Headers.Add(ClusterSigner.NodeHeader, signer);
        message.Headers.Add(ClusterSigner.TimestampHeader, timestamp);
        message.Headers.Add(ClusterSigner.NonceHeader, nonce);
        message.Headers.Add(ClusterSigner.SignatureHeader, ClusterSigner.SignRequest(key, "POST", path, timestamp, nonce, body));

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(CallTimeout);
        using var response = await _http.SendAsync(message, deadline.Token);
        var payload = await response.Content.ReadAsByteArrayAsync(deadline.Token);
        var signature = response.Headers.TryGetValues(ClusterSigner.SignatureHeader, out var values) ? values.FirstOrDefault() : null;
        var verified = ClusterSigner.Matches(ClusterSigner.SignResponse(key, nonce, (int)response.StatusCode, payload), signature);
        return (response.StatusCode, payload, verified);
    }

    private void Learn(IReadOnlyList<AdvertisedEndpoint> endpoints)
    {
        if (_credential is null || ClusterJson.Write(_credential.Learned) == ClusterJson.Write(endpoints)) return;
        _credential.Learned = [.. endpoints];
        _store.Save(_credential);
    }

    public async ValueTask DisposeAsync()
    {
        if (_current is not null) await _current.DisposeAsync();
    }
}
