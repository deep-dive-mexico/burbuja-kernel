using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Process;

namespace Burbuja.Agents.Abstractions.Tools;

public sealed record ToolDescriptor(
    string Name,
    string Description,
    JsonNode InputSchema,
    bool ParallelSafe,
    bool MainRunOnly,
    bool Idempotent,
    string? RiskHint,
    IReadOnlyList<string> WriteTargetKeys);

public sealed record ToolExecutionContext(
    string WorkRoot,
    string StreamId,
    string RunId,
    string JobId,
    TimeSpan Timeout,
    IReadOnlyDictionary<string, string> EnvironmentPolicy);

public sealed class GatePass
{
    internal GatePass(string toolUseId, string toolName, string decisionKey, ExecutionGrant grant)
    {
        ToolUseId = toolUseId;
        ToolName = toolName;
        DecisionKey = decisionKey;
        Grant = grant;
    }

    public string ToolUseId { get; }
    public string ToolName { get; }
    public string DecisionKey { get; }
    public ExecutionGrant Grant { get; }
}

public sealed record ToolResult(JsonNode Content, bool IsError);

public interface IAgentTool
{
    ToolDescriptor Descriptor { get; }
    ValueTask<ToolResult> ExecuteAsync(ToolExecutionContext context, JsonNode input, GatePass pass, CancellationToken ct);
}

public sealed record ToolCallSubject(
    string ToolName,
    JsonNode Input,
    string? Cwd,
    string RunId,
    string JobId,
    bool IsSubRun);
