using Burbuja.Agents.Abstractions.Orchestration;

namespace Burbuja.Agents.Abstractions.Runtime;

public static class AgentAmbient
{
    private static readonly AsyncLocal<State?> Current = new();

    public static State? Value
    {
        get => Current.Value;
        set => Current.Value = value;
    }

    public sealed record State(
        string JobKey,
        string JobId,
        string RunId,
        string Scenario,
        AgentRunRole Role,
        string Brief,
        string Language,
        IReadOnlyDictionary<string, string> Tags,
        string? AgentId = null,
        string? SessionId = null,
        string? TeamId = null,
        string? Label = null,
        int Depth = 0);
}
