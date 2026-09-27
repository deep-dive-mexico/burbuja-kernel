using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Agents.Abstractions;
using Burbuja.Agents.Abstractions.Domain;
using Burbuja.Agents.Abstractions.Llm;
using Burbuja.Agents.Abstractions.Orchestration;
using Burbuja.Agents.Abstractions.Registry;
using Burbuja.Core.Abstractions.Json;
using Microsoft.Extensions.Logging;

namespace Burbuja.Agents.Runtime;

public sealed class RunGroupJoiner
{
    private sealed class Group
    {
        public int Expected;
        public int Completed;
        public int MergerStarted;
        public Func<CancellationToken, ValueTask>? Merger;
    }

    private readonly ConcurrentDictionary<Guid, Group> _groups = new();

    public void Register(Guid groupId, int expected, Func<CancellationToken, ValueTask> merger) =>
        _groups[groupId] = new Group { Expected = expected, Merger = merger };

    public async ValueTask SignalAsync(Guid groupId, CancellationToken ct)
    {
        if (!_groups.TryGetValue(groupId, out var group))
            return;
        var completed = Interlocked.Increment(ref group.Completed);
        if (completed < group.Expected)
            return;
        if (Interlocked.CompareExchange(ref group.MergerStarted, 1, 0) != 0)
            return;
        if (group.Merger is not null)
            await group.Merger(ct);
    }
}

public sealed class JobOrchestrator(StrategyRegistry strategies, JobStateStore states)
{
    public async Task StartAsync(string jobKey, CancellationToken ct)
    {
        var job = await states.GetAsync(jobKey, ct);
        if (job is null)
            return;
        var scenario = job.Tags.TryGetValue(AgentsConstants.ScenarioTag, out var tag) ? tag : "";
        var id = StrategyIds.Normalize(job.Properties.Spec.Strategy, scenario);
        if (!strategies.TryGet(id, out var strategy))
        {
            // An unknown id is rejected at PUT time; reaching here means a job written by an older
            // kernel. Fall back loudly to the single-worker strategy rather than string-switching.
            if (!strategies.TryGet(StrategyIds.SingleWorker, out strategy))
                return;
        }

        await strategy.StartAsync(new OrchestrationContext(job.Id, jobKey, id, job.Properties.Spec), ct);
    }
}

public sealed class HeuristicIntakeStrategy(IRepoRegistry? repos) : IIntakeStrategy
{
    public ValueTask<JobSpec?> TryParseAsync(string rawRequest, IReadOnlyList<string> candidateRepoIds, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var repoId = candidateRepoIds.FirstOrDefault(id => rawRequest.Contains(id, StringComparison.OrdinalIgnoreCase));
        if (repoId is null)
            return new((JobSpec?)null);
        if (repos?.Get(repoId) is null && repos is not null)
            return new((JobSpec?)null);
        return new(new JobSpec
        {
            Kind = JobKind.Implement,
            RepoId = repoId,
            TargetEnv = TargetEnv.Dev,
            Title = rawRequest.Length > 40 ? rawRequest[..40] : rawRequest,
            Slug = "intake",
            Brief = rawRequest,
            Language = rawRequest.Contains("ñ", StringComparison.OrdinalIgnoreCase) ? "es" : "en",
            Confidence = 0.5,
            AutoAccept = true,
        });
    }
}

public sealed class IntakeService(LlmProviderRouter llmRouter, IIntakeStrategy fallback, IRepoRegistry? repos)
{
    public async ValueTask<JobSpec?> ParseAsync(string raw, IReadOnlyList<string> candidates, CancellationToken ct)
    {
        if (llmRouter.Structured is { } structured && structured is not null)
        {
            try
            {
                var node = await structured.CallAsync(new StructuredLlmRequest(
                    AgentsConstants.DefaultModelId,
                    [new InputMessage("user", [new TextBlock(raw)])],
                    "emit_spec",
                    new ToolSpec("emit_spec", "job spec", new JsonObject()),
                    new ToolChoiceTool("emit_spec"),
                    1024), ct);
                var spec = node is null ? null : JsonSerializer.Deserialize<JobSpec>(node, JsonWire.Options);
                if (spec is not null)
                {
                    if (spec.RepoId is { } id && repos is not null && repos.Get(id) is null)
                        return await fallback.TryParseAsync(raw, candidates, ct);
                    return spec;
                }
            }
            catch
            {
                // fall through to heuristics
            }
        }

        return await fallback.TryParseAsync(raw, candidates, ct);
    }
}

public sealed class JobRecoveryHandler : Burbuja.Core.Abstractions.Recovery.IStreamRecoveryHandler
{
    public string StreamType => AgentsConstants.StreamType;

    public ValueTask RecoverAsync(string streamId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return default;
    }
}

public sealed class AgentExecutor(
    Burbuja.Core.Abstractions.Bus.IMessageBus bus,
    JobOrchestrator orchestrator,
    Microsoft.Extensions.Configuration.IConfiguration configuration,
    Microsoft.Extensions.Logging.ILogger<AgentExecutor> logger) : Microsoft.Extensions.Hosting.IHostedService
{
    private IAsyncDisposable? _sub;
    private Burbuja.Core.Bus.KeyedWorkDispatcher<string>? _dispatcher;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var role = configuration["Kernel:Role"] ?? "all";
        if (!role.Equals("worker", StringComparison.OrdinalIgnoreCase)
            && !role.Equals("all", StringComparison.OrdinalIgnoreCase))
            return;

        // M7.0b: the strategy runs the job to completion, so leaving it inline on the bus's serial
        // pump blocked every later job on `agents.run` for its whole lifetime. One lane per job;
        // several jobs at once.
        _dispatcher = new Burbuja.Core.Bus.KeyedWorkDispatcher<string>(
            jobKey => jobKey,
            (jobKey, _, ct) => new ValueTask(orchestrator.StartAsync(jobKey, ct)),
            concurrency: int.TryParse(configuration["Runtime:JobConcurrency"], out var n) && n > 0 ? n : 2,
            onFault: (ex, jobKey) => logger.LogError(ex, "orchestration for job '{JobKey}' failed", jobKey));

        _sub = await bus.SubscribeAsync(AgentsConstants.TopicRun, OnMessage, cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_sub is not null)
            await _sub.DisposeAsync();
        if (_dispatcher is not null)
            await _dispatcher.DisposeAsync(TimeSpan.FromSeconds(20));
    }

    internal ValueTask OnMessage(Burbuja.Core.Abstractions.Bus.BusMessage message, CancellationToken ct)
    {
        var payload = message.Payload as JsonObject ?? JsonSerializer.Deserialize<JsonObject>(message.Payload);
        var jobKey = payload?["jobKey"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(jobKey))
            return default;
        _dispatcher?.TryPost(jobKey);
        return default;
    }
}
