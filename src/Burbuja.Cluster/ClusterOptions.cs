using Burbuja.Cluster.Abstractions;

namespace Burbuja.Cluster;

/// <summary>
/// Configuration for the cluster module. Off by default: a host that never sets <c>Cluster:Enabled</c> gets no
/// listener, no background work and no files.
/// </summary>
public sealed class ClusterOptions
{
    public const string Section = ClusterConstants.Section;

    public bool Enabled { get; set; }

    /// <summary><c>controller</c>, <c>agent</c> or <c>all</c> (one process that is both).</summary>
    public string Role { get; set; } = "all";

    /// <summary>Stable id of this node. Defaults to the machine name, lower-cased.</summary>
    public string? NodeId { get; set; }
    public string? NodeName { get; set; }

    /// <summary>
    /// Absolute directory for the cluster's own files (membership, credentials, workload logs). Created with
    /// owner-only access. Empty keeps everything in memory, which is only suitable for tests.
    /// </summary>
    public string StateDirectory { get; set; } = "";

    /// <summary>The scheme and port this process listens on, so networks can advertise addresses for it.</summary>
    public string Scheme { get; set; } = "http";
    public int Port { get; set; } = 18520;

    /// <summary>
    /// Addresses the standalone node host binds, e.g. <c>http://127.0.0.1:18520</c> plus the machine's overlay
    /// address. Empty binds the loopback only, which is all an agent needs because nothing connects to it. A
    /// host composed some other way binds its own addresses and ignores this.
    /// </summary>
    public List<string> Listen { get; set; } = [];

    public ControllerOptions Controller { get; set; } = new();
    public AgentOptions Agent { get; set; } = new();

    /// <summary>Addresses the <c>static</c> network advertises for this node, for networks without a provider.</summary>
    public List<string> Advertise { get; set; } = [];

    public bool IsController => Enabled && Role is "controller" or "all";
    public bool IsAgent => Enabled && Role is "agent" or "all";

    public string ResolvedNodeId => (NodeId is { Length: > 0 } id ? id : Environment.MachineName).ToLowerInvariant();

    public void Validate()
    {
        if (!Enabled) return;
        var errors = new List<string>();
        if (Role is not ("controller" or "agent" or "all")) errors.Add("Cluster:Role must be controller, agent or all");
        if (StateDirectory.Length > 0 && !Path.IsPathFullyQualified(StateDirectory)) errors.Add("Cluster:StateDirectory must be absolute");
        if (Port is < 1 or > 65535) errors.Add("Cluster:Port is out of range");
        if (ResolvedNodeId.Length is 0 or > 63 || ResolvedNodeId.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_')))
            errors.Add("Cluster:NodeId must be 1-63 letters, digits, '-', '.' or '_'");
        if (Role == "agent" && Agent.Controllers.Count == 0) errors.Add("Cluster:Agent:Controllers needs at least one address");
        Controller.Validate(errors);
        Agent.Validate(errors);
        if (errors.Count > 0) throw new InvalidOperationException("Invalid cluster configuration: " + string.Join("; ", errors));
    }
}

public sealed class ControllerOptions
{
    /// <summary>File holding the operator bearer token (at least 32 characters). Generated in the state directory when empty.</summary>
    public string OperatorTokenFile { get; set; } = "";

    public double SyncIntervalSeconds { get; set; } = 5;

    /// <summary>A node that has not synced for this long is unreachable and its work is reported as unknown.</summary>
    public double NodeTtlSeconds { get; set; } = 20;

    /// <summary>Older readings are "cannot see", which batch work treats as a refusal.</summary>
    public double ObservationMaxAgeSeconds { get; set; } = 90;

    /// <summary>How long work holding an exclusive key runs without contact before its node stops it.</summary>
    public double ExclusiveTtlSeconds { get; set; } = 30;

    /// <summary>How long a node must be gone before work that may move is placed elsewhere.</summary>
    public double NodeLossGraceSeconds { get; set; } = 90;

    /// <summary>Consecutive pressed readings before a node counts as under pressure, and clear ones before it recovers.</summary>
    public int PressureBadSamples { get; set; } = 3;
    public int PressureGoodSamples { get; set; } = 6;

    public int JoinTokenMinutes { get; set; } = 30;

    internal void Validate(List<string> errors)
    {
        if (SyncIntervalSeconds < 0.05) errors.Add("Cluster:Controller:SyncIntervalSeconds must be at least 0.05");
        if (NodeTtlSeconds < SyncIntervalSeconds * 2) errors.Add("Cluster:Controller:NodeTtlSeconds must be at least twice the sync interval");
        if (ExclusiveTtlSeconds < SyncIntervalSeconds * 2) errors.Add("Cluster:Controller:ExclusiveTtlSeconds must be at least twice the sync interval");
        if (PressureBadSamples < 1 || PressureGoodSamples < 1) errors.Add("Cluster:Controller pressure sample counts must be positive");
    }
}

public sealed class AgentOptions
{
    /// <summary>Addresses of the controller, any order; the agent prefers the nearest one that answers.</summary>
    public List<string> Controllers { get; set; } = [];

    /// <summary>File holding a one-time join token. Only read while this node has no credential yet.</summary>
    public string JoinTokenFile { get; set; } = "";

    public Dictionary<string, string> Labels { get; set; } = new();
    public List<string> Capabilities { get; set; } = [];

    /// <summary>
    /// What this node lends the cluster. For a measured resource (<c>cpu</c>, <c>memory-mb</c>, <c>gpu</c>) the
    /// value is a ceiling on what was detected; any other name is a resource the operator declares, such as a
    /// measured cap on how many of some workload the machine can carry.
    /// </summary>
    public Dictionary<string, decimal> Lend { get; set; } = new();

    /// <summary>Device ids the cluster may use. Null lends every detected device.</summary>
    public List<string>? LendDevices { get; set; }

    /// <summary>Workload kinds allowed here. Null allows every kind this node has a driver for.</summary>
    public List<string>? Kinds { get; set; }

    /// <summary>The programs the built-in process kind may start on this node. Nothing else can be executed.</summary>
    public Dictionary<string, ProgramOptions> Programs { get; set; } = new();

    /// <summary>The most favourable priority cluster work may ask for on this machine.</summary>
    public ExecutionPriority MaxPriority { get; set; } = ExecutionPriority.Interactive;

    /// <summary>
    /// Whether stopping the node process stops its work. Off by default so that upgrading or restarting the
    /// node does not interrupt work; the next process finds it again.
    /// </summary>
    public bool StopWorkloadsOnShutdown { get; set; }

    /// <summary>
    /// The longest the node waits for work it must stop when the node process itself is stopping. Work that
    /// has not finished by then is ended, so that the node never exits leaving work holding an exclusive key
    /// behind it. Keep the host's own shutdown timeout above this.
    /// </summary>
    public int ShutdownGraceSeconds { get; set; } = 20;

    /// <summary>Longest a single interactive hold lasts without being renewed. A holder that dies cannot pause batch work for longer.</summary>
    public int InteractiveHoldMaxSeconds { get; set; } = 60;

    public int CpuBusyPercent { get; set; } = 85;

    /// <summary>
    /// A temperature, in degrees Celsius, at or above which the node reports pressure. Zero does not look at
    /// temperature. Read from the thermal zones Linux publishes; other platforms have no reading and report none.
    /// </summary>
    public int MaxTemperatureC { get; set; }

    /// <summary>
    /// Let this node's own pressure verdict hold back the realtime module on the same host: while the node
    /// is under pressure, sessions cannot claim more node resources (a new match, a relay). Claims already
    /// held are never taken away, and an unknown verdict does not hold anything back. Off by default.
    /// </summary>
    public bool GuardRealtime { get; set; }
    public int MinFreeMemoryMb { get; set; } = 768;

    internal void Validate(List<string> errors)
    {
        foreach (var (name, program) in Programs)
        {
            if (name.Length == 0 || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
                errors.Add($"Cluster:Agent:Programs name '{name}' may contain only letters, digits, '-', '_' and '.'");
            if (!Path.IsPathFullyQualified(program.Executable)) errors.Add($"Cluster:Agent:Programs:{name}:Executable must be an absolute path");
            if (program.WorkingDirectory.Length > 0 && !Path.IsPathFullyQualified(program.WorkingDirectory))
                errors.Add($"Cluster:Agent:Programs:{name}:WorkingDirectory must be absolute");
            if (program.ParameterFlags.Values.Any(string.IsNullOrWhiteSpace)) errors.Add($"Cluster:Agent:Programs:{name}:ParameterFlags must not be blank");
        }

        if (ShutdownGraceSeconds is < 0 or > 21600) errors.Add("Cluster:Agent:ShutdownGraceSeconds must be between 0 and 21600");
        if (InteractiveHoldMaxSeconds is < 1 or > 600) errors.Add("Cluster:Agent:InteractiveHoldMaxSeconds must be between 1 and 600");
        if (Lend.Values.Any(v => v < 0)) errors.Add("Cluster:Agent:Lend values must not be negative");
    }
}

/// <summary>
/// One program a node's operator allows the cluster to start. The executable and its fixed arguments are the
/// operator's; a workload can only choose among the listed parameters, each of which becomes a literal
/// argument after its flag. There is no shell and no caller-supplied command line.
/// </summary>
public sealed class ProgramOptions
{
    public string Executable { get; set; } = "";
    public List<string> Arguments { get; set; } = [];
    public Dictionary<string, string> ParameterFlags { get; set; } = new();
    public string WorkingDirectory { get; set; } = "";
    public Dictionary<string, string> Environment { get; set; } = new();
}
