using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Approvals;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Clock;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Scopes;
using Burbuja.Core.Arm;
using Burbuja.Core.Store;

namespace Burbuja.Core.Scopes;

/// <summary>
/// A6/A3: <see cref="IScopeStateStore"/> over <see cref="IResourceStore"/>, one resource per scope.
///
/// <para><b>One envelope, one compare-and-set.</b> The ladder, the rung, the halt latch and its evidence
/// live in one resource, so latching a halt is a single write — a halt that took two could be found
/// half-applied, and "the scope is halted but nobody recorded why" is the state the evidence requirement
/// exists to prevent.</para>
///
/// <para><b>Not met, and named in the docs rather than implied:</b> the design of record has the limit
/// ledger sharing this envelope so that "100 % ⇒ HALTED" is also one write. It is two today — the breach
/// lands in the limit resource and the halt in this one — so a crash between them leaves a breach with no
/// halt. Closing that means moving the buckets into this envelope; it is a real gap, not a rounding
/// error.</para>
/// </summary>
public sealed class DurableScopeStore(
    IApprovalService approvals,
    IResourceStore store,
    IEventLog events,
    IClock clock) : IScopeStateStore, IScopeStateResolver
{
    public const string Namespace = "Burbuja.Resources";

    public const string Type = Namespace + "/scopes";

    public static string Key(string scope) => $"{Namespace}/scopes/{Sanitize(scope)}";

    public async ValueTask<ScopeState> GetAsync(string scope, CancellationToken ct)
    {
        var envelope = await store.GetAsync(Key(scope), ct).ConfigureAwait(false);
        return envelope is null
            ? new ScopeState(scope, "", [])
            : (envelope.Properties.Deserialize<ScopeState>(Burbuja.Core.Abstractions.Json.JsonWire.Options)
               ?? new ScopeState(scope, "", [])) with { Scope = scope };
    }

    public async ValueTask<ScopeState?> ResolveAsync(string scope, CancellationToken ct)
    {
        var envelope = await store.GetAsync(Key(scope), ct).ConfigureAwait(false);
        return envelope is null
            ? null
            : envelope.Properties.Deserialize<ScopeState>(Burbuja.Core.Abstractions.Json.JsonWire.Options);
    }

    public async ValueTask<ScopeState> EnsureAsync(string scope, IReadOnlyList<string> ladder, CancellationToken ct)
    {
        if (ladder.Count == 0)
            throw new ArgumentException("a scope ladder must have at least one rung", nameof(ladder));

        var existing = await GetAsync(scope, ct).ConfigureAwait(false);
        if (existing.Ladder.Count > 0)
        {
            // A ladder that CHANGED is not silently adopted. Re-basing a scope onto new rungs would move
            // its autonomy without an approval, which is the one thing promotion exists to require.
            if (!existing.Ladder.SequenceEqual(ladder, StringComparer.Ordinal))
            {
                throw new ArmException(ArmError.Validation(
                    $"Scope '{scope}' already has a ladder [{string.Join(", ", existing.Ladder)}]; "
                    + $"refusing to replace it with [{string.Join(", ", ladder)}].",
                    []));
            }

            return existing;
        }

        var created = new ScopeState(scope, ladder[0], ladder);
        await WriteAsync(created, ct).ConfigureAwait(false);
        return created;
    }

    public async ValueTask<ScopeChange> HaltAsync(string scope, ScopeHalt halt, CancellationToken ct)
    {
        var state = await GetAsync(scope, ct).ConfigureAwait(false);
        if (state.Ladder.Count == 0)
            return ScopeChange.Refused(state, $"Scope '{scope}' has no ladder; call Ensure before halting it.");

        // "No containment without evidence." A halt that cannot name what it saw is indistinguishable
        // from a bug, and the check lives here because a caller is exactly what might forget it.
        if (halt.EvidenceRefs is not { Count: > 0 })
            return ScopeChange.Refused(state, "A halt requires at least one evidence reference.");
        if (string.IsNullOrWhiteSpace(halt.Reason))
            return ScopeChange.Refused(state, "A halt requires a reason.");
        if (string.IsNullOrWhiteSpace(halt.Actor))
            return ScopeChange.Refused(state, "A halt requires an actor.");

        // Idempotent: halting an already-halted scope keeps the FIRST evidence. The later halt did not
        // cause the stop, and overwriting the reason would replace the record of why with the record of
        // somebody noticing again.
        if (state.Halted)
            return new ScopeChange(true, state);

        var at = halt.At ?? clock.UtcNow;
        var halted = state with
        {
            Halted = true,
            HaltReason = halt.Reason,
            HaltEvidence = halt.EvidenceRefs,
            HaltedAt = at,
            HaltedBy = halt.Actor,
        };
        await WriteAsync(halted, ct).ConfigureAwait(false);
        await EmitAsync(scope, "core.scope.halted", halted, halt.Reason, ct).ConfigureAwait(false);
        return new ScopeChange(true, halted);
    }

    public async ValueTask<ScopeChange> ResumeAsync(string scope, ScopeResume resume, CancellationToken ct)
    {
        var state = await GetAsync(scope, ct).ConfigureAwait(false);
        if (!state.Halted)
            return ScopeChange.Refused(state, $"Scope '{scope}' is not halted.");

        if (string.IsNullOrWhiteSpace(resume.DiagnosisRef))
            return ScopeChange.Refused(state, "A resume requires a diagnosis reference.");

        if (!await ScopeApprovalCheck.IsApprovedAsync(approvals, resume.ApprovalId, ScopeTransitions.ResumeFromHalt, ct).ConfigureAwait(false))
        {
            return ScopeChange.Refused(
                state,
                $"Resuming requires an approved '{ScopeTransitions.ResumeFromHalt}' approval; '{resume.ApprovalId}' is not one.");
        }

        var target = resume.ToRung is { Length: > 0 } requested ? requested : state.Rung;
        var currentIndex = state.RungIndex;
        var targetIndex = state.RungIndexOf(target);
        if (targetIndex < 0)
            return ScopeChange.Refused(state, $"'{target}' is not a rung of this scope's ladder.");

        // A resume may only go DOWN. Coming back from a halt after an incident is a different act from
        // being granted more autonomy, and a resume that could climb would let a scope that just
        // misbehaved advance by being un-halted.
        if (currentIndex >= 0 && targetIndex > currentIndex)
        {
            return ScopeChange.Refused(
                state,
                $"A resume may not promote: '{target}' is above '{state.Rung}'. Use {ScopeTransitions.PromoteAutonomy}.");
        }

        var at = resume.At ?? clock.UtcNow;
        var resumed = state with
        {
            Halted = false,
            Rung = target,
            ResumeDiagnosis = resume.DiagnosisRef,
            ResumedAt = at,
        };
        await WriteAsync(resumed, ct).ConfigureAwait(false);
        await EmitAsync(scope, "core.scope.resumed", resumed, resume.DiagnosisRef, ct).ConfigureAwait(false);
        return new ScopeChange(true, resumed);
    }

    public async ValueTask<ScopeChange> PromoteAsync(string scope, ScopePromotion promote, CancellationToken ct)
    {
        var state = await GetAsync(scope, ct).ConfigureAwait(false);

        // Not halted: a halt is a latch, and climbing while latched would be a promotion by another route.
        if (state.Halted)
            return ScopeChange.Refused(state, $"Scope '{scope}' is halted; resume it before promoting.");

        if (promote.Unmet is { Count: > 0 } unmet)
            return ScopeChange.Refused(state, $"Promotion has unmet conditions: {string.Join("; ", unmet)}.");

        // A dimension with no ceiling blocks promotion. This is where A4 and A6 meet: autonomy that
        // cannot be bounded is not autonomy the host can grant.
        if (promote.BlockedLimit is { Length: > 0 } blocked)
            return ScopeChange.Refused(state, $"The limit '{blocked}' has no ceiling; a blocked limit refuses promotion.");

        if (!await ScopeApprovalCheck.IsApprovedAsync(approvals, promote.ApprovalId, ScopeTransitions.PromoteAutonomy, ct).ConfigureAwait(false))
        {
            return ScopeChange.Refused(
                state,
                $"Promotion requires an approved '{ScopeTransitions.PromoteAutonomy}' approval; '{promote.ApprovalId}' is not one.");
        }

        if (state.Next is not { } next)
            return ScopeChange.Refused(state, $"Scope '{scope}' is already on its top rung.");

        var promoted = state with { Rung = next, Promotions = state.Promotions + 1 };
        await WriteAsync(promoted, ct).ConfigureAwait(false);
        await EmitAsync(scope, "core.scope.promoted", promoted, next, ct).ConfigureAwait(false);
        return new ScopeChange(true, promoted);
    }

    private ValueTask<ResourceEnvelope<JsonObject>> WriteAsync(ScopeState state, CancellationToken ct) =>
        EnvelopeAppender.AppendAsync(
            store,
            Key(state.Scope),
            Type,
            // The validator reads nothing: this method already did the read, and re-reading inside a merge
            // would re-apply a decision made against a state that has since moved. The CAS is what makes
            // that safe — a concurrent writer's ETag mismatch retries the whole operation at the caller.
            _ => state,
            (desired, existing) => System.Text.Json.JsonSerializer.SerializeToNode(desired, Burbuja.Core.Abstractions.Json.JsonWire.Options)!.AsObject(),
            idempotent: null,
            ct);

    private async ValueTask EmitAsync(string scope, string type, ScopeState state, string? summary, CancellationToken ct)
    {
        await events.AppendAsync(
            new EventAppend(
                EventStreamIds.Resource($"{Namespace}/scopes/{Sanitize(scope)}"),
                null,
                type,
                summary ?? type,
                new JsonObject
                {
                    ["scope"] = scope,
                    ["rung"] = state.Rung,
                    ["halted"] = state.Halted,
                    ["haltReason"] = state.HaltReason,
                    ["haltEvidence"] = state.HaltEvidence is null ? null : new JsonArray(state.HaltEvidence.Select(e => (JsonNode)e!).ToArray()),
                    ["haltedBy"] = state.HaltedBy,
                    ["diagnosis"] = state.ResumeDiagnosis,
                    ["promotions"] = state.Promotions,
                },
                RenderLane.Monitor | RenderLane.Log,
                state.Rung,
                null,
                "core.scope"),
            ct).ConfigureAwait(false);
    }

    private static string Sanitize(string value)
    {
        var chars = value.Trim().Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray();
        var s = new string(chars).Trim('_');
        return s.Length == 0 ? "default" : s;
    }
}
