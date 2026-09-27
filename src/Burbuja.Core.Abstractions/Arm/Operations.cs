using System.Text.Json.Nodes;

namespace Burbuja.Core.Abstractions.Arm;

public enum OperationStatus
{
    Running,
    Succeeded,
    Failed,
    Canceled,
}

public sealed record OperationResource(
    string Id,
    string Name,
    OperationStatus Status,
    DateTimeOffset StartTime,
    DateTimeOffset? EndTime,
    int? PercentComplete,
    ArmError? Error,
    string? TargetResourceId,
    JsonObject? Properties);

public interface IOperationTracker
{
    ValueTask StartAsync(string operationId, string subscriptionId, string ns, string location, string? targetResourceId, JsonObject? props, CancellationToken ct);
    ValueTask ReportAsync(string operationId, int percent, JsonObject? props, CancellationToken ct);
    ValueTask SucceedAsync(string operationId, JsonObject? result, CancellationToken ct);
    ValueTask FailAsync(string operationId, ArmError error, CancellationToken ct);
    ValueTask CancelAsync(string operationId, string reason, CancellationToken ct);
    ValueTask<OperationResource?> GetAsync(string operationId, CancellationToken ct);
    ValueTask<IReadOnlyList<OperationResource>> ListAsync(string? subscriptionId, string? ns, CancellationToken ct);
}
