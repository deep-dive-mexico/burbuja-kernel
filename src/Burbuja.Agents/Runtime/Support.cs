using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Burbuja.Agents.Abstractions;
using Burbuja.Agents.Abstractions.Budget;
using Burbuja.Agents.Abstractions.Domain;
using Burbuja.Agents.Abstractions.Llm;
using Burbuja.Core.Abstractions.Json;

namespace Burbuja.Agents.Runtime;

public static class SpecHash
{
    public static string Compute(JobSpec spec, IReadOnlyDictionary<string, string> tags)
    {
        var payload = new
        {
            spec,
            tags = tags.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => kv.Value),
        };
        var json = JsonSerializer.Serialize(payload, JsonWire.Options);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

/// <summary>
/// Stable ids for things that are not jobs but still need one for the budget ledger and the
/// arbiter: a stateful agent is keyed by its canonical id, a run by its run id.
/// </summary>
public static class EntityIds
{
    public static Guid Derive(string canonical)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToLowerInvariant()));
        return new Guid(bytes.AsSpan(0, 16));
    }
}

public static class DenyText
{
    public static string Format(string language, string riskClass)
    {
        if (language.StartsWith("es", StringComparison.OrdinalIgnoreCase))
            return $"Burbuja bloqueó esta acción (clase: {riskClass}). Es definitivo: no la reintentes ni busques otra forma de lograr el mismo efecto. Sigue con lo demás y déjalo bajo «Pendiente».";
        return $"Burbuja blocked this action (class: {riskClass}). It is final: do not retry it or find another way to achieve the same effect. Continue with the rest and leave it under \"Pending\".";
    }
}

public sealed class CancellationRegistry
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _tokens = new(StringComparer.OrdinalIgnoreCase);

    public CancellationToken TokenFor(string key)
    {
        var cts = _tokens.GetOrAdd(key, _ => new CancellationTokenSource());
        return cts.Token;
    }

    public void Cancel(string key)
    {
        var cts = _tokens.GetOrAdd(key, _ => new CancellationTokenSource());
        cts.Cancel();
    }

    public bool IsCanceled(string key) =>
        _tokens.TryGetValue(key, out var cts) && cts.IsCancellationRequested;

    public void Reset(string key) =>
        _tokens.AddOrUpdate(key, _ => new CancellationTokenSource(), (_, existing) =>
        {
            existing.Dispose();
            return new CancellationTokenSource();
        });
}

public sealed class SteeringInbox
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _inbox = new(StringComparer.OrdinalIgnoreCase);

    public void Push(string jobKey, string text) =>
        _inbox.GetOrAdd(jobKey, _ => new ConcurrentQueue<string>()).Enqueue(text);

    public IReadOnlyList<string> Drain(string jobKey)
    {
        if (!_inbox.TryGetValue(jobKey, out var q))
            return [];
        var items = new List<string>();
        while (q.TryDequeue(out var text))
            items.Add(text);
        return items;
    }
}

public sealed class CostCalculator : ICostCalculator
{
    public static readonly ModelPricing Sonnet45 = new(
        AgentsConstants.DefaultModelId,
        3m,
        15m,
        1.25m,
        0.1m,
        1.10m);

    public ModelPricing? TryGet(string modelId)
    {
        if (modelId.Equals(AgentsConstants.DefaultModelId, StringComparison.OrdinalIgnoreCase)
            || modelId.Contains("sonnet-4-5", StringComparison.OrdinalIgnoreCase)
            || modelId.Contains("sonnet-4.5", StringComparison.OrdinalIgnoreCase))
            return Sonnet45;
        return null;
    }

    public decimal Quote(string modelId, Usage usage)
    {
        var pricing = TryGet(modelId) ?? throw new InvalidOperationException($"Unpriced model '{modelId}'.");
        var input = usage.InputTokens / 1_000_000m * pricing.InputPerMillionUsd;
        var output = usage.OutputTokens / 1_000_000m * pricing.OutputPerMillionUsd;
        var cacheWrite = usage.CacheWriteInputTokens / 1_000_000m * pricing.InputPerMillionUsd * pricing.CacheWriteMultiplier;
        var cacheRead = usage.CacheReadInputTokens / 1_000_000m * pricing.InputPerMillionUsd * pricing.CacheReadMultiplier;
        return decimal.Round((input + output + cacheWrite + cacheRead) * pricing.AccountMultiplier, 6, MidpointRounding.AwayFromZero);
    }
}

/// <summary>
/// The calculator the runtime resolves (M5.1): the Sonnet default stays exactly as it was, and every
/// provider-owned price book is composed in after it, so a model is priced when any contributor
/// knows it. A price book only quotes, so its contribution is resolved into the effective
/// <see cref="ModelPricing"/> at the instant of the call.
/// </summary>
public sealed class CompositeCostCalculator(IEnumerable<IModelPricebook> pricebooks, TimeProvider time) : ICostCalculator
{
    private readonly CostCalculator _default = new();
    private readonly IReadOnlyList<IModelPricebook> _pricebooks = pricebooks as IReadOnlyList<IModelPricebook> ?? pricebooks.ToList();

    public ModelPricing? TryGet(string modelId) => Resolve(modelId);

    public decimal Quote(string modelId, Usage usage)
    {
        var pricing = Resolve(modelId) ?? throw new InvalidOperationException($"Unpriced model '{modelId}'.");
        var input = usage.InputTokens / 1_000_000m * pricing.InputPerMillionUsd;
        var output = usage.OutputTokens / 1_000_000m * pricing.OutputPerMillionUsd;
        var cacheWrite = usage.CacheWriteInputTokens / 1_000_000m * pricing.InputPerMillionUsd * pricing.CacheWriteMultiplier;
        var cacheRead = usage.CacheReadInputTokens / 1_000_000m * pricing.InputPerMillionUsd * pricing.CacheReadMultiplier;
        return decimal.Round((input + output + cacheWrite + cacheRead) * pricing.AccountMultiplier, 6, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// The default book wins the models it knows so Sonnet pricing never moves; otherwise the first
    /// contributing book that <c>Knows</c> the model. A time-dependent book (peak/off-peak) is
    /// resolved at one instant through four unit probes: the per-million rates come back directly,
    /// the write/read multipliers are relative to input, and any account factor is folded into the
    /// rates so the formula above reproduces that book's own quote.
    /// </summary>
    private ModelPricing? Resolve(string modelId)
    {
        if (_default.TryGet(modelId) is { } known)
            return known;
        foreach (var book in _pricebooks)
        {
            if (!book.Knows(modelId))
                continue;
            var at = time.GetUtcNow();
            var input = book.Quote(modelId, new Usage(1_000_000, 0, 0, 0), at);
            var output = book.Quote(modelId, new Usage(0, 1_000_000, 0, 0), at);
            var write = book.Quote(modelId, new Usage(0, 0, 0, 1_000_000), at);
            var read = book.Quote(modelId, new Usage(0, 0, 1_000_000, 0), at);
            return input == 0m
                ? new ModelPricing(modelId, 0m, output, 0m, 0m, 1m)
                : new ModelPricing(modelId, input, output, write / input, read / input, 1m);
        }

        return null;
    }
}

public sealed class InMemoryBudgetLedger : IBudgetLedger
{
    private readonly ConcurrentBag<BudgetBooking> _bookings = [];

    public ValueTask<Guid> BookAsync(BudgetBooking booking, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _bookings.Add(booking);
        // A booking id here is minted per call, exactly like the durable ledger's `LedgerEntry.Id`, so a
        // turn event cites something stable in either implementation (M9 T1).
        return new(Guid.NewGuid());
    }

    public ValueTask<DailySpend> GetDailyAsync(DateOnly day, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var usd = _bookings.Sum(b => b.Usd);
        return new(new DailySpend(day, usd, _bookings.Select(b => b.JobId).Distinct().Count()));
    }

    public ValueTask<decimal> GetJobSpendAsync(Guid jobId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return new(_bookings.Where(b => b.JobId == jobId).Sum(b => b.Usd));
    }
}
