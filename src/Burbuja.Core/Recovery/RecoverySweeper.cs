using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Recovery;
using Burbuja.Core.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Burbuja.Core.Recovery;

/// <summary>
/// C7: sweeps recovery candidates by indexed store queries. Only handlers that return a
/// <see cref="RecoveryQuery"/> are swept; each candidate resource id is handed to
/// <see cref="IStreamRecoveryHandler.RecoverAsync"/> exactly once per sweep.
/// </summary>
public sealed class RecoverySweeper(
    IEnumerable<IStreamRecoveryHandler> handlers,
    IResourceStore store,
    TimeProvider time,
    IOptions<RuntimeOptions> runtime,
    IOptions<KernelOptions> kernel,
    ILogger<RecoverySweeper> log) : IHostedService
{
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var role = kernel.Value.Role;
        if (!role.Equals("control-plane", StringComparison.OrdinalIgnoreCase)
            && !role.Equals("all", StringComparison.OrdinalIgnoreCase))
            return Task.CompletedTask;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Loop(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is null)
            return;
        await _cts.CancelAsync();
        if (_loop is not null)
            await _loop.WaitAsync(cancellationToken);
    }

    private async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var interval = runtime.Value.RecoverySweepInterval;
                if (interval <= TimeSpan.Zero)
                    interval = TimeSpan.FromSeconds(30);
                await Task.Delay(interval, time, ct);
                await SweepOnceAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Recovery sweep failed");
            }
        }
    }

    internal async Task SweepOnceAsync(CancellationToken ct)
    {
        foreach (var handler in handlers)
        {
            if (handler.Query is not { } query)
                continue;

            string? token = null;
            do
            {
                var page = await store.ListAsync(
                    new ResourceStoreQuery(null, null, query.Namespace, query.Type, query.PageSize, token),
                    ct);
                foreach (var env in page.Value)
                {
                    if (query.States.Count > 0
                        && !query.States.Contains(env.ProvisioningState.ToString(), StringComparer.OrdinalIgnoreCase))
                        continue;
                    await handler.RecoverAsync(env.Id, ct);
                }

                token = page.NextLink;
            }
            while (!string.IsNullOrEmpty(token));
        }
    }
}
