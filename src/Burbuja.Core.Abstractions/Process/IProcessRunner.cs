namespace Burbuja.Core.Abstractions.Process;

public sealed record ProcessStartRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    TimeSpan Timeout,
    int MaxOutputBytes,
    bool RedactOutput = false);

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut);

public sealed class ExecutionGrant
{
    internal ExecutionGrant(string subjectId, string decisionKey, DateTimeOffset expiresAt)
    {
        SubjectId = subjectId;
        DecisionKey = decisionKey;
        ExpiresAt = expiresAt;
    }

    public string SubjectId { get; }
    public string DecisionKey { get; }
    public DateTimeOffset ExpiresAt { get; }
}

public interface IExecutionGrantAuthority
{
    ExecutionGrant Mint(string subjectId, string decisionKey, TimeSpan ttl);
}

public interface IProcessRunner
{
    ValueTask<ProcessResult> RunAsync(ProcessStartRequest request, ExecutionGrant grant, CancellationToken ct);
}
