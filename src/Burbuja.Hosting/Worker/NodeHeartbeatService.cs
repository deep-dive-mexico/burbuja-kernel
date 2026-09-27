using Burbuja.Agents.Abstractions.Nodes;
using Burbuja.Core.Options;
using Microsoft.Extensions.Options;

namespace Burbuja.Host.Worker;

public sealed class NodeHeartbeatService(
    IOptions<KernelOptions> kernel,
    IOptions<RuntimeOptions> runtime,
    INodeDirectory nodes,
    ILogger<NodeHeartbeatService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var role = kernel.Value.Role;
        if (!role.Equals("worker", StringComparison.OrdinalIgnoreCase)
            && !role.Equals("all", StringComparison.OrdinalIgnoreCase))
            return;
        var nodeId = string.IsNullOrWhiteSpace(kernel.Value.NodeId) ? KernelOptions.FallbackNodeId() : kernel.Value.NodeId;
        while (!stoppingToken.IsCancellationRequested)
        {
            nodes.Heartbeat(nodeId, DateTimeOffset.UtcNow);
            log.LogDebug("node heartbeat {NodeId}", nodeId);
            var interval = runtime.Value.HeartbeatInterval <= TimeSpan.Zero ? TimeSpan.FromSeconds(15) : runtime.Value.HeartbeatInterval;
            await Task.Delay(interval, stoppingToken);
        }
    }
}
