using System.Text.Json.Nodes;
using Burbuja.Agents.Abstractions;
using Burbuja.Agents.Abstractions.Budget;
using Burbuja.Agents.Abstractions.Llm;
using Burbuja.Agents.Runtime;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Store;
using Burbuja.Plugins.Llm.DeepSeek;
using Burbuja.Plugins.Llm.Scripted;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Burbuja.Agents.UnitTests;

/// <summary>
/// M5.1 slice C: cost is real for every model a contributed price book knows, a job's own model is
/// what is sent and quoted, an unpriced job is a 400 at PUT, and the ledger is durable over
/// <see cref="IResourceStore"/> with a read-only `ledgers` projection.
/// </summary>
public sealed class CostAndLedgerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string AgentsRoot = "/subscriptions/sub1/resourceGroups/rg1/providers/Burbuja.Agents";
    private const string LedgersRoot = AgentsRoot + "/ledgers";

    private static readonly DateTimeOffset OffPeak = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero); // Wednesday noon UTC
    private static readonly DateTimeOffset Peak = new(2026, 9, 9, 2, 0, 0, TimeSpan.Zero);    // Wednesday 02:00 UTC
    private static readonly DateOnly Day = new(2026, 9, 9);

    [Fact]
    public void Composite_keeps_sonnet_and_prices_a_contributed_book()
    {
        var time = new FakeTimeProvider(Peak);
        var book = new DeepSeekPricebook(time: time);
        var composite = new CompositeCostCalculator([book], time);

        // The default book is byte-identical: (3 + 15 + 3*1.25 + 3*0.1) * 1.10 = 24.255.
        composite.Quote(AgentsConstants.DefaultModelId, new Usage(1_000_000, 1_000_000, 1_000_000, 1_000_000)).ShouldBe(24.255m);
        composite.TryGet(AgentsConstants.DefaultModelId).ShouldNotBeNull();

        // The contributed book is asked at the current instant, so peak/off-peak is respected.
        var usage = new Usage(100, 50, 40, 30);
        composite.TryGet("deepseek-flash").ShouldNotBeNull();
        composite.Quote("deepseek-flash", usage)
            .ShouldBe(decimal.Round(book.Quote("deepseek-flash", usage, Peak), 6, MidpointRounding.AwayFromZero));
        time.Advance(OffPeak - Peak);
        composite.Quote("deepseek-flash", usage)
            .ShouldBe(decimal.Round(book.Quote("deepseek-flash", usage, OffPeak), 6, MidpointRounding.AwayFromZero));

        composite.TryGet("gpt-5").ShouldBeNull();
        Should.Throw<InvalidOperationException>(() => composite.Quote("gpt-5", usage))
            .Message.ShouldBe("Unpriced model 'gpt-5'.");

        // With no contributed book the DeepSeek id is exactly as unpriced as any other.
        var alone = new CompositeCostCalculator([], TimeProvider.System);
        alone.TryGet("deepseek-flash").ShouldBeNull();
        Should.Throw<InvalidOperationException>(() => alone.Quote("deepseek-flash", usage))
            .Message.ShouldBe("Unpriced model 'deepseek-flash'.");
    }

    [Fact]
    public void Host_resolves_a_calculator_that_prices_both_books()
    {
        var time = new FixedUtcTimeProvider(OffPeak);
        using var sp = Harness.CreateHost(extra: s =>
        {
            s.AddSingleton<TimeProvider>(time);
            s.AddSingleton<IModelPricebook>(new DeepSeekPricebook(time: time));
        });

        var costs = sp.GetRequiredService<ICostCalculator>();
        costs.Quote(AgentsConstants.DefaultModelId, new Usage(1_000_000, 1_000_000, 1_000_000, 1_000_000)).ShouldBe(24.255m);
        costs.TryGet("deepseek-flash").ShouldNotBeNull();
        // The DeepSeek book's own off-peak number for (1M in, 1M out).
        costs.Quote("deepseek-flash", new Usage(1_000_000, 1_000_000, 0, 0)).ShouldBe(0.825m);
    }

    [Fact]
    public async Task Unpriced_job_put_is_a_400_but_an_agent_put_is_not()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);

        var unpriced = await Harness.Send(sp, "PUT", Harness.JobPath, Harness.JobBody("happy-path", modelId: "deepseek-flash"));
        unpriced.StatusCode.ShouldBe(400);
        unpriced.Error!.Code.ShouldBe("InvalidRequestContent");
        unpriced.Error.Details.Count.ShouldBe(1);
        unpriced.Error.Details[0].Code.ShouldBe("UnpricedModel");
        unpriced.Error.Details[0].HttpStatus.ShouldBe(400);
        unpriced.Error.Details[0].Message.ShouldContain("deepseek-flash");

        // An agent defaults to deepseek-flash and this host cannot price it either; creating one is
        // still allowed, because unknown cost is not a bad request.
        (await Harness.Send(sp, "PUT", AgentsRoot + "/agents/a1", """{"properties":{"displayName":"a1"}}""")).Ok();

        // The default job model is the priced Sonnet one.
        (await Harness.Send(sp, "PUT", Harness.JobPath, Harness.JobBody("happy-path"))).StatusCode.ShouldBe(202);
    }

    [Fact]
    public async Task Priced_job_model_reaches_the_llm_and_is_booked_at_that_model()
    {
        var time = new FixedUtcTimeProvider(OffPeak);
        var book = new DeepSeekPricebook(time: time);
        var asked = new List<string>();
        using var sp = Harness.CreateHost(
            extra: s =>
            {
                s.AddSingleton<TimeProvider>(time);
                s.AddSingleton<IModelPricebook>(book);
                s.AddSingleton<ILlmProvider>(p => new RecordingLlmProvider(p.GetRequiredService<ScriptedLlmProvider>(), asked));
            },
            settings: new Dictionary<string, string?> { ["Agents:ProviderId"] = "recording" });

        await Harness.SeedAsync(sp);
        (await Harness.Send(sp, "PUT", Harness.JobPath, Harness.JobBody("happy-path", modelId: "deepseek-flash")))
            .StatusCode.ShouldBe(202);
        await Harness.RunJobAsync(sp, Harness.JobPath, Ct);

        var job = await Harness.Send(sp, "GET", Harness.JobPath);
        job.Ok();
        job.AsJson()["properties"]!["status"]!.GetValue<string>().ShouldBe("succeeded");
        asked.ShouldNotBeEmpty();
        asked.ShouldAllBe(model => model == "deepseek-flash");

        // Every booked turn was quoted at the job's model, and the ledger row carries that number.
        var perTurn = sp.GetRequiredService<ICostCalculator>().Quote("deepseek-flash", new Usage(100, 50, 0, 0));
        perTurn.ShouldBe(0.00005m);
        var workerUsd = job.AsJson()["properties"]!["cost"]!["workerUsd"]!.GetValue<decimal>();
        workerUsd.ShouldBe(perTurn * asked.Count);

        var ledger = sp.GetRequiredService<IBudgetLedger>();
        (await ledger.GetJobSpendAsync(EntityIds.Derive(Harness.JobPath), Ct)).ShouldBe(workerUsd);
        (await ledger.GetDailyAsync(Day, Ct)).SpentUsd.ShouldBe(workerUsd);
    }

    [Fact]
    public async Task Durable_ledger_reads_survive_a_new_instance_over_the_same_store()
    {
        using var sp = Harness.CreateHost();
        var store = sp.GetRequiredService<IResourceStore>();
        var time = new FakeTimeProvider(OffPeak);
        var jobA = Guid.NewGuid();
        var jobB = Guid.NewGuid();

        var first = new DurableBudgetLedger(store, time);
        await first.BookAsync(new BudgetBooking(jobA, Guid.NewGuid(), BudgetKind.Worker, 1.25m, 10, 5), Ct);
        await first.BookAsync(new BudgetBooking(jobA, Guid.NewGuid(), BudgetKind.Worker, 0.75m, 10, 5), Ct);
        await first.BookAsync(new BudgetBooking(jobB, Guid.NewGuid(), BudgetKind.Intake, 2m, 1, 1), Ct);

        // A new ledger over the same store is what a restart looks like at this level.
        var second = new DurableBudgetLedger(store, time);
        var daily = await second.GetDailyAsync(Day, Ct);
        daily.Day.ShouldBe(Day);
        daily.SpentUsd.ShouldBe(4m);
        daily.JobCount.ShouldBe(2);
        (await second.GetJobSpendAsync(jobA, Ct)).ShouldBe(2m);
        (await second.GetJobSpendAsync(jobB, Ct)).ShouldBe(2m);
        (await second.GetJobSpendAsync(Guid.NewGuid(), Ct)).ShouldBe(0m);
        (await second.GetDailyAsync(Day.AddDays(1), Ct)).SpentUsd.ShouldBe(0m);
    }

    [Fact]
    public async Task Concurrent_books_through_two_ledgers_lose_nothing()
    {
        using var sp = Harness.CreateHost();
        var store = sp.GetRequiredService<IResourceStore>();
        var time = new FakeTimeProvider(OffPeak);
        var left = new DurableBudgetLedger(store, time);
        var right = new DurableBudgetLedger(store, time);
        var jobA = Guid.NewGuid();
        var jobB = Guid.NewGuid();

        // Both ledgers race the very first booking of the day and of each job index.
        await Task.WhenAll(
            left.BookAsync(new BudgetBooking(jobA, Guid.NewGuid(), BudgetKind.Worker, 1m, 1, 1), Ct).AsTask(),
            right.BookAsync(new BudgetBooking(jobB, Guid.NewGuid(), BudgetKind.Worker, 2m, 1, 1), Ct).AsTask());

        var daily = await left.GetDailyAsync(Day, Ct);
        daily.SpentUsd.ShouldBe(3m);
        daily.JobCount.ShouldBe(2);
        (await left.GetJobSpendAsync(jobA, Ct)).ShouldBe(1m);
        (await right.GetJobSpendAsync(jobB, Ct)).ShouldBe(2m);

        // More contention on one key: eight appends split across both instances.
        var job = Guid.NewGuid();
        await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(i => (i % 2 == 0 ? left : right)
                .BookAsync(new BudgetBooking(job, Guid.NewGuid(), BudgetKind.Worker, 0.5m, 1, 1), Ct).AsTask())
            .ToArray());

        (await left.GetJobSpendAsync(job, Ct)).ShouldBe(4m);
        (await right.GetDailyAsync(Day, Ct)).SpentUsd.ShouldBe(7m);
    }

    [Fact]
    public async Task Ledgers_resource_projects_the_day_and_rejects_every_write()
    {
        using var sp = Harness.CreateHost();
        await Harness.SeedAsync(sp);
        var store = sp.GetRequiredService<IResourceStore>();
        var ledger = new DurableBudgetLedger(store, new FakeTimeProvider(OffPeak));
        await ledger.BookAsync(new BudgetBooking(Guid.NewGuid(), Guid.NewGuid(), BudgetKind.Worker, 1.5m, 10, 5), Ct);
        await ledger.BookAsync(new BudgetBooking(Guid.NewGuid(), Guid.NewGuid(), BudgetKind.Worker, 2.5m, 10, 5), Ct);

        var get = await Harness.Send(sp, "GET", LedgersRoot + "/2026-09-09");
        get.Ok();
        get.AsJson()["properties"]!["day"]!.GetValue<string>().ShouldBe("2026-09-09");
        get.AsJson()["properties"]!["spentUsd"]!.GetValue<decimal>().ShouldBe(4m);
        get.AsJson()["properties"]!["jobCount"]!.GetValue<int>().ShouldBe(2);

        (await Harness.Send(sp, "GET", LedgersRoot + "/2026-09-08")).StatusCode.ShouldBe(404);

        // Listing days must not leak the per-job index resources.
        var list = await Harness.Send(sp, "GET", LedgersRoot);
        list.Ok();
        var days = list.AsJson()["value"]!.AsArray();
        days.Count.ShouldBe(1);
        days[0]!["name"]!.GetValue<string>().ShouldBe("2026-09-09");

        (await Harness.Send(sp, "PUT", LedgersRoot + "/2026-09-09", """{"properties":{}}""")).StatusCode.ShouldBe(405);
        (await Harness.Send(sp, "PATCH", LedgersRoot + "/2026-09-09", """{"properties":{"spentUsd":0}}""")).StatusCode.ShouldBe(405);
        (await Harness.Send(sp, "DELETE", LedgersRoot + "/2026-09-09")).StatusCode.ShouldBe(405);
    }

    /// <summary>
    /// Pins <see cref="TimeProvider.GetUtcNow"/> so peak/off-peak pricing is deterministic, while
    /// timers (retry backoff, hosted-service intervals) still run on the real clock.
    /// </summary>
    private sealed class FixedUtcTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override long TimestampFrequency => TimeProvider.System.TimestampFrequency;

        public override long GetTimestamp() => TimeProvider.System.GetTimestamp();

        public override TimeZoneInfo LocalTimeZone => TimeProvider.System.LocalTimeZone;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            TimeProvider.System.CreateTimer(callback, state, dueTime, period);
    }

    /// <summary>Records the model of every turn while the scripted provider keeps serving them.</summary>
    private sealed class RecordingLlmProvider(ILlmProvider inner, List<string> models) : ILlmProvider, IStructuredLlm
    {
        public string Id => "recording";

        public ModelCapabilities Capabilities(string modelId) => inner.Capabilities(modelId);

        public ValueTask<LlmResponse> InvokeAsync(LlmRequest request, CancellationToken ct)
        {
            models.Add(request.ModelId);
            return inner.InvokeAsync(request, ct);
        }

        public ValueTask<JsonNode?> CallAsync(StructuredLlmRequest request, CancellationToken ct) =>
            ((IStructuredLlm)inner).CallAsync(request, ct);
    }
}
