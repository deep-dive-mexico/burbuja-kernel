using Burbuja.Core.Options;
using Microsoft.Extensions.Options;

namespace Burbuja.Host.Worker;

/// <summary>Reserved for leftover worker topics. Deployments and agents.run have dedicated executors.</summary>
public sealed class WorkerPlaceholderService(IOptions<KernelOptions> kernel) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var role = kernel.Value.Role;
        if (!role.Equals("worker", StringComparison.OrdinalIgnoreCase)
            && !role.Equals("all", StringComparison.OrdinalIgnoreCase))
            return;
        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
    }
}
