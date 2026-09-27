using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Burbuja.Agents.Abstractions;
using Burbuja.Agents.Abstractions.Arbiter;
using Burbuja.Agents.Abstractions.Llm;
using Burbuja.Agents.Abstractions.Runtime;
using Burbuja.Core.Abstractions.Plugin;
using Burbuja.Core.Abstractions.Redaction;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Burbuja.Plugins.Supervisor;

public sealed class SupervisorPlugin : IBurbujaPlugin
{
    public PluginManifest Manifest { get; } = new(
        "Supervisor",
        "Supervisor",
        "1.0.0",
        PluginContracts.Version,
        "Burbuja.Plugins.Supervisor.dll",
        typeof(SupervisorPlugin).FullName!,
        PluginLoadKind.Static,
        ["agents.arbiter", "mentor"],
        ["agents.arbiter"],
        [],
        ["all", "worker"],
        null,
        true,
        [],
        310);

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IPersonaProvider, FilePersonaProvider>();
        services.AddSingleton<IGrayZoneArbiter, SupervisorArbiter>();
        services.AddSingleton<IMentor, SupervisorMentor>();
    }
}

public sealed class FilePersonaProvider : IPersonaProvider
{
    public FilePersonaProvider()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "persona.md");
        if (!File.Exists(path))
        {
            var asm = typeof(FilePersonaProvider).Assembly;
            var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("persona.md", StringComparison.OrdinalIgnoreCase));
            Text = name is null ? "# supervisor\n" : new StreamReader(asm.GetManifestResourceStream(name)!).ReadToEnd();
        }
        else
            Text = File.ReadAllText(path);
        Sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Text))).ToLowerInvariant();
    }

    public string Sha { get; }
    public string Text { get; }
}

public sealed class SupervisorArbiter(
    IEnumerable<ILlmProvider> providers,
    IPersonaProvider persona,
    IRedactor redactor,
    IConfiguration configuration) : IGrayZoneArbiter
{
    /// <summary>The model this arbiter asks, when the host names one (M10 A6b).</summary>
    public const string ModelSetting = "Supervisor:ModelId";

    /// <summary>
    /// Which provider serves the classifier, as <c>Llm:ArbiterProviderId</c> names it.
    ///
    /// <para><b>M10 A6b — the literal that made a live host's classifier dead.</b> This method used to send
    /// <see cref="AgentsConstants.DefaultModelId"/> (an Anthropic id) to whichever <c>IStructuredLlm</c> the
    /// container resolved last. On the E2E lane that is the scripted engine, which ignores the model id, so
    /// every fact passed. On a host with <c>Llm.DeepSeek</c> staged it is DeepSeek, which refuses an unknown
    /// model — so EVERY gray-zone decision threw. It failed toward the human, which is the right direction and
    /// still a dead classifier: a supervisor that escalates because it cannot speak is not supervising.</para>
    ///
    /// <para><b>Why the provider list and not <c>LlmProviderRouter</c>.</b> The router's <c>Structured</c>
    /// property is the obvious reach and it lives in <c>Burbuja.Agents</c>, which a plugin must not reference
    /// — the same boundary that makes this plugin loadable at all. The job the router does here is two lines
    /// (pick the named provider, or the last one installed), and both are done below rather than by taking on
    /// an assembly reference.</para>
    ///
    /// <para><b>The model, in order:</b> <c>Supervisor:ModelId</c> when the host names one, then
    /// <c>Llm:ArbiterModel</c>, then <c>Pomnibot:SupervisorModel</c> (the profile key the mac deployment
    /// already carries for exactly this classifier), and only then the agents' own default — which is a
    /// DeepSeek id and is right on a host whose agents run on DeepSeek.</para>
    /// </summary>
    private (IStructuredLlm? Model, string ModelId) Resolve()
    {
        var installed = providers.ToList();
        var wanted = configuration["Llm:ArbiterProviderId"];
        var provider = string.IsNullOrWhiteSpace(wanted)
            ? installed.LastOrDefault()
            : installed.FirstOrDefault(p => p.Id.Equals(wanted, StringComparison.OrdinalIgnoreCase));

        var modelId = configuration[ModelSetting];
        if (string.IsNullOrWhiteSpace(modelId))
            modelId = configuration["Llm:ArbiterModel"];
        if (string.IsNullOrWhiteSpace(modelId))
            modelId = configuration["Pomnibot:SupervisorModel"];
        if (string.IsNullOrWhiteSpace(modelId))
            modelId = AgentsConstants.DefaultAgentModelId;

        return (provider as IStructuredLlm, modelId!);
    }

    public async ValueTask<ArbiterDecision> DecideAsync(ArbiterRequest request, CancellationToken ct)
    {
        var input = redactor.Redact(request.Subject.Input.ToJsonString());
        var starved = $"{persona.Text}\nclassifier={request.ClassifierClass}/{request.ClassifierTier}\npreview={redactor.Redact(request.CommandPreview)}\n<untrusted>{input}</untrusted>";
        var (structured, modelId) = Resolve();
        if (structured is null)
            return FromScenario(request);
        var node = await structured.CallAsync(new StructuredLlmRequest(
            modelId,
            [new InputMessage("user", [new TextBlock(starved)])],
            persona.Text,
            new ToolSpec("decide", "decide", new JsonObject()),
            new ToolChoiceTool("decide"),
            256), ct);
        if (node is null)
            return new ArbiterDecision(ArbiterVerdict.Escalate, "malformed", 0, false, false);
        return Parse(node);
    }

    private static ArbiterDecision FromScenario(ArbiterRequest request)
    {
        var scenario = AgentAmbient.Value?.Scenario ?? "";
        if (scenario.Contains("escalate", StringComparison.OrdinalIgnoreCase))
            return new ArbiterDecision(ArbiterVerdict.Escalate, "scenario", 0.9, false, false);
        return new ArbiterDecision(ArbiterVerdict.Allow, "scenario", 0.9, false, false);
    }

    private static ArbiterDecision Parse(JsonNode node)
    {
        var raw = node["decision"]?.GetValue<string>() ?? node["verdict"]?.GetValue<string>() ?? "";
        if (!Enum.TryParse<ArbiterVerdict>(raw, true, out var verdict))
            return new ArbiterDecision(ArbiterVerdict.Escalate, "malformed", 0, false, false);
        var confidence = node["confidence"]?.GetValue<double>() ?? 0;
        var injection = node["injection_suspected"]?.GetValue<bool>() ?? false;
        return new ArbiterDecision(verdict, node["reason"]?.GetValue<string>() ?? "", confidence, injection, false);
    }
}

public sealed class SupervisorMentor : IMentor
{
    public ValueTask<MentorAnswer> ConsultAsync(MentorConsult consult, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return new(new MentorAnswer("continue", []));
    }
}
