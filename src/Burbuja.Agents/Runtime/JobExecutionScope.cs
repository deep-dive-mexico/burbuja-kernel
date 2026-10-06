using Burbuja.Agents.Model;
using Burbuja.Core.Abstractions.Arm;

namespace Burbuja.Agents.Runtime;

/// <summary>Internal execution ownership and run-local view; consumers continue crossing the public job interface.</summary>
internal static class JobExecutionScope
{
    private static readonly AsyncLocal<Ownership?> Ambient = new();
    public static Ownership? Current => Ambient.Value;
    public sealed record Ownership(string JobKey, string FenceToken, Guid? RunId = null, bool OwnsTerminal = true);

    public static IDisposable Enter(Ownership? value)
    {
        var previous = Ambient.Value;
        Ambient.Value = value;
        return new Restore(previous);
    }

    public static IDisposable Run(Guid runId, bool ownsTerminal) =>
        Enter(Current is { } owner ? owner with { RunId = runId, OwnsTerminal = ownsTerminal } : null);

    public static void EnsureOwned(string key, JobProperties properties)
    {
        if (Current is { } owner && key.Equals(owner.JobKey, StringComparison.OrdinalIgnoreCase)
            && properties.Recovery?.FenceToken != owner.FenceToken)
            throw new OperationCanceledException("Job execution ownership changed.");
    }

    public static async ValueTask EnsureOwnedAsync(IResourceStore store, CancellationToken ct)
    {
        if (Current is not { } owner)
            return;
        var job = await store.GetAsync(owner.JobKey, ct);
        if (job?.Properties["recovery"]?["fenceToken"]?.GetValue<string>() != owner.FenceToken)
            throw new OperationCanceledException("Job execution ownership changed.");
    }

    private sealed class Restore(Ownership? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }
}
