using System.Globalization;
using Burbuja.Core.Abstractions.Events;

namespace Burbuja.Portal.Services;

/// <summary>
/// M9 U3 — the metrics fold: what a run cost in TIME and TOKENS, computed from the events the page already
/// holds.
///
/// <para><b>Every number here comes off an event, not off the spacing between events.</b> Until T1 the stream
/// recorded what happened and never how long it took, so a "duration" could only be the gap between two row
/// timestamps — a property of when rows were written. `llmMs` is measured around the provider call and
/// `durationMs` around the tool call, and this fold adds them up.</para>
///
/// <para><b>A missing measure is null, never zero.</b> A run whose turns predate T1 has no `llmMs`; reporting
/// 0 ms would say the model answered instantly, which is the opposite of "we do not know". The page renders
/// "—" for a missing measure and names how many turns carried one, so an old run cannot look fast.</para>
/// </summary>
public sealed record TrajectoryStats(
    int Turns,
    int Steps,
    int TurnsMeasured,
    int StepsMeasured,
    long LlmMs,
    long ToolMs,
    int InputTokens,
    int OutputTokens,
    int CacheReadTokens,
    int CacheWriteTokens,
    decimal BookedUsd)
{
    public static readonly TrajectoryStats Empty = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0m);

    /// <summary>
    /// Cache reads as a share of everything the model was fed — the number dsh's stats line calls "cache
    /// hit". Null when nothing was read at all, because 0% and "no data" are different claims.
    /// </summary>
    public double? CacheHitPercent =>
        InputTokens + CacheReadTokens == 0
            ? null
            : 100d * CacheReadTokens / (InputTokens + CacheReadTokens);

    /// <summary>Output tokens per second of model time, over the turns that reported a duration.</summary>
    public double? TokensPerSecond =>
        LlmMs <= 0 ? null : 1000d * OutputTokens / LlmMs;

    public string LlmTime => TurnsMeasured == 0 ? "—" : Duration(LlmMs);

    public string ToolTime => StepsMeasured == 0 ? "—" : Duration(ToolMs);

    public string Tokens => $"{InputTokens:N0} in · {OutputTokens:N0} out";

    public string CacheHit => CacheHitPercent is { } pct ? pct.ToString("0.0", CultureInfo.InvariantCulture) + "%" : "—";

    public string Tps => TokensPerSecond is { } tps ? tps.ToString("0.0", CultureInfo.InvariantCulture) : "—";

    /// <summary>
    /// Whether every turn and step carried a measure. A page that showed a total without this would let a run
    /// whose events predate T1 read as a run that took no time.
    /// </summary>
    public bool FullyMeasured => Turns > 0 && TurnsMeasured == Turns && StepsMeasured == Steps;

    /// <summary>
    /// A MEASURED zero is "0 ms", not "—": the two are different claims and the scripted provider really does
    /// answer in under a millisecond. The first version of this folded `ms &lt;= 0` into "—", which is how the
    /// G8 screenshot came to show `— tool time` next to a footer saying every tool call was measured.
    /// </summary>
    private static string Duration(long ms) =>
        ms < 1000
            ? ms.ToString(CultureInfo.InvariantCulture) + " ms"
            : (ms / 1000d).ToString("0.0", CultureInfo.InvariantCulture) + " s";
}

public static class TrajectoryStatsBuilder
{
    public const string TurnCompleted = "agents.turn.completed";
    public const string ToolResult = "agents.tool.result";

    /// <summary>
    /// Folds the event range. The counters are the events' own fields — `payload.usage`, `payload.llmMs`,
    /// `payload.durationMs`, and the booking `value` a ledger event carries — so the page cannot hold a second
    /// model of what the kernel wrote.
    /// </summary>
    public static TrajectoryStats Fold(IEnumerable<LedgerEvent> events)
    {
        var turns = 0;
        var steps = 0;
        var turnsMeasured = 0;
        var stepsMeasured = 0;
        long llmMs = 0;
        long toolMs = 0;
        var input = 0;
        var output = 0;
        var cacheRead = 0;
        var cacheWrite = 0;
        decimal booked = 0m;

        foreach (var evt in events)
        {
            if (evt.Value is { } value)
                booked += value;

            if (evt.Type.Equals(TurnCompleted, StringComparison.OrdinalIgnoreCase))
            {
                turns++;
                if (Int(evt.Payload?["llmMs"]) is { } ms)
                {
                    llmMs += ms;
                    turnsMeasured++;
                }

                var usage = evt.Payload?["usage"];
                input += Int(usage?["input"]) ?? 0;
                output += Int(usage?["output"]) ?? 0;
                cacheRead += Int(usage?["cacheRead"]) ?? 0;
                cacheWrite += Int(usage?["cacheWrite"]) ?? 0;
            }
            else if (evt.Type.Equals(ToolResult, StringComparison.OrdinalIgnoreCase))
            {
                steps++;
                if (Int(evt.Payload?["durationMs"]) is { } ms)
                {
                    toolMs += ms;
                    stepsMeasured++;
                }
            }
        }

        return new TrajectoryStats(
            turns, steps, turnsMeasured, stepsMeasured, llmMs, toolMs,
            input, output, cacheRead, cacheWrite, booked);
    }

    private static int? Int(System.Text.Json.Nodes.JsonNode? node)
    {
        if (node is null)
            return null;
        try
        {
            return node.GetValue<int>();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
