using Microsoft.Extensions.Configuration;

namespace Burbuja.Core.Options;

public sealed class EnvAliasConfigurationSource : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder) => new EnvAliasConfigurationProvider();
}

internal sealed class EnvAliasConfigurationProvider : ConfigurationProvider
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BURBUJA_ROLE"] = "Kernel:Role",
        ["BURBUJA_NODE_ID"] = "Kernel:NodeId",
        ["BURBUJA_PLUGIN_DIR"] = "Kernel:PluginsDirectory",
        ["BURBUJA_PLUGINS_ENABLED"] = "Kernel:EnabledPlugins",
        ["BURBUJA_RUN_MIGRATIONS"] = "Kernel:RunMigrations",
        ["BURBUJA_TEST_HOOKS"] = "Kernel:TestHooks",
        ["BURBUJA_LIVE"] = "Kernel:Live",
        ["BURBUJA_API_KEY"] = "Auth:ApiKeys:0:Key",
        ["BURBUJA_WORKER_CONCURRENCY"] = "Runtime:WorkerConcurrency",
        ["BURBUJA_POSTGRES"] = "Infrastructure:Postgres",
        ["BURBUJA_RABBITMQ"] = "Infrastructure:RabbitMq",
        ["BURBUJA_REDIS"] = "Infrastructure:Redis",
        ["BURBUJA_WORKTREE_ROOT"] = "Workspace:WorktreeRoot",
        ["BURBUJA_REPO_ROOT"] = "Workspace:RepoRoot",
        ["BURBUJA_SCRIPTED_SCENARIO_DIR"] = "Plugins:Llm.Scripted:ScenarioRoot",
        ["BURBUJA_LLM_PROVIDER"] = "Llm:ProviderId",
        ["BURBUJA_SUPERVISOR_PROVIDER"] = "Llm:ArbiterProviderId",
        ["BURBUJA_BEDROCK_MODEL_ID"] = "Plugins:Llm.Bedrock:Models:Worker",
        ["BURBUJA_APPROVAL_DEFER_AFTER"] = "Policy:DeferAfter",
        ["BURBUJA_APPROVAL_REUSE_WINDOW"] = "Policy:ApprovalReuseWindow",
        ["BURBUJA_MAX_TURNS"] = "Runtime:MaxTurns",
        ["BURBUJA_LEASE_TTL"] = "Runtime:LeaseTtl",
        ["BURBUJA_HEARTBEAT_INTERVAL"] = "Runtime:HeartbeatInterval",
        ["BURBUJA_NODE_TTL"] = "Runtime:NodeTtl",
        ["BURBUJA_RECOVERY_SWEEP_INTERVAL"] = "Runtime:RecoverySweepInterval",
        ["BURBUJA_OUTBOX_RELAY_INTERVAL"] = "Runtime:OutboxRelayInterval",
        ["BURBUJA_TOOL_TIMEOUT"] = "Runtime:ToolTimeout",
        ["BURBUJA_DAILY_SOFT_CAP_USD"] = "Budget:DailySoftCapUsd",
        ["BURBUJA_DAILY_HARD_STOP_USD"] = "Budget:DailyHardStopUsd",
        ["BURBUJA_LIVE_MAX_JOB_USD"] = "Budget:DefaultJobUsd",
        // M10 Q1: the workspace initializer's principal, settable by a container lane. It was in the self-hosted
        // fixture and in compose.yaml by its CONFIG name, and a container configures by environment variable —
        // so the first time the browser suite ran in the container, `initialize` failed with "No
        // 'Agents:LifecycleKey' is configured". The two ways of naming one setting are now the same way.
        ["BURBUJA_AGENTS_LIFECYCLE_KEY"] = "Agents:LifecycleKey",
        ["BURBUJA_WEBHOOK_HMAC_SECRET"] = "Plugins:Sink.Webhook:Endpoints:0:Secret",
        ["BURBUJA_S3_ENDPOINT"] = "Infrastructure:S3:Endpoint",
        ["BURBUJA_S3_ACCESS_KEY"] = "Infrastructure:S3:AccessKey",
        ["BURBUJA_S3_SECRET_KEY"] = "Infrastructure:S3:SecretKey",
        ["BURBUJA_S3_BUCKET"] = "Infrastructure:S3:Bucket",
        ["BURBUJA_S3_FORCE_PATH_STYLE"] = "Infrastructure:S3:ForcePathStyle",
    };

    public override void Load()
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (alias, key) in Aliases)
        {
            var value = Environment.GetEnvironmentVariable(alias);
            if (!string.IsNullOrEmpty(value))
                data[key] = value;
        }

        if (data.TryGetValue("Auth:ApiKeys:0:Key", out var apiKey) && !string.IsNullOrEmpty(apiKey))
        {
            data.TryAdd("Auth:ApiKeys:0:PrincipalId", "owner");
            data.TryAdd("Auth:ApiKeys:0:Roles:0", "Owner");
        }

        Data = data;
    }
}
