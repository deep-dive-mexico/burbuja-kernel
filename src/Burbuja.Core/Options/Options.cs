using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace Burbuja.Core.Options;

public sealed class KernelOptions
{
    public const string Section = "Kernel";

    [Required]
    public string Role { get; set; } = "control-plane";

    public string NodeId { get; set; } = "";

    public string PluginsDirectory { get; set; } = "plugins";

    public string EnabledPlugins { get; set; } = "";

    public string[] TrustedPlugins { get; set; } = ["Policy.Core", "Supervisor", "Burbuja.Compute", "Llm.DeepSeek"];

    public bool RunMigrations { get; set; }

    public bool TestHooks { get; set; }

    public bool Live { get; set; }

    public bool Seed { get; set; }

    public string ContractsVersion { get; set; } = Burbuja.Core.Abstractions.Plugin.PluginContracts.Version;

    public string[] TrustedPaths { get; set; } =
    [
        "src/Burbuja.Core",
        "src/Burbuja.Core.Abstractions",
        "src/Burbuja.Core.Sdk",
        "src/Burbuja.Host",
        "src/Burbuja.Hosting",
        "plugins/",
        "config/",
        "secrets",
    ];

    public static string FallbackNodeId() =>
        Environment.GetEnvironmentVariable("HOSTNAME") is { Length: > 0 } hostname
            ? hostname
            : Environment.MachineName;
}

public sealed class AuthOptions
{
    public const string Section = "Auth";

    [ValidateEnumeratedItems]
    public List<ApiKeyEntry> ApiKeys { get; set; } = [];
}

public sealed class ApiKeyEntry
{
    [Required]
    public string Key { get; set; } = "";

    [Required]
    public string PrincipalId { get; set; } = "";

    public string PrincipalType { get; set; } = "User";

    public string[] Roles { get; set; } = [];

    public string[] Groups { get; set; } = [];
}

public sealed class PolicyOptions
{
    public const string Section = "Policy";

    public TimeSpan DeferAfter { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan ApprovalReuseWindow { get; set; } = TimeSpan.FromHours(4);

    public string[] ProtectedHosts { get; set; } = ["pi", "pi.local", "pi.example.test"];

    public string[] AllowedAwsProfiles { get; set; } = [];
}

public sealed class RuntimeOptions
{
    public const string Section = "Runtime";

    /// <summary>
    /// K2: how many recent ARM requests the activity ring holds. Bounded because an unbounded list is a
    /// memory leak with a friendly name, and configurable because the right size depends on how much a host
    /// is polled — a portal alone asks several times a second.
    /// </summary>
    public int ActivityRingSize { get; set; } = 1000;

    /// <summary>
    /// M7.0b: how many bus-delivered work items one subscriber may run at once. The in-memory bus
    /// delivers a topic on ONE serial pump, so before this the first item that parked on a human (or
    /// ran for minutes) stopped every later item on that topic. Declared in M4, unused until M7.0b.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int WorkerConcurrency { get; set; } = 4;

    /// <summary>
    /// M7.0b: how many orchestrated jobs the kernel runs at once. Lower than
    /// <see cref="WorkerConcurrency"/> on purpose — a job is a whole agent loop with tools, and the
    /// point of the lane is only that one job cannot head-of-line-block the rest.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int JobConcurrency { get; set; } = 2;

    /// <summary>M7.1: how often <c>DurableScheduleRunner</c> looks for due schedules.</summary>
    public TimeSpan ScheduleTickInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>M7.1d: how often the host collects and publishes its own facts.</summary>
    public TimeSpan HostFactsInterval { get; set; } = TimeSpan.FromSeconds(60);

    public TimeSpan LeaseTtl { get; set; } = TimeSpan.FromSeconds(45);

    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

    public TimeSpan NodeTtl { get; set; } = TimeSpan.FromSeconds(60);

    public TimeSpan RecoverySweepInterval { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan OutboxRelayInterval { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan ToolTimeout { get; set; } = TimeSpan.FromMinutes(10);

    public int MaxTurns { get; set; } = 80;

    /// <summary>N6: event payloads over this size spill to the artifact store. 0 disables spilling.</summary>
    [Range(0, int.MaxValue)]
    public int MaxEventPayloadBytes { get; set; } = Burbuja.Core.Events.EventPayloadCap.DefaultMaxBytes;
}

public sealed class ArmOptions
{
    public const string Section = "Arm";

    [ValidateObjectMembers]
    public ArmThrottleOptions Throttle { get; set; } = new();
}

public sealed class ArmThrottleOptions
{
    public const string Section = "Arm:Throttle";

    [Range(1, int.MaxValue)]
    public int ReadsPerMinute { get; set; } = 12000;

    [Range(1, int.MaxValue)]
    public int WritesPerMinute { get; set; } = 1200;

    [Range(1, int.MaxValue)]
    public int DeletesPerMinute { get; set; } = 1200;
}

public sealed class BudgetOptions
{
    public const string Section = "Budget";

    public decimal DailySoftCapUsd { get; set; }

    public decimal DailyHardStopUsd { get; set; }

    public decimal DefaultJobUsd { get; set; }
}

public sealed class LlmOptions
{
    public const string Section = "Llm";

    public string ProviderId { get; set; } = "";

    public string ArbiterProviderId { get; set; } = "";
}

public sealed class WorkspaceOptions
{
    public const string Section = "Workspace";

    public string WorktreeRoot { get; set; } = "";

    public string RepoRoot { get; set; } = "";
}
