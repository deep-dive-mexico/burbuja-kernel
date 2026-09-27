using System.Text.Json.Nodes;

namespace Burbuja.Core.Abstractions.Outbox;

public sealed record OutboxMessage(Guid MessageId, Guid AggregateId, string Topic, JsonObject Payload);

public enum OutboxStatus
{
    Pending,
    InFlight,
    Sent,
    Dead,
}

public sealed record OutboxRecord(
    OutboxMessage Message,
    OutboxStatus Status,
    int Attempts,
    DateTimeOffset NextAttemptAt,
    string? DeadReason);

public interface IOutbox
{
    ValueTask EnqueueAsync(OutboxMessage message, CancellationToken ct);
}

public interface IOutboxStore
{
    ValueTask EnqueueAsync(OutboxMessage message, CancellationToken ct);
    ValueTask<IReadOnlyList<OutboxRecord>> ClaimAsync(int max, DateTimeOffset now, CancellationToken ct);
    ValueTask MarkSentAsync(Guid messageId, CancellationToken ct);
    ValueTask MarkDeadAsync(Guid messageId, string reason, CancellationToken ct);
    ValueTask RetryLaterAsync(Guid messageId, DateTimeOffset nextAttemptAt, CancellationToken ct);
    ValueTask<IReadOnlyList<OutboxRecord>> ListDeadAsync(CancellationToken ct);
}
