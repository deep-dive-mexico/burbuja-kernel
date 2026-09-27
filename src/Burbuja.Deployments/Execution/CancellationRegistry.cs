using System.Collections.Concurrent;

namespace Burbuja.Deployments.Execution;

public sealed class CancellationRegistry
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _tokens = new(StringComparer.OrdinalIgnoreCase);

    public CancellationToken Register(string deploymentKey)
    {
        var cts = new CancellationTokenSource();
        _tokens[deploymentKey] = cts;
        return cts.Token;
    }

    public void Cancel(string deploymentKey)
    {
        if (_tokens.TryGetValue(deploymentKey, out var cts))
            cts.Cancel();
    }

    public void Forget(string deploymentKey)
    {
        if (_tokens.TryRemove(deploymentKey, out var cts))
            cts.Dispose();
    }
}
