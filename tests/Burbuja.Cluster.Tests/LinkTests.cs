using System.Net;
using System.Text;
using Burbuja.Cluster.Abstractions;
using Burbuja.Cluster.Networks;
using Burbuja.Cluster.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Burbuja.Cluster.Tests;

/// <summary>A node's link to its controller against an address that answers, but cannot prove who it is.</summary>
public sealed class LinkTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cluster-link-" + Guid.NewGuid().ToString("N"));

    public LinkTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, true);

    /// <summary>Answers the public identity document correctly and everything else without a signature.</summary>
    private sealed class Unsigned(HttpStatusCode syncStatus) : HttpMessageHandler
    {
        public readonly List<string> Posts = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(ClusterJson.Write(new ClusterIdentity("cluster-1", "controller-1", ClusterConstants.ProtocolVersion)), Encoding.UTF8, "application/json"),
                });
            }

            lock (Posts) Posts.Add(path);
            return Task.FromResult(new HttpResponseMessage(path.EndsWith("/join", StringComparison.Ordinal) ? HttpStatusCode.OK : syncStatus)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public async Task An_unsigned_unknown_node_answer_never_costs_a_node_its_credential()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = Convert.ToBase64String(ClusterSigner.NodeKey([7], "cluster-1", "studio", "a", "b"));
        var credential = Path.Combine(_root, "node.json");
        File.WriteAllText(credential, $$"""{"clusterId":"cluster-1","nodeId":"studio","key":"{{key}}","tokenId":"0000000000000000","learned":[]}""");

        var options = new ClusterOptions { Enabled = true, Role = "agent", NodeId = "studio", StateDirectory = _root };
        options.Agent.Controllers.Add("http://127.0.0.1:9");
        options.Agent.JoinTokenFile = Path.Combine(_root, "join.token");
        var handler = new Unsigned(HttpStatusCode.NotFound);
        var link = new HttpControllerLink(options, [new LoopbackNetwork()], new HttpClient(handler), TimeProvider.System, NullLogger<HttpControllerLink>.Instance);
        var request = new NodeSyncRequest { InstanceId = "process-1", Descriptor = new NodeDescriptor { NodeId = "studio" } };

        // Anything can answer 404. It is reported, the address is set aside, and the credential stays.
        (await link.SyncAsync(request, ct)).Status.ShouldBe(LinkStatus.NotMember);
        (await link.SyncAsync(request, ct)).Status.ShouldBe(LinkStatus.NotMember);
        handler.Posts.ShouldBe(["/cluster/v1/nodes/studio/sync", "/cluster/v1/nodes/studio/sync"]);
        File.ReadAllText(credential).ShouldContain(key);

        // With a fresh join token on disk the node does try to join again, but an unsigned "welcome" is not
        // believed either: the old credential is still the one on disk, and the token is not burnt on a lie.
        File.WriteAllText(options.Agent.JoinTokenFile, ClusterSigner.NewJoinToken().Token);
        (await link.SyncAsync(request, ct)).Status.ShouldBe(LinkStatus.Unreachable);
        handler.Posts.Last().ShouldBe("/cluster/v1/join");
        File.ReadAllText(credential).ShouldContain(key);
        await link.DisposeAsync();
    }
}
