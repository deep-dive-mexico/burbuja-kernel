using System.Text.Json;

namespace Burbuja.Core.Abstractions.Ingress;

public sealed record IngressEnvelope(
    string ChannelId,
    string ActorId,
    string IdempotencyKey,
    string Kind,
    JsonElement Payload);

public interface IIngressCodec
{
    string Kind { get; }
    ValueTask HandleAsync(IngressEnvelope envelope, CancellationToken ct);
}
