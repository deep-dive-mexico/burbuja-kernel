namespace Burbuja.Agents.Abstractions.Llm;

public sealed record ModelPricing(
    string ModelId,
    decimal InputPerMillionUsd,
    decimal OutputPerMillionUsd,
    decimal CacheWriteMultiplier,
    decimal CacheReadMultiplier,
    decimal AccountMultiplier);

public interface ICostCalculator
{
    ModelPricing? TryGet(string modelId);
    decimal Quote(string modelId, Usage usage);
}

/// <summary>
/// A provider-owned price book. Time-dependent books (peak/off-peak) take the instant; a book that
/// does not know a model returns false so the composite can fall through to another book.
/// </summary>
public interface IModelPricebook
{
    bool Knows(string modelId);
    decimal Quote(string modelId, Usage usage, DateTimeOffset at);
}
