using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Agents.Abstractions;
using Burbuja.Agents.Abstractions.Llm;
using Burbuja.Agents.Abstractions.Orchestration;
using Burbuja.Agents.Abstractions.Runtime;
using Burbuja.Core.Abstractions.Json;
using Burbuja.Core.Abstractions.Store;

namespace Burbuja.Plugins.Llm.Scripted;

public sealed class ScriptedLlmProvider(
    ICache cache,
    TimeProvider time,
    IEnumerable<IScriptedStructuredResponse> structuredResponses) : ILlmProvider, IStructuredLlm, IStructuredLlmWithUsage
{
    public ScriptedLlmProvider(ICache cache, TimeProvider time) : this(cache, time, []) { }

    private readonly IScriptedStructuredResponse[] _structuredResponses = [.. structuredResponses];
    private readonly Dictionary<string, ScenarioFile> _scenarios = LoadAll();

    public string Id => "scripted";

    public ModelCapabilities Capabilities(string modelId) =>
        new(false, "disabled", true, true, 1024, 200_000);

    public async ValueTask<LlmResponse> InvokeAsync(LlmRequest request, CancellationToken ct)
    {
        var ambient = AgentAmbient.Value;
        var scenarioId = ambient?.Scenario ?? "happy-path";
        if (!_scenarios.TryGetValue(scenarioId, out var scenario))
            throw new InvalidOperationException($"Unknown scripted scenario '{scenarioId}'.");

        var turnIndex = request.Messages.Count(m => m.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase));
        var (turns, scriptLabel) = await ResolveTurnsAsync(scenario, ambient, ct);
        var usedKey = $"scripted:{ambient?.JobId ?? "none"}:{ambient?.RunId ?? "none"}:used-turns";
        var used = (await cache.GetAsync<int[]>(usedKey, ct) ?? []).ToHashSet();
        var turn = MatchTurn(turns, request, turnIndex, used)
                   ?? throw new InvalidOperationException($"Scenario '{scenarioId}' has no matching turn and no 'any' fallback.");
        var turnOrdinal = IndexOf(turns, turn);
        if (turn.When?.Any != true)
        {
            used.Add(turnOrdinal);
            await cache.SetAsync(usedKey, used.ToArray(), TimeSpan.FromHours(1), ct);
        }

        MarkConsumed(scenarioId, scriptLabel, turnOrdinal);

        var key = $"scripted:{ambient?.JobId ?? "none"}:{turnIndex}:attempts";
        var attempts = await cache.GetAsync<int>(key, ct);
        attempts++;
        await cache.SetAsync(key, attempts, TimeSpan.FromHours(1), ct);

        var fault = turn.Fault;
        if (fault is not null)
            await ApplyFaultAsync(fault, attempts, ct);

        if (turn.DelayMs is > 0)
            await Task.Delay(TimeSpan.FromMilliseconds(turn.DelayMs.Value), time, ct);

        var usage = turn.Usage ?? scenario.DefaultUsage ?? new UsageDto();
        if (fault?.UsageBlowup is { } blow)
            usage = usage with { InputTokens = blow };

        var blocks = (turn.Emit.Blocks ?? []).Select(ParseBlock).ToArray();
        return new LlmResponse(ParseStop(turn.Emit.StopReason), blocks, new Usage(usage.InputTokens, usage.OutputTokens, usage.CacheReadInputTokens, usage.CacheWriteInputTokens), turn.Emit.StopReason);
    }

    public ValueTask<JsonNode?> CallAsync(StructuredLlmRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        foreach (var contribution in _structuredResponses)
        {
            if (contribution.TryRespond(request, out var response))
                return new(response);
        }

        var tool = request.Tool.Name;
        if (tool.Equals("emit_plan", StringComparison.OrdinalIgnoreCase))
        {
            var plan = new FanOutPlan(
                [
                    new PlanSubtask("a", "a", "write alpha.txt", ["alpha.txt"], [], 0.1m),
                    new PlanSubtask("b", "b", "write beta.txt", ["beta.txt"], [], 0.1m),
                    new PlanSubtask("c", "c", "rm -rf /", ["/"], [], 0.1m),
                ],
                "concat",
                [],
                0.8);
            return new(JsonSerializer.SerializeToNode(plan, JsonWire.Options));
        }

        if (tool.Equals("emit_spec", StringComparison.OrdinalIgnoreCase))
            return new((JsonNode?)null);

        var ambient = AgentAmbient.Value;
        var scenarioId = ambient?.Scenario ?? "happy-path";
        if (_scenarios.TryGetValue(scenarioId, out var scenario) && scenario.Supervisor?.Decisions is { Count: > 0 } decisions)
        {
            var hit = decisions.FirstOrDefault(d => d.When?.Any == true)
                      ?? decisions[0];
            return new(JsonSerializer.SerializeToNode(hit.Emit, JsonWire.Options));
        }

        return new(JsonSerializer.SerializeToNode(new
        {
            decision = "allow",
            reason = "scripted",
            confidence = 0.9,
            injection_suspected = false,
        }, JsonWire.Options));
    }

    /// <summary>
    /// K-1 — the same scripted answer, with the usage the fixture's own transcript vocabulary uses.
    ///
    /// <para><b>Why a constant and not a computed estimate.</b> This provider has no model and no bill: any
    /// number here is fixture data, and a number derived from string lengths would LOOK measured while being
    /// just as invented. The pair (100 in, 50 out) is the default <c>UsageDto</c> the agent transcripts in
    /// <c>tests/scenarios</c> already carry, so a caller reading tokens sees one convention on both the agent
    /// path and the structured path rather than two. The delegation keeps <see cref="CallAsync"/> — the
    /// method structured callers already use — byte-identical; this is the additive
    /// interface M14 R5c landed for exactly this shape.</para>
    /// </summary>
    public async ValueTask<(JsonNode? Content, StructuredLlmUsage? Usage)> CallWithUsageAsync(
        StructuredLlmRequest request, CancellationToken ct) =>
        (await CallAsync(request, ct).ConfigureAwait(false), new StructuredLlmUsage(100, 50));

    private static TurnDto? MatchTurn(IReadOnlyList<TurnDto> turns, LlmRequest request, int turnIndex, HashSet<int> used)    {
        TurnDto? any = null;
        for (var i = 0; i < turns.Count; i++)
        {
            var turn = turns[i];
            if (!Matches(turn.When, request, turnIndex))
                continue;
            if (turn.When?.Any == true)
            {
                any ??= turn;
                continue;
            }

            if (used.Contains(i))
                continue;
            return turn;
        }

        return any;
    }

    private static int IndexOf(IReadOnlyList<TurnDto> turns, TurnDto turn)
    {
        for (var i = 0; i < turns.Count; i++)
        {
            if (ReferenceEquals(turns[i], turn))
                return i;
        }

        return -1;
    }

    /// <summary>
    /// v2 first-call-order binding: the first call from a live (job, session) claims the next
    /// unbound script and remembers it. Running out of scripts is a loud failure, never a silent
    /// fallback to another script.
    /// </summary>
    private async ValueTask<(IReadOnlyList<TurnDto> Turns, string Label)> ResolveTurnsAsync(
        ScenarioFile scenario,
        AgentAmbient.State? ambient,
        CancellationToken ct)
    {
        var fallback = (IReadOnlyList<TurnDto>)(scenario.Agent?.Turns ?? []);
        if (!string.Equals(scenario.Bind, "firstCallOrder", StringComparison.OrdinalIgnoreCase) || scenario.Scripts.Count == 0)
            return (fallback, "default");

        var bindKey = $"scripted:bind:{ambient?.JobId ?? ambient?.RunId ?? "none"}";
        var label = await cache.GetAsync<string>(bindKey, ct);
        if (string.IsNullOrEmpty(label))
        {
            var boundKey = $"scripted:bound:{scenario.Id}";
            var bound = (await cache.GetAsync<string[]>(boundKey, ct) ?? []).ToHashSet(StringComparer.Ordinal);
            var next = scenario.Scripts.FirstOrDefault(s => !bound.Contains(s.Label))
                       ?? throw new InvalidOperationException($"Scenario '{scenario.Id}' has no unbound script left.");
            label = next.Label;
            bound.Add(label);
            await cache.SetAsync(boundKey, bound.ToArray(), TimeSpan.FromHours(1), ct);
            await cache.SetAsync(bindKey, label, TimeSpan.FromHours(1), ct);
        }

        var script = scenario.Scripts.FirstOrDefault(s => s.Label == label)
                     ?? throw new InvalidOperationException($"Scenario '{scenario.Id}' has no script '{label}'.");
        return (script.Turns, script.Label);
    }

    // Instance-scoped on purpose: a test host that asserts on one provider must not see the
    // turns a different host consumed in the same process.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentDictionary<int, byte>> _consumedTurns =
        new(StringComparer.Ordinal);

    private void MarkConsumed(string scenarioId, string label, int turnIndex)
    {
        if (turnIndex < 0)
            return;
        _consumedTurns.GetOrAdd($"{scenarioId}/{label}", _ => new System.Collections.Concurrent.ConcurrentDictionary<int, byte>())[turnIndex] = 0;
    }

    /// <summary>
    /// Throws when a required (non-`any`) turn inside a bound script was never consumed, so a
    /// scenario that silently stopped exercising a branch fails the test instead of passing.
    /// </summary>
    public void AssertConsumed()
    {
        var problems = new List<string>();
        foreach (var (id, scenario) in _scenarios)
        {
            var lists = new List<(string Label, IReadOnlyList<TurnDto> Turns)>();
            if (scenario.Agent?.Turns is { Count: > 0 } defaults)
                lists.Add(("default", defaults));
            foreach (var script in scenario.Scripts)
                lists.Add((script.Label, script.Turns));

            foreach (var (label, turns) in lists)
            {
                // Only a bound script can have unused turns.
                if (!_consumedTurns.TryGetValue($"{id}/{label}", out var consumed))
                    continue;
                for (var i = 0; i < turns.Count; i++)
                {
                    if (turns[i].When?.Any == true)
                        continue;
                    if (!consumed.ContainsKey(i))
                        problems.Add($"{id}/{label} turn {i} was never consumed");
                }
            }
        }

        if (problems.Count > 0)
            throw new InvalidOperationException("assert-consumed: " + string.Join("; ", problems));
    }

    private static bool Matches(WhenDto? when, LlmRequest request, int turnIndex)
    {
        if (when is null)
            return false;
        if (when.Any == true)
            return true;
        if (when.FirstTurn == true)
            return turnIndex == 0;
        if (when.TurnIndex is { } idx)
            return turnIndex == idx;
        if (when.LastUserMessage is { } um)
        {
            var last = request.Messages.LastOrDefault(m => m.Role.Equals("user", StringComparison.OrdinalIgnoreCase));
            var text = last is null ? "" : string.Join('\n', last.Content.OfType<TextBlock>().Select(t => t.Text)
                .Concat(last.Content.OfType<ToolResultBlock>().Select(r => r.Content.ToJsonString())));
            if (um.Contains is { } c && text.Contains(c, StringComparison.OrdinalIgnoreCase))
                return true;
            // Every needle must be present in the current user message, not elsewhere in the transcript.
            if (um.ContainsAll is { Count: > 0 } all
                && all.All(needle => !string.IsNullOrEmpty(needle)
                    && text.Contains(needle, StringComparison.OrdinalIgnoreCase)))
                return true;
            if (um.Matches is { } m && System.Text.RegularExpressions.Regex.IsMatch(text, m))
                return true;
            return false;
        }

        if (when.ToolResult is { } tr)
        {
            var last = request.Messages.LastOrDefault(m => m.Content.OfType<ToolResultBlock>().Any());
            if (last is null)
                return false;
            foreach (var block in last.Content.OfType<ToolResultBlock>())
            {
                var ok = true;
                if (tr.IsError is { } err && block.IsError != err)
                    ok = false;
                var text = block.Content.ToJsonString();
                if (tr.Contains is { } c && !text.Contains(c, StringComparison.OrdinalIgnoreCase))
                    ok = false;
                if (tr.ToolName is { } name)
                {
                    var use = request.Messages.SelectMany(m => m.Content.OfType<ToolUseBlock>()).LastOrDefault(u => u.Id == block.ToolUseId);
                    if (use is null || !use.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        ok = false;
                }

                if (ok)
                    return true;
            }

            return false;
        }

        if (when.TranscriptContains is { } needle)
        {
            var blob = string.Join('\n', request.Messages.SelectMany(m => m.Content.OfType<TextBlock>().Select(t => t.Text)));
            return blob.Contains(needle, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static async Task ApplyFaultAsync(FaultDto fault, int attempts, CancellationToken ct)
    {
        if (fault.Throttling is { } times && attempts <= times)
            throw new LlmThrottledException();
        if (fault.Hang is { } ms)
            await Task.Delay(TimeSpan.FromMilliseconds(ms), ct);
        if (fault.ApiError is { } status)
            throw new InvalidOperationException("apiError " + status);
        if (fault.InjectionEcho == true)
            throw new InvalidOperationException("injectionEcho");
        if (fault.MalformedOutput?.Shape is { Length: > 0 } shape && attempts == 1)
            throw new InvalidOperationException("malformed:" + shape);
    }

    private static LlmStopReason ParseStop(string? raw) => raw switch
    {
        "end_turn" => LlmStopReason.EndTurn,
        "tool_use" => LlmStopReason.ToolUse,
        "max_tokens" => LlmStopReason.MaxTokens,
        "stop_sequence" => LlmStopReason.StopSequence,
        _ => LlmStopReason.Unknown,
    };

    private static ContentBlock ParseBlock(JsonNode node)
    {
        var type = node["type"]?.GetValue<string>() ?? "text";
        return type switch
        {
            "tool_use" => new ToolUseBlock(
                node["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N"),
                node["name"]?.GetValue<string>() ?? "",
                node["input"]?.DeepClone() ?? new JsonObject()),
            "thinking" => new ThinkingBlock(
                node["thinking"]?.GetValue<string>() ?? "",
                node["signature"]?.GetValue<string>()),
            _ => new TextBlock(node["text"]?.GetValue<string>() ?? ""),
        };
    }

    private static Dictionary<string, ScenarioFile> LoadAll()
    {
        var dirs = new List<string>();
        var env = Environment.GetEnvironmentVariable("BURBUJA_SCRIPTED_SCENARIO_DIR");
        if (!string.IsNullOrWhiteSpace(env))
            dirs.Add(env);
        dirs.Add(Path.Combine(AppContext.BaseDirectory, "scenarios"));
        dirs.Add(Path.Combine(AppContext.BaseDirectory, "tests", "scenarios"));
        var root = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && root is not null; i++)
        {
            dirs.Add(Path.Combine(root, "tests", "scenarios"));
            root = Directory.GetParent(root)?.FullName;
        }

        var map = new Dictionary<string, ScenarioFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in dirs.Distinct(StringComparer.Ordinal))
        {
            if (!Directory.Exists(dir))
                continue;
            foreach (var file in Directory.GetFiles(dir, "*.json"))
            {
                var parsed = JsonSerializer.Deserialize<ScenarioFile>(File.ReadAllText(file), JsonWire.Options);
                if (parsed?.Id is { Length: > 0 } id)
                    map[id] = parsed;
            }
        }

        foreach (var scenario in map.Values)
        {
            var turns = (scenario.Agent?.Turns ?? []).Concat(scenario.Scripts.SelectMany(s => s.Turns)).ToList();
            if (turns.Count == 0 || turns.All(t => t.When?.Any != true))
                throw new InvalidOperationException($"Scenario '{scenario.Id}' is missing a mandatory 'any' matcher.");
        }

        return map;
    }

    private sealed class ScenarioFile
    {
        public string Id { get; set; } = "";
        public UsageDto? DefaultUsage { get; set; }
        public AgentDto? Agent { get; set; }
        public SupervisorDto? Supervisor { get; set; }

        /// <summary>v2: "firstCallOrder" binds each live (job, session) to the next unbound script.</summary>
        public string? Bind { get; set; }

        public List<ScriptDto> Scripts { get; set; } = [];
    }

    private sealed class ScriptDto
    {
        public string Label { get; set; } = "";
        public List<TurnDto> Turns { get; set; } = [];
    }

    private sealed class AgentDto
    {
        public List<TurnDto> Turns { get; set; } = [];
    }

    private sealed class TurnDto
    {
        public WhenDto? When { get; set; }
        public EmitDto Emit { get; set; } = new();
        public UsageDto? Usage { get; set; }
        public int? DelayMs { get; set; }
        public FaultDto? Fault { get; set; }
    }

    private sealed class WhenDto
    {
        public bool? FirstTurn { get; set; }
        public int? TurnIndex { get; set; }
        public bool? Any { get; set; }
        public MessageMatchDto? LastUserMessage { get; set; }
        public ToolResultMatchDto? ToolResult { get; set; }
        public string? TranscriptContains { get; set; }
    }

    private sealed class MessageMatchDto
    {
        public string? Contains { get; set; }

        /// <summary>K-R4 — every needle must appear in the last user message (AND, unlike <see cref="Contains"/>).</summary>
        public List<string>? ContainsAll { get; set; }

        public string? Matches { get; set; }
    }

    private sealed class ToolResultMatchDto
    {
        public string? ToolName { get; set; }
        public string? Contains { get; set; }
        public string? Matches { get; set; }
        public bool? IsError { get; set; }
    }

    private sealed class EmitDto
    {
        public string? StopReason { get; set; }
        public List<JsonNode>? Blocks { get; set; }
    }

    private sealed record UsageDto(int InputTokens = 100, int OutputTokens = 50, int CacheReadInputTokens = 0, int CacheWriteInputTokens = 0);

    private sealed class FaultDto
    {
        public int? Throttling { get; set; }
        public MalformedDto? MalformedOutput { get; set; }
        public bool? InjectionEcho { get; set; }
        public int? ApiError { get; set; }
        public int? Hang { get; set; }
        public int? UsageBlowup { get; set; }
    }

    private sealed class MalformedDto
    {
        public string? Shape { get; set; }
        public static implicit operator string?(MalformedDto? d) => d?.Shape;
    }

    private sealed class SupervisorDto
    {
        public List<SupervisorDecisionDto> Decisions { get; set; } = [];
    }

    private sealed class SupervisorDecisionDto
    {
        public SupervisorWhenDto? When { get; set; }
        public SupervisorEmitDto Emit { get; set; } = new();
    }

    private sealed class SupervisorWhenDto
    {
        public string? RiskClass { get; set; }
        public bool? Any { get; set; }
    }

    private sealed class SupervisorEmitDto
    {
        public string Decision { get; set; } = "allow";
        public string Reason { get; set; } = "ok";
        public double Confidence { get; set; } = 0.9;
        public bool InjectionSuspected { get; set; }
    }
}
