using System.Globalization;
using Burbuja.Cluster;
using Burbuja.Cluster.Abstractions;

namespace Burbuja.Node;

/// <summary>
/// The operator's command line. It is only a client of the controller's HTTP API: everything it does can be
/// done with the API directly, and it holds no state of its own.
/// </summary>
internal static class OperatorCommand
{
    private const string Usage = """
        usage: burbuja-node ctl [--controller URL] [--state DIR | --token-file FILE] [--json] <verb>

          status                     nodes and workloads, with the reason anything is pending
          token                      create a one-time token for a machine to join with
          apply FILE                 create or change workloads from a JSON file (one object or an array)
          delete NAME                remove a workload; its node stops it
          cordon|drain|activate NODE stop placing on a node / also move what may move / place there again
          forget NODE                remove a node from the cluster; it stops the cluster's work

        The controller defaults to http://127.0.0.1:18520 and the token to operator.token in --state
        (or in $BURBUJA_CLUSTER_STATE).
        """;

    public static async Task<int> RunAsync(string[] args)
    {
        var controller = "http://127.0.0.1:18520";
        var state = Environment.GetEnvironmentVariable("BURBUJA_CLUSTER_STATE");
        string? tokenFile = null;
        var json = false;
        var rest = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--controller" when i + 1 < args.Length: controller = args[++i]; break;
                case "--state" when i + 1 < args.Length: state = args[++i]; break;
                case "--token-file" when i + 1 < args.Length: tokenFile = args[++i]; break;
                case "--json": json = true; break;
                default: rest.Add(args[i]); break;
            }
        }

        tokenFile ??= state is { Length: > 0 } ? Path.Combine(state, "operator.token") : null;
        if (rest.Count == 0 || tokenFile is null || !File.Exists(tokenFile) || !Uri.TryCreate(controller, UriKind.Absolute, out var url))
        {
            Console.Error.WriteLine(tokenFile is not null && !File.Exists(tokenFile) ? $"No operator token at {tokenFile}.\n\n{Usage}" : Usage);
            return 2;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var client = new ClusterClient(http, url, File.ReadAllText(tokenFile).Trim());
        try
        {
            switch (rest[0], rest.Count)
            {
                case ("status", 1):
                    var status = await client.StatusAsync();
                    Console.WriteLine(json ? ClusterJson.Write(status) : Render(status));
                    return 0;
                case ("token", 1):
                    var grant = await client.CreateJoinTokenAsync();
                    Console.WriteLine(json ? ClusterJson.Write(grant) : grant.Token);
                    if (!json) Console.Error.WriteLine($"Valid once, until {grant.ExpiresAt:u}. Carry it to the joining machine out of band.");
                    return 0;
                case ("apply", 2):
                    return await ApplyAsync(client, rest[1]);
                case ("delete", 2):
                    return Report(await client.DeleteAsync(rest[1]), $"workload {rest[1]} deleted", $"no workload named {rest[1]}");
                case ("cordon" or "drain" or "activate", 2):
                    return Report(await client.SetNodeAsync(rest[1], rest[0]), $"node {rest[1]}: {rest[0]}", $"no node named {rest[1]}");
                case ("forget", 2):
                    return Report(await client.ForgetNodeAsync(rest[1]), $"node {rest[1]} removed", $"no node named {rest[1]}");
                default:
                    Console.Error.WriteLine(Usage);
                    return 2;
            }
        }
        catch (HttpRequestException e)
        {
            Console.Error.WriteLine($"Could not reach the controller at {url}: {e.Message}");
            return 1;
        }
    }

    private static int Report(bool ok, string done, string failed)
    {
        (ok ? Console.Out : Console.Error).WriteLine(ok ? done : failed);
        return ok ? 0 : 1;
    }

    private static async Task<int> ApplyAsync(ClusterClient client, string file)
    {
        var text = await File.ReadAllTextAsync(file);
        var specs = text.TrimStart().StartsWith('[') ? ClusterJson.Read<List<WorkloadSpec>>(text) : ClusterJson.Read<WorkloadSpec>(text) is { } one ? [one] : null;
        if (specs is null || specs.Count == 0)
        {
            Console.Error.WriteLine($"{file} is not a workload specification or an array of them.");
            return 1;
        }

        var failed = 0;
        foreach (var spec in specs)
        {
            var (workload, errors) = await client.ApplyAsync(spec);
            if (workload is null)
            {
                failed++;
                Console.Error.WriteLine($"{spec.Name}: declined: {string.Join("; ", errors)}");
            }
            else
            {
                Console.WriteLine($"{spec.Name}: {Phase(workload)}");
            }
        }

        return failed == 0 ? 0 : 1;
    }

    private static string Phase(WorkloadView workload) =>
        workload.NodeId is null ? $"pending ({workload.Reason})" : $"{workload.Phase.ToString().ToLowerInvariant()} on {workload.NodeId}";

    private static string Render(ClusterStatus status)
    {
        var lines = new List<string> { $"cluster {status.Identity.ClusterId}", "", "NODES" };
        if (status.Nodes.Count == 0) lines.Add("  (none; create a token with `ctl token` and start a node with it)");
        foreach (var node in status.Nodes)
        {
            var descriptor = node.Descriptor;
            var health = !node.Reachable ? "unreachable" : node.Pressured ? "pressured" : node.ObservationFresh ? "ready" : "ready (no reading)";
            var capacity = string.Join("  ", descriptor.Capacity.OrderBy(c => c.Key, StringComparer.Ordinal)
                .Select(c => $"{c.Key} {Number(node.Allocated.GetValueOrDefault(c.Key))}/{Number(c.Value)}"));
            lines.Add($"  {descriptor.NodeId,-16} {health,-18} {node.Availability.ToString().ToLowerInvariant(),-9} {descriptor.Platform.Os}/{descriptor.Platform.Architecture,-6} {capacity}");
        }

        lines.Add("");
        lines.Add("WORKLOADS");
        if (status.Workloads.Count == 0) lines.Add("  (none)");
        foreach (var workload in status.Workloads)
        {
            lines.Add($"  {workload.Spec.Name,-16} {workload.Phase.ToString().ToLowerInvariant(),-10} {workload.NodeId ?? "-",-16} gen {workload.Generation}  {workload.Spec.Class.ToString().ToLowerInvariant()}"
                + (workload.Reason is { Length: > 0 } reason ? $"  ({reason})" : ""));
            if (workload.NodeId is not null) continue;
            foreach (var (node, why) in workload.Rejections.OrderBy(r => r.Key, StringComparer.Ordinal)) lines.Add($"      {node}: {why}");
        }

        return string.Join('\n', lines);

        static string Number(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
