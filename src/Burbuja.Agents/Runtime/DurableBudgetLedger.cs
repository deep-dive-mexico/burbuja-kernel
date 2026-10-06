using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Agents.Abstractions;
using Burbuja.Agents.Abstractions.Budget;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Json;
using Burbuja.Core.Arm;

namespace Burbuja.Agents.Runtime;

/// <summary>
/// Durable <see cref="IBudgetLedger"/> over <see cref="IResourceStore"/>: one resource per UTC day
/// plus a per-job index, so both reads are a single resource read and both become durable for free
/// once Store.Sqlite replaces the in-memory store. There is no new table and no cross-plugin
/// replace — the ledger is just two more resource types.
/// </summary>
public sealed class DurableBudgetLedger(IResourceStore store, TimeProvider time) : IBudgetLedger
{
    internal const string DayType = AgentsConstants.Namespace + "/ledgers";
    internal const string JobType = AgentsConstants.Namespace + "/ledger-jobs";

    /// <summary>How a day's bookings are stored; also the shape of the `ledgers` projection.</summary>
    public sealed class DayEnvelope
    {
        public List<LedgerEntry> Bookings { get; set; } = [];
    }

    private const int MaxAttempts = 8;

    /// <summary>
    /// <see cref="IResourceStore"/> has no conditional create — an upsert with a null ETag on a
    /// missing key is a blind insert — so two first-bookings of the same day inside one process
    /// could overwrite each other. Appends to one key are serialised here (striped, so the gate
    /// table is bounded); a 412 from another process still re-reads and retries below.
    /// </summary>
    private static readonly SemaphoreSlim[] Gates = [.. Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1))];

    public async ValueTask<Guid> BookAsync(BudgetBooking booking, CancellationToken ct)
    {
        var day = DateOnly.FromDateTime((booking.BookedAt ?? time.GetUtcNow()).UtcDateTime);
        var entry = new LedgerEntry(booking.IdempotencyId ?? Guid.NewGuid(), booking.JobId, booking.RunId, booking.Kind, booking.Usd, booking.InputTokens, booking.OutputTokens);
        if (booking.IdempotencyId is not null &&
            (await ReadAsync(JobKey(booking.JobId), ct)).FirstOrDefault(existing => existing.Id == entry.Id) is { } recorded)
        {
            VerifyIdentity(recorded, entry);
            return recorded.Id;
        }
        await AppendAsync(DayKey(day), DayType, entry, ct).ConfigureAwait(false);
        await AppendAsync(JobKey(booking.JobId), JobType, entry, ct).ConfigureAwait(false);
        return entry.Id;
    }

    public async ValueTask<DailySpend> GetDailyAsync(DateOnly day, CancellationToken ct)
    {
        var bookings = await ReadAsync(DayKey(day), ct).ConfigureAwait(false);
        return new DailySpend(day, Total(bookings), bookings.Select(b => b.JobId).Distinct().Count());
    }

    public async ValueTask<decimal> GetJobSpendAsync(Guid jobId, CancellationToken ct) =>
        Total(await ReadAsync(JobKey(jobId), ct).ConfigureAwait(false));

    public static string DayPrefix => $"/providers/{AgentsConstants.Namespace}/ledgers";

    public static string DayKey(DateOnly day) =>
        $"{DayPrefix}/{day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";

    /// <summary>
    /// One resource per job, not per day: <see cref="GetJobSpendAsync"/> has no day parameter, so a
    /// day-scoped child could only be found by scanning every day — the unbounded read this design
    /// exists to avoid. A job-scoped index is one read and keeps the job total cumulative across
    /// midnight, which is what the in-memory ledger returned.
    /// </summary>
    public static string JobKey(Guid jobId) =>
        $"/providers/{AgentsConstants.Namespace}/ledger-jobs/{jobId:N}";

    private async ValueTask<IReadOnlyList<LedgerEntry>> ReadAsync(string key, CancellationToken ct)
    {
        var env = await store.GetAsync(key, ct).ConfigureAwait(false);
        return env is null ? [] : Entries(env);
    }

    private async ValueTask AppendAsync(string key, string type, LedgerEntry entry, CancellationToken ct)
    {
        var gate = Gates[(uint)StringComparer.Ordinal.GetHashCode(key) % (uint)Gates.Length];
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; attempt < MaxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                var existing = await store.GetAsync(key, ct).ConfigureAwait(false);
                var bookings = existing is null ? [] : Entries(existing).ToList();
                if (bookings.FirstOrDefault(b => b.Id == entry.Id) is { } recorded)
                {
                    VerifyIdentity(recorded, entry);
                    return;
                }
                bookings.Add(entry);
                try
                {
                    await store.UpsertAsync(ToJson(key, type, bookings, existing), existing?.ETag, ct).ConfigureAwait(false);
                }
                catch (ArmException ex) when (ex.Error.HttpStatus == 412)
                {
                    continue;
                }

                if (existing is not null)
                    return;
                // A blind create can lose a cross-process race with another writer; the store
                // returns what was written, not what is current, so confirm by reading it back.
                var current = await store.GetAsync(key, ct).ConfigureAwait(false);
                if (current is not null && Entries(current).Any(b => b.Id == entry.Id))
                    return;
            }

            throw new ArmException(ArmError.PreconditionFailed($"Could not append to the budget ledger '{key}'."));
        }
        finally
        {
            gate.Release();
        }
    }

    private static decimal Total(IReadOnlyList<LedgerEntry> bookings) => bookings.Sum(b => b.Usd);

    private static void VerifyIdentity(LedgerEntry recorded, LedgerEntry requested)
    {
        if (recorded != requested)
            throw new ArmException(ArmError.Conflict("BookingIdentityConflict",
                "The charge identity already belongs to different usage or pricing."));
    }

    private static List<LedgerEntry> Entries(ResourceEnvelope<JsonObject> env) =>
        (env.Properties.Deserialize<DayEnvelope>(JsonWire.Options) ?? new DayEnvelope()).Bookings;

    private ResourceEnvelope<JsonObject> ToJson(string key, string type, List<LedgerEntry> bookings, ResourceEnvelope<JsonObject>? existing)
    {
        var now = time.GetUtcNow();
        return new ResourceEnvelope<JsonObject>(
            key,
            key[(key.LastIndexOf('/') + 1)..],
            type,
            existing?.Location ?? "local",
            existing?.Tags ?? new Dictionary<string, string>(),
            JsonSerializer.SerializeToNode(new DayEnvelope { Bookings = bookings }, JsonWire.Options)!.AsObject(),
            ProvisioningState.Succeeded,
            existing?.SystemData ?? new SystemData(now, "system", "System", now, "system", "System"),
            existing?.ETag ?? "");
    }
}

/// <summary>
/// One booked turn. The id is what makes an append verifiable: with a blind create there is no ETag
/// to compare, so the writer checks that its own booking is the one now stored.
/// </summary>
public sealed record LedgerEntry(
    Guid Id,
    Guid JobId,
    Guid RunId,
    BudgetKind Kind,
    decimal Usd,
    int InputTokens,
    int OutputTokens);
