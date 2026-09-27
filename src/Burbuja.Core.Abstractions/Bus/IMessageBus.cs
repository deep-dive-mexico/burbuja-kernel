using System.Text.Json.Nodes;

namespace Burbuja.Core.Abstractions.Bus;

public sealed record BusMessage(Guid MessageId, string Topic, JsonNode Payload, IReadOnlyDictionary<string, string> Headers);

public interface IMessageBus
{
    ValueTask PublishAsync(BusMessage message, CancellationToken ct);
    ValueTask<IAsyncDisposable> SubscribeAsync(string topic, Func<BusMessage, CancellationToken, ValueTask> handler, CancellationToken ct);
}
