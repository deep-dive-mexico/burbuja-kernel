using Burbuja.Core.Abstractions.Plugin;
using Burbuja.Core.Abstractions.Policy;
using Microsoft.Extensions.DependencyInjection;

namespace Burbuja.Plugins.Policy.Core;

public sealed class PolicyCorePlugin : IBurbujaPlugin
{
    public PluginManifest Manifest { get; } = new(
        "Policy.Core",
        "Policy Core",
        "1.0.0",
        PluginContracts.Version,
        "Burbuja.Plugins.Policy.Core.dll",
        typeof(PolicyCorePlugin).FullName!,
        PluginLoadKind.Dynamic,
        ["policy.rules"],
        ["policy.rules"],
        [],
        ["all"],
        "Plugins:Policy.Core",
        true,
        [],
        100);

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IPolicyRule<SegmentPolicySubject>, RuleGateEvasion>();
        services.AddSingleton<IPolicyRule<SegmentPolicySubject>, RulePi>();
        services.AddSingleton<IPolicyRule<SegmentPolicySubject>, RuleSecretPaths>();
        services.AddSingleton<IPolicyRule<SegmentPolicySubject>, RuleFsWrites>();
        services.AddSingleton<IPolicyRule<SegmentPolicySubject>, RuleFind>();
        services.AddSingleton<IPolicyRule<SegmentPolicySubject>, RuleGit>();
        services.AddSingleton<IPolicyRule<SegmentPolicySubject>, RuleAws>();
        services.AddSingleton<IPolicyRule<SegmentPolicySubject>, RuleAwsProfile>();
        services.AddSingleton<IPolicyRule<SegmentPolicySubject>, RuleGh>();
        services.AddSingleton<IPolicyRule<SegmentPolicySubject>, RuleHttp>();
        services.AddSingleton<IPolicyRule<SegmentPolicySubject>, RuleDocker>();
        services.AddSingleton<IPolicyRule<SegmentPolicySubject>, RuleInfra>();
        services.AddSingleton<IPolicyRule<SegmentPolicySubject>, RuleDb>();
        services.AddSingleton<IPolicyRule<SegmentPolicySubject>, RulePackages>();
        services.AddSingleton<IPolicyRule<SegmentPolicySubject>, RuleInterpreters>();
        services.AddSingleton<IPolicyRule<SegmentPolicySubject>, RuleLocalDestructive>();
        services.AddSingleton<IPolicyRule<SegmentPolicySubject>, RuleSsh>();
        services.AddSingleton<IPolicyRule<SegmentPolicySubject>, RuleEnvFileRead>();
        services.AddSingleton<IPolicyRule<SegmentPolicySubject>, RuleShellControl>();
        services.AddSingleton<IPolicyRule<SegmentPolicySubject>, RuleReadOnly>();
        services.AddSingleton<IPolicyRule<ShellLineSubject>, RuleExfiltration>();
        services.AddSingleton<IPolicyRule<ShellLineSubject>, RuleKernelInfrastructure>();

        // M4.3: agent-to-agent access is classified against the kernel-supplied adjacency facts.
        services.AddSingleton<IPolicyClassifier, AgentReadClassifier>();
        services.AddSingleton<IPolicyClassifier, AgentWriteClassifier>();

        // P1.5: the two integration surfaces, ported from nightshift's rules. Both follow the same
        // shape — reading is an allow, acting is gray, and an UNCLASSIFIED tool is gray rather than an
        // allow, because the tool list belongs to the extension or the server and "we have not heard of
        // it" is not evidence that it is harmless.
        services.AddSingleton<IPolicyClassifier, BrowserClassifier>();
        services.AddSingleton<IPolicyClassifier, MailClassifier>();
        // I3: the workspace, the third surface the same abstraction carries and the same rule again — read
        // is an allow, write is gray, and an unclassified tool of that surface is gray.
        services.AddSingleton<IPolicyClassifier, NotionClassifier>();

        // A5: a declared action request, judged against the transition it names. This is what gives
        // RiskTier.Clarify a producer -- until it existed, the tier A5/A7 added had nothing that reached it.
        services.AddSingleton<IPolicyClassifier, ActionRequestClassifier>();
    }
}
