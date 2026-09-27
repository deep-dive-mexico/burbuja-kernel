using Burbuja.Agents.Abstractions.Tools;
using Burbuja.Core.Abstractions.Policy;

namespace Burbuja.Agents.Abstractions.Arbiter;

public enum ArbiterVerdict
{
    Allow,
    Deny,
    Escalate,
}

public sealed record ArbiterRequest(
    ToolCallSubject Subject,
    string Brief,
    string ClassifierClass,
    RiskTier ClassifierTier,
    string CommandPreview,
    int CallCount,
    decimal JobSpendUsd);

public sealed record ArbiterDecision(
    ArbiterVerdict Verdict,
    string Reason,
    double Confidence,
    bool InjectionSuspected,
    bool Overridden);

public interface IGrayZoneArbiter
{
    ValueTask<ArbiterDecision> DecideAsync(ArbiterRequest request, CancellationToken ct);
}

public interface IPersonaProvider
{
    string Sha { get; }
    string Text { get; }
}

public sealed record MentorConsult(string Question, string Context);

public sealed record MentorAnswer(string Answer, IReadOnlyList<string> ResidualRisks);

public interface IMentor
{
    ValueTask<MentorAnswer> ConsultAsync(MentorConsult consult, CancellationToken ct);
}
