using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Json;
using Burbuja.Core.Arm;

namespace Burbuja.Core.Store;

/// <summary>
/// Read-modify-write over <see cref="IResourceStore"/>, lifted out of the agents' budget ledger so more
/// than one subsystem can book against a resource without inventing its own concurrency story.
///
/// <para><b>The problem it solves.</b> <see cref="IResourceStore"/> has no conditional create: an upsert
/// with a null ETag on a missing key is a blind insert, so two first-appends to the same key inside one
/// process can silently overwrite each other — one booking disappears and both writers believe they
/// succeeded. Two mechanisms close that, and both are needed:</para>
///
/// <list type="number">
/// <item>a <b>striped gate</b> serialises appends to one key inside the process. Striped rather than one
/// lock per key so the gate table is bounded; a collision between two unrelated keys costs a little
/// concurrency and never correctness.</item>
/// <item>an <b>ETag retry</b> handles the other process: a 412 re-reads and tries again, and a blind
/// create is confirmed by reading back that this writer's own entry is the one now stored.</item>
/// </list>
///
/// <para><b>What it does not do.</b> It does not merge — the caller decides what the new state is from the
/// old one. That is the point: the interesting part of every one of these appends is the arithmetic, and
/// hiding it behind a generic "add" would put a domain decision in an infrastructure helper.</para>
/// </summary>
public static class EnvelopeAppender
{
    private const int MaxAttempts = 8;

    private static readonly SemaphoreSlim[] Gates =
        [.. Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1))];

    /// <summary>One resource, read-modify-written under the key's gate, with ETag retries.</summary>
    /// <param name="store">Where the envelope lives.</param>
    /// <param name="key">The resource key; the gate is chosen from it.</param>
    /// <param name="type">The ARM type the envelope carries.</param>
    /// <param name="read">The current value, or null when the resource does not exist yet.</param>
    /// <param name="merge">
    /// The new value given the old one. Called again on every retry — a merge that captures state from
    /// outside will re-apply it, which is why <paramref name="idempotent"/> exists.
    /// </param>
    /// <param name="idempotent">
    /// When set, a blind create is confirmed by reading back: the store returns what was written rather
    /// than what is current, so a lost cross-process race is invisible without this check.
    /// </param>
    public static async ValueTask<ResourceEnvelope<JsonObject>> AppendAsync<T>(
        IResourceStore store,
        string key,
        string type,
        Func<ResourceEnvelope<JsonObject>?, T> read,
        Func<T, ResourceEnvelope<JsonObject>?, JsonObject> merge,
        Func<T, bool>? idempotent,
        CancellationToken ct)
        where T : class
    {
        var gate = Gates[(uint)StringComparer.Ordinal.GetHashCode(key) % (uint)Gates.Length];
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; attempt < MaxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                var existing = await store.GetAsync(key, ct).ConfigureAwait(false);
                var current = read(existing);
                if (idempotent is not null && existing is not null && idempotent(current))
                    return existing;

                var properties = merge(current, existing);
                try
                {
                    return await store.UpsertAsync(Build(key, type, properties, existing), existing?.ETag, ct).ConfigureAwait(false);
                }
                catch (ArmException ex) when (ex.Error.HttpStatus == 412)
                {
                    continue;
                }
            }

            throw new ArmException(ArmError.PreconditionFailed($"Could not append to '{key}' after {MaxAttempts} attempts."));
        }
        finally
        {
            gate.Release();
        }
    }

    private static ResourceEnvelope<JsonObject> Build(string key, string type, JsonObject properties, ResourceEnvelope<JsonObject>? existing) =>
        new(
            key,
            key[(key.LastIndexOf('/') + 1)..],
            type,
            existing?.Location ?? "local",
            existing?.Tags ?? new Dictionary<string, string>(),
            properties,
            ProvisioningState.Succeeded,
            // The system data is carried forward: a booking is not an operator's edit, and rewriting the
            // audit fields here would erase who created the resource.
            existing?.SystemData ?? new SystemData(DateTimeOffset.UtcNow, "system", "System", DateTimeOffset.UtcNow, "system", "System"),
            existing?.ETag ?? "");

    /// <summary>Deserialise an envelope's properties, or a fresh instance.</summary>
    public static T Read<T>(ResourceEnvelope<JsonObject>? envelope, Func<T> empty)
        where T : class =>
        envelope is null ? empty() : envelope.Properties.Deserialize<T>(JsonWire.Options) ?? empty();
}
