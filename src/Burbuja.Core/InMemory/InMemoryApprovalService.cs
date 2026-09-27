using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Approvals;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Clock;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Options;
using Microsoft.Extensions.Options;

namespace Burbuja.Core.InMemory;

/// <summary>
/// C7: the in-memory approval store (records + grants). Process-local waiters live in
/// <see cref="InMemoryApprovalService"/>, so a durable implementation of this interface is enough
/// to survive a restart.
/// </summary>
public sealed class InMemoryApprovalStore : IApprovalStore
{
    private readonly ConcurrentDictionary<string, ApprovalRecord> _records = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ApprovalGrant> _grants = new(StringComparer.Ordinal);

    public ApprovalRecord? Get(string id) => _records.TryGetValue(id, out var record) ? record : null;

    public void Put(ApprovalRecord record) => _records[record.Id] = record;

    public IReadOnlyCollection<ApprovalRecord> All() => _records.Values.ToArray();

    public ApprovalGrant? FindGrant(string decisionKey, string? shapeKey, string? transition, DateTimeOffset now)
    {
        // The exact call first: a grant for this precise decision is the narrower permission, so it
        // wins when both exist. Either lookup must also agree about the TRANSITION — a grant minted for
        // one named change never satisfies another, and matching without that check would hand out a
        // permission nobody gave whenever two transitions arrive through the same tool.
        if (_grants.TryGetValue(GrantKey(decisionKey, shapeKey, transition), out var exact)
            && exact.ExpiresAt > now
            && SameTransition(exact, transition))
        {
            return exact;
        }

        // M7.2b: then a SHAPE-wide grant. "🔁 similares" pre-approves every later command of the same
        // shape, and those commands have a different decision key by construction — the key is derived
        // from the command, the shape from its structure. Without this fallback a shape grant could be
        // written (the approve path always stored one) and never once be read, which is the kind of
        // bug that looks like the feature simply does not work.
        if (!string.IsNullOrEmpty(shapeKey)
            && _grants.TryGetValue(ShapeGrantKey(shapeKey, transition), out var byShape)
            && byShape.ExpiresAt > now
            && SameTransition(byShape, transition))
        {
            return byShape;
        }

        return null;
    }

    /// <summary>
    /// Whether a stored grant is for the transition being asked about. An unnamed transition on either
    /// side matches only an unnamed transition on the other: treating "no name" as a wildcard would make
    /// every pre-A7 grant satisfy every A7 transition.
    /// </summary>
    private static bool SameTransition(ApprovalGrant grant, string? transition) =>
        string.Equals(grant.Transition ?? "", transition ?? "", StringComparison.Ordinal);

    public void PutGrant(ApprovalGrant grant) => _grants[GrantKey(grant.DecisionKey, grant.ShapeKey, grant.Transition)] = grant;

    /// <summary>
    /// The grant key. It carries the transition so that two transitions sharing a decision key cannot
    /// overwrite each other's grant — a collision that would look like "the second approval silently
    /// replaced the first" and would be almost impossible to see from the outside.
    /// </summary>

    public void RemoveGrantsForStream(string streamId)
    {
        foreach (var kv in _grants.ToArray())
        {
            if (kv.Value.StreamId.Equals(streamId, StringComparison.Ordinal))
                _grants.TryRemove(kv.Key, out _);
        }
    }

    public void PurgeGrants(DateTimeOffset now)
    {
        foreach (var kv in _grants.ToArray())
        {
            if (kv.Value.ExpiresAt <= now)
                _grants.TryRemove(kv.Key, out _);
        }
    }

    /// <summary>
    /// The grant key. It carries the TRANSITION as well as the decision and shape, so two transitions
    /// sharing a decision key cannot overwrite each other's grant — a collision that would look like "the
    /// second approval silently replaced the first" and would be nearly invisible from the outside.
    /// </summary>
    internal static string GrantKey(string decisionKey, string? shapeKey, string? transition = null) =>
        transition is { Length: > 0 } t
            ? t + "\u001f" + decisionKey + "\n" + (shapeKey ?? "")
            : decisionKey + "\n" + (shapeKey ?? "");

    /// <summary>A grant that names no decision: it applies to any command of this shape.</summary>
    internal static string ShapeGrantKey(string shapeKey, string? transition = null) => GrantKey("", shapeKey, transition);
}

public sealed class InMemoryApprovalService(
    IClock clock,
    TimeProvider time,
    IEventLog events,
    IOptions<PolicyOptions> policy,
    IApprovalStore store) : IApprovalService
{
    private sealed class Waiter
    {
        public TaskCompletionSource<ApprovalOutcome> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly ConcurrentDictionary<string, Waiter> _waiters = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public async ValueTask<string> OpenAsync(ApprovalRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var now = clock.UtcNow;
        store.PurgeGrants(now);
        if (request.Grantable && store.FindGrant(request.DecisionKey, request.ShapeKey, request.Transition, now) is { } grant
            && grant.StreamId.Equals(request.StreamId, StringComparison.Ordinal))
        {
            var reused = new Approved(true, grant.Scope, "reused");
            var reusedId = Guid.NewGuid().ToString("N");
            var reusedRecord = new ApprovalRecord(reusedId, request, reused, now, now, now, "grant");
            store.Put(reusedRecord);
            _waiters[reusedId] = new Waiter();
            _waiters[reusedId].Tcs.TrySetResult(reused);
            await EmitAsync(reusedRecord, "core.approval.opened", ct);
            await EmitAsync(reusedRecord, "core.approval.granted", ct);
            return reusedId;
        }

        var openId = Guid.NewGuid().ToString("N");
        var timeout = request.Timeout < TimeSpan.Zero ? TimeSpan.Zero : request.Timeout;
        var opened = new ApprovalRecord(openId, request, null, now, now + timeout, null, null);
        store.Put(opened);
        _waiters[openId] = new Waiter();
        await EmitAsync(opened, "core.approval.opened", ct);
        return openId;
    }

    public async ValueTask<ApprovalOutcome> WaitAsync(string approvalId, CancellationToken ct)
    {
        var record = store.Get(approvalId);
        if (record is null)
            return new Cancelled();
        if (record.Outcome is { } already)
            return already;

        // No in-process waiter: this process did not open the approval (e.g. it restarted). Poll
        // the store instead of failing — durability is the point of the store seam.
        if (!_waiters.TryGetValue(approvalId, out var waiter))
            return await PollAsync(approvalId, ct);

        var now = time.GetUtcNow();
        var untilExpiry = record.ExpiresAt - now;
        if (untilExpiry <= TimeSpan.Zero)
            return await DecideAsync(approvalId, new TimedOut(), "system", "core.approval.timed_out", ct);

        var deferAfter = policy.Value.DeferAfter;
        var deferWait = deferAfter <= TimeSpan.Zero ? untilExpiry : deferAfter;
        var wait = untilExpiry < deferWait ? untilExpiry : deferWait;
        using var timeoutCts = new CancellationTokenSource(wait, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            return await waiter.Tcs.Task.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            if (time.GetUtcNow() >= record.ExpiresAt)
                return await DecideAsync(approvalId, new TimedOut(), "system", "core.approval.timed_out", ct);
            return new Deferred();
        }
    }

    private async ValueTask<ApprovalOutcome> PollAsync(string approvalId, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var record = store.Get(approvalId);
            if (record is null)
                return new Cancelled();
            if (record.Outcome is { } decided)
                return decided;
            if (time.GetUtcNow() >= record.ExpiresAt)
                return await DecideAsync(approvalId, new TimedOut(), "system", "core.approval.timed_out", ct);
            await Task.Delay(TimeSpan.FromMilliseconds(50), time, ct);
        }
    }

    public ValueTask<ApprovalOutcome> ApproveAsync(string approvalId, string? note, string? grantScope, double? grantHours, CancellationToken ct, string? actorId = null) =>
        DecideAsync(approvalId, new Approved(false, grantScope, note), Actor(actorId), "core.approval.approved", ct, grantScope, grantHours);

    public ValueTask<ApprovalOutcome> DenyAsync(string approvalId, string? note, CancellationToken ct, string? actorId = null) =>
        DecideAsync(approvalId, new Denied(note), Actor(actorId), "core.approval.denied", ct);

    public ValueTask<ApprovalOutcome> AnswerAsync(string approvalId, string answer, CancellationToken ct, string? actorId = null) =>
        DecideAsync(approvalId, new Answered(answer), Actor(actorId), "core.approval.answered", ct);

    public async ValueTask<ApprovalOutcome> CancelAsync(string approvalId, CancellationToken ct, string? actorId = null)
    {
        if (store.Get(approvalId) is { } record)
            store.RemoveGrantsForStream(record.Request.StreamId);
        return await DecideAsync(approvalId, new Cancelled(), Actor(actorId), "core.approval.cancelled", ct);
    }

    /// <summary>
    /// Who decided. `human` is the documented fallback for a caller with no principal — it is NOT a
    /// default worth reaching for, because a record that says `human` about a specific person's tap is
    /// worse than one that says nothing.
    /// </summary>
    private static string Actor(string? actorId) => string.IsNullOrWhiteSpace(actorId) ? "human" : actorId;

    public ValueTask<ApprovalRecord?> GetAsync(string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return new(store.Get(id));
    }

    public ValueTask<PagedResult<ApprovalRecord>> ListAsync(ApprovalQuery query, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var rows = store.All().AsEnumerable();
        if (query.SubjectType is not null)
            rows = rows.Where(r => r.Request.SubjectType.Equals(query.SubjectType, StringComparison.OrdinalIgnoreCase));
        if (query.SubjectId is not null)
            rows = rows.Where(r => r.Request.SubjectId.Equals(query.SubjectId, StringComparison.OrdinalIgnoreCase));
        if (query.StreamId is not null)
            rows = rows.Where(r => r.Request.StreamId.Equals(query.StreamId, StringComparison.OrdinalIgnoreCase));
        if (query.PendingOnly)
            rows = rows.Where(r => r.Outcome is null);
        var ordered = rows.OrderBy(r => r.OpenedAt).ThenBy(r => r.Id, StringComparer.Ordinal).ToList();
        var skip = 0;
        if (!string.IsNullOrEmpty(query.ContinuationToken)
            && int.TryParse(query.ContinuationToken, out var parsed))
            skip = Math.Max(0, parsed);
        var take = Math.Clamp(query.Top <= 0 ? 50 : query.Top, 1, 1000);
        var page = ordered.Skip(skip).Take(take).ToList();
        var next = skip + page.Count < ordered.Count ? (skip + page.Count).ToString() : null;
        return new(new PagedResult<ApprovalRecord>(page, next));
    }

    private async ValueTask<ApprovalOutcome> DecideAsync(
        string id,
        ApprovalOutcome outcome,
        string decidedBy,
        string eventType,
        CancellationToken ct,
        string? grantScope = null,
        double? grantHours = null)
    {
        ApprovalRecord? decided;
        lock (_gate)
        {
            var existing = store.Get(id);
            if (existing is null)
                return outcome;
            if (existing.Outcome is { } already)
                return already;

            var now = clock.UtcNow;
            decided = existing with { Outcome = outcome, DecidedAt = now, DecidedBy = decidedBy };
            store.Put(decided);
            if (_waiters.TryGetValue(id, out var waiter))
                waiter.Tcs.TrySetResult(outcome);
            if (outcome is Approved && grantHours is > 0)
            {
                var req = decided.Request;
                var expires = now + TimeSpan.FromHours(grantHours.Value);
                store.PutGrant(new ApprovalGrant(req.DecisionKey, req.ShapeKey, req.StreamId, grantScope, expires, req.Transition));

                // M7.2b: "🔁 similares" grants the SHAPE, not this one command, so the shape row is
                // written alongside the exact one. `GrantScope.Shape` is the marker: the Discord card
                // and `pomni approve --similar` both send it, and nothing else may claim a shape-wide
                // permission by accident.
                if (req.ShapeKey is { Length: > 0 } shape && GrantScope.IsShape(grantScope, shape))
                    store.PutGrant(new ApprovalGrant("", shape, req.StreamId, grantScope, expires, req.Transition));
            }
        }

        await EmitAsync(decided, eventType, ct);
        return outcome;
    }

    private async ValueTask EmitAsync(ApprovalRecord rec, string type, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["approvalId"] = rec.Id,
            ["subjectId"] = rec.Request.SubjectId,
            ["decisionKey"] = rec.Request.DecisionKey,
        };
        await events.AppendAsync(new EventAppend(
            rec.Request.StreamId,
            rec.Request.SubStreamId,
            type,
            type,
            payload,
            RenderLane.Monitor | RenderLane.Log,
            rec.Request.SubjectId,
            null,
            "core.approval"), ct);
    }
}
