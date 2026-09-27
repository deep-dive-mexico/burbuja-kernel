using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Bus;
using Burbuja.Core.Abstractions.Json;
using Burbuja.Deployments.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Burbuja.Deployments.Execution;

public sealed class DeploymentExecutor(
    IMessageBus bus,
    IResourceStore store,
    DeploymentStateStore states,
    DeploymentRunner runner,
    IConfiguration configuration) : IHostedService
{
    private IAsyncDisposable? _sub;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var role = configuration["Kernel:Role"] ?? "all";
        if (!role.Equals("worker", StringComparison.OrdinalIgnoreCase)
            && !role.Equals("all", StringComparison.OrdinalIgnoreCase))
            return;
        _sub = await bus.SubscribeAsync(DeploymentsConstants.TopicExecute, OnMessage, cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_sub is not null)
            await _sub.DisposeAsync();
    }

    internal async ValueTask OnMessage(BusMessage message, CancellationToken ct)
    {
        var payload = message.Payload as JsonObject ?? message.Payload.Deserialize<JsonObject>();
        var deploymentKey = payload?["deploymentKey"]?.GetValue<string>();
        var environmentKey = payload?["environmentKey"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(deploymentKey) || string.IsNullOrWhiteSpace(environmentKey))
            return;
        var envRaw = await store.GetAsync(environmentKey, ct);
        if (envRaw is null)
            return;
        var environment = envRaw.Properties.Deserialize<EnvironmentProperties>(JsonWire.Options)
                          ?? new EnvironmentProperties();
        _ = await states.GetAsync(deploymentKey, ct);
        await runner.RunAsync(deploymentKey, envRaw.Id, environment, ct);
    }
}
