namespace Burbuja.Cluster.Abstractions;

/// <summary>
/// Names shared by every part of a cluster. They are plain strings on purpose: a resource, a capability, a
/// workload kind or a network is something a plugin may add, so none of them is an enum the kernel owns.
/// </summary>
public static class ClusterConstants
{
    public const string Namespace = "Burbuja.Cluster";
    public const string Section = "Cluster";

    /// <summary>
    /// Wire protocol between a node and its controller. A mismatch is refused, never negotiated down. A
    /// property rather than a constant, so a plugin reads the host's value instead of the one it was built with.
    /// </summary>
    public static int ProtocolVersion => 1;

    /// <summary>
    /// The unauthenticated identity document. It carries nothing secret and exists so a node can tell that an
    /// address answers as the cluster it expects <em>before</em> it presents any credential there.
    /// </summary>
    public const string IdentityPath = "/.well-known/burbuja/cluster";

    /// <summary>Resources every node measures. Applications add their own names (a measured room cap, a seat).</summary>
    public static class Resources
    {
        /// <summary>Logical processor cores.</summary>
        public const string Cpu = "cpu";

        /// <summary>Main memory, in MiB.</summary>
        public const string MemoryMb = "memory-mb";

        /// <summary>Whole lendable accelerators.</summary>
        public const string Gpu = "gpu";
    }

    /// <summary>Things a node can do that are not a quantity.</summary>
    public static class Capabilities
    {
        public const string Cuda = "cuda";
        public const string Mps = "mps";

        /// <summary>Prefix for what the node's operating-system adapter can actually enforce, e.g. <c>os.memory-limit</c>.</summary>
        public const string OsPrefix = "os.";
    }

    public static class Kinds
    {
        /// <summary>An operator-allowlisted program run as an owned process tree. The only built-in kind.</summary>
        public const string Process = "process";
    }

    public static class Networks
    {
        public const string Loopback = "loopback";
        public const string Lan = "lan";
        public const string Static = "static";
        public const string Tailscale = "tailscale";
        public const string Twingate = "twingate";
    }
}

/// <summary>
/// Names of nodes and workloads. They become file names on a node (a log, a working directory), so the rule
/// is checked by the controller when a name is submitted <em>and</em> by every node before it acts on one: a
/// node does not trust a name just because its controller sent it.
/// </summary>
public static class ClusterNames
{
    /// <summary>1-63 of lower-case letters, digits, '-', '_' and '.', starting with a letter or digit, with no "..".</summary>
    public static bool IsValid(string? name) =>
        name is { Length: > 0 and <= 63 }
        && (char.IsAsciiLetterLower(name[0]) || char.IsAsciiDigit(name[0]))
        && name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '.' or '_')
        && !name.Contains("..", StringComparison.Ordinal);
}

/// <summary>
/// Arithmetic over named quantities. A name that is absent is <b>not offered</b>, which is different from
/// zero: a node that never declared <c>gpu</c> cannot satisfy a request for one, and the reason says so
/// rather than reporting "0 of 1 free".
/// </summary>
public static class ResourceMath
{
    public static readonly IReadOnlyDictionary<string, decimal> Empty = new Dictionary<string, decimal>();

    /// <summary>The first requested resource that <paramref name="free"/> cannot cover, or null when all fit.</summary>
    public static string? FirstShortfall(IReadOnlyDictionary<string, decimal> requested, IReadOnlyDictionary<string, decimal> free)
    {
        foreach (var (name, amount) in requested)
        {
            if (amount <= 0) continue;
            if (!free.TryGetValue(name, out var available)) return $"{name} is not offered";
            if (available < amount) return $"{name}: {amount} requested, {available} free";
        }

        return null;
    }

    public static Dictionary<string, decimal> Add(IReadOnlyDictionary<string, decimal> left, IReadOnlyDictionary<string, decimal> right)
    {
        var sum = new Dictionary<string, decimal>(left, StringComparer.Ordinal);
        foreach (var (name, amount) in right) sum[name] = sum.GetValueOrDefault(name) + amount;
        return sum;
    }

    /// <summary>What remains of <paramref name="capacity"/>; only names the capacity declares appear in the result.</summary>
    public static Dictionary<string, decimal> Remaining(IReadOnlyDictionary<string, decimal> capacity, IReadOnlyDictionary<string, decimal> used)
    {
        var free = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var (name, amount) in capacity) free[name] = amount - used.GetValueOrDefault(name);
        return free;
    }
}
