using Burbuja.Agents.Abstractions.Llm;

namespace Burbuja.Plugins.Llm.DeepSeek;

/// <summary>DeepSeek V4.1 Flash list prices, USD per 1M tokens (2026-09-10).</summary>
public sealed record DeepSeekPricing(
    decimal CacheHitPeak = 0.006m,
    decimal CacheHitOffPeak = 0.003m,
    decimal CacheMissPeak = 0.30m,
    decimal CacheMissOffPeak = 0.15m,
    decimal OutputPeak = 1.20m,
    decimal OutputOffPeak = 0.60m,
    decimal AccountFactor = 1.10m);

/// <summary>
/// Peak is 01:00–04:00 and 06:00–10:00 UTC, Monday–Friday; everything else is off-peak (half
/// price). A cache write is billed at the cache-miss rate.
/// </summary>
public sealed class DeepSeekPricebook(DeepSeekPricing? pricing = null, TimeProvider? time = null) : IModelPricebook
{
    private readonly DeepSeekPricing _pricing = pricing ?? new DeepSeekPricing();
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public DeepSeekPricing Pricing => _pricing;

    public bool Knows(string modelId) =>
        modelId.Equals(DeepSeekLlmProvider.FlashModelId, StringComparison.OrdinalIgnoreCase);

    public bool IsPeak(DateTimeOffset at)
    {
        var utc = at.ToUniversalTime();
        if (utc.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            return false;
        var hour = utc.Hour;
        return (hour >= 1 && hour < 4) || (hour >= 6 && hour < 10);
    }

    public decimal Quote(string modelId, Usage usage, DateTimeOffset at)
    {
        if (!Knows(modelId))
            throw new ArgumentOutOfRangeException(nameof(modelId), $"No DeepSeek price for '{modelId}'.");

        var peak = IsPeak(at);
        var cacheHit = peak ? _pricing.CacheHitPeak : _pricing.CacheHitOffPeak;
        var cacheMiss = peak ? _pricing.CacheMissPeak : _pricing.CacheMissOffPeak;
        var output = peak ? _pricing.OutputPeak : _pricing.OutputOffPeak;

        var usd = (usage.CacheReadInputTokens / 1_000_000m) * cacheHit
                  + ((usage.InputTokens + usage.CacheWriteInputTokens) / 1_000_000m) * cacheMiss
                  + (usage.OutputTokens / 1_000_000m) * output;
        return usd * _pricing.AccountFactor;
    }

    public DateTimeOffset Now() => _time.GetUtcNow();
}
