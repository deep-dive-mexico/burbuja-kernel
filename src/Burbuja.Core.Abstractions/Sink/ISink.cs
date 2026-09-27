using System.Text.Json.Nodes;

namespace Burbuja.Core.Abstractions.Sink;

public interface ISink
{
    string Id { get; }
    ValueTask DeliverAsync(JsonObject payload, Guid messageId, CancellationToken ct);
}
