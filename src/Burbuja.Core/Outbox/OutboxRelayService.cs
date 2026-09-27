using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Bus;
using Burbuja.Core.Abstractions.Outbox;
using Burbuja.Core.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Burbuja.Core.Outbox;

public sealed class OutboxRelayService(
    IOutboxStore store,
    IMessageBus bus,
    TimeProvider time,
    IOptions<RuntimeOptions> runtime,
    IOptions<KernelOptions> kernel,
    ILogger<OutboxRelayService> log) : IHostedService
{
    public const int MaxAttempts = 8;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!IsControlPlane(kernel.Value.Role))
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

    internal async Task DrainOnceAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var claimed = await store.ClaimAsync(32, now, ct);
        foreach (var rec in claimed)
        {
            try
            {
                var payload = rec.Message.Payload.DeepClone();
                await bus.PublishAsync(new BusMessage(
                    rec.Message.MessageId,
                    rec.Message.Topic,
                    payload,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["aggregateId"] = rec.Message.AggregateId.ToString(),
                    }), ct);
                await store.MarkSentAsync(rec.Message.MessageId, ct);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Outbox relay failed for {MessageId} attempt {Attempt}", rec.Message.MessageId, rec.Attempts);
                if (rec.Attempts >= MaxAttempts)
                    await store.MarkDeadAsync(rec.Message.MessageId, ex.Message, ct);
                else
                {
                    var delay = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, rec.Attempts - 1)));
                    await store.RetryLaterAsync(rec.Message.MessageId, now + delay, ct);
                }
            }
        }
    }

    private async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var interval = runtime.Value.OutboxRelayInterval;
                if (interval <= TimeSpan.Zero)
                    interval = TimeSpan.FromSeconds(2);
                await Task.Delay(interval, time, ct);
                await DrainOnceAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Outbox relay loop failed");
            }
        }
    }

    internal static bool IsControlPlane(string role) =>
        role.Equals("control-plane", StringComparison.OrdinalIgnoreCase)
        || role.Equals("all", StringComparison.OrdinalIgnoreCase);
}
