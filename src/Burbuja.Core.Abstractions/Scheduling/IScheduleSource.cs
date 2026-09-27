using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Arm;

namespace Burbuja.Core.Abstractions.Scheduling;

/// <summary>
/// What a schedule resource says about itself. <paramref name="LastFiredAt"/> is the anchor the next
/// window is computed from; when it is null the caller falls back to the envelope's created time.
/// </summary>
public sealed record ScheduleDefinition(
    string Cron,
    TimeZoneInfo TimeZone,
    bool Paused,
    DateTimeOffset? LastFiredAt);

/// <summary>
/// A resource type whose instances are schedules. The kernel owns the clock, the cron arithmetic and
/// the de-duplication; a plugin owns what a fired schedule actually does.
///
/// <para>This is deliberately not an <c>IStreamRecoveryHandler</c>. Recovery is about a stream that
/// was interrupted mid-flight, so it is driven by a stored state; a schedule has no in-flight work —
/// after a <c>kill -9</c> the next tick simply recomputes the next window from the stored anchor.
/// Making it a sweeper instead of a recovery handler is what keeps "missed windows" a pure function
/// of (cron, lastFiredAt, now) rather than a replay.</para>
/// </summary>
public interface IScheduleSource
{
    /// <summary>Resource namespace, e.g. <c>Burbuja.Pomnibot</c>.</summary>
    string Namespace { get; }

    /// <summary>Resource type within the namespace, e.g. <c>schedules</c>.</summary>
    string Type { get; }

    /// <summary>
    /// Read the schedule out of an envelope, or <c>null</c> when this envelope is not sweepable (an
    /// unparseable cron, a disabled row). Returning null must never throw: the runner treats one bad
    /// schedule as one skipped schedule, not as a failed sweep.
    /// </summary>
    ScheduleDefinition? Read(ResourceEnvelope<JsonObject> envelope);

    /// <summary>
    /// Do the work for one window.
    ///
    /// <para><b>This must be idempotent for a given <paramref name="scheduledFor"/>.</b> The runner
    /// fires before it records, on purpose: a crash in between re-fires the same window on the next
    /// tick rather than silently losing it, and the way that stays honest is that firing twice for one
    /// window produces one piece of work — name the resource you create after the window (pomnibot
    /// uses <c>{slug}-{yyyyMMdd-HHmm}</c>) so the second write upserts the first.</para>
    /// </summary>
    ValueTask FireAsync(ResourceEnvelope<JsonObject> envelope, DateTimeOffset scheduledFor, CancellationToken ct);

    /// <summary>
    /// Record that the window was handled and when the next one is due. Returns <c>false</c> when the
    /// ETag CAS lost — another node fired this window first, which is not an error.
    /// </summary>
    ValueTask<bool> MarkFiredAsync(
        ResourceEnvelope<JsonObject> envelope,
        DateTimeOffset firedAt,
        DateTimeOffset? nextDue,
        CancellationToken ct);
}

/// <summary>Event types the durable runner writes on the <c>schedule:{id}</c> stream.</summary>
public static class ScheduleEvents
{
    public const string Fired = "schedule.fired";
    public const string SkippedETag = "schedule.skipped_etag";

    public static string StreamId(string scheduleKey) => "schedule:" + scheduleKey;
}
