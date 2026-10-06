// Adapted from burbuja-server 97940d9; the fixture here is the independent generic kernel host.
using Burbuja.TestKit;
using Shouldly;

namespace Burbuja.E2E.Tests;

/// <summary>
/// M13 R0 — the ARM-level recipes the E2E lane shares, in ONE file.
///
/// <para><b>Why this exists.</b> M12 R2 collapsed the browser helpers into one file
/// (<see cref="PortalBrowserHelpers"/>) and the audit found the job still half done: <c>PutWorkspaceAsync</c>,
/// <c>PutJobAsync</c> and <c>WaitForJobAsync</c> were byte-identical in <c>LedgerE2ETests</c> and
/// <c>AgentE2ETests</c>, and the browser file carried a THIRD copy. Three copies of a request body is not
/// tidiness — the body is the part the kernel validates, and the M12 R3 note in the browser helper records
/// exactly how that fails: a hand-written approximation answered <c>400 ValidationFailed</c>, so the fact went
/// red for a reason that had nothing to do with the page under test.</para>
///
/// <para><b>Why a separate class from the browser helpers.</b> These need no browser, no page and no fixture —
/// they take an <see cref="ArmTestClient"/> and talk HTTP. Putting them here means the HTTP lane can use them
/// without a Playwright type in scope, and it is why <see cref="PortalBrowserHelpers"/> now delegates rather
/// than defining a fourth copy. The browser file keeps its own name for the browser-shaped calls
/// (<c>SignInAsync</c>, <c>FillStickyAsync</c>, <c>ShotAsync</c>); the recipes live here.</para>
/// </summary>
internal static class ArmTestHelpers
{
    public const string AgentsRoot = "/subscriptions/local/resourceGroups/dev/providers/Burbuja.Agents";

    public const string ResourcesRoot = "/subscriptions/local/providers/Burbuja.Resources";

    /// <summary>The tag the scripted provider reads its scenario from.</summary>
    public const string ScenarioTag = "burbuja.io/scenario";

    /// <summary>The one model the fixture's price book knows, so a cost-booking fact can price its turns.</summary>
    public const string PricedModelId = "us.anthropic.claude-sonnet-4-5-20250929-v1:0";

    public static string WorkspacePath(string name) => AgentsRoot + "/workspaces/" + name;

    public static string JobPath(string ws, string job) => WorkspacePath(ws) + "/jobs/" + job;

    /// <summary>
    /// Puts a workspace with the profile the E2E recipes need.
    ///
    /// <para>The body has <c>tags</c> at the ROOT beside <c>properties</c> and a <c>profile</c> of
    /// <c>DevFullAuto</c>; a hand-written approximation is what answered <c>400 ValidationFailed</c> in M12 R3.
    /// </para>
    /// </summary>
    public static async Task<string> PutWorkspaceAsync(ArmTestClient client, string name, CancellationToken ct)
    {
        var body = "{\"properties\":{\"profile\":\"DevFullAuto\",\"repoId\":\"local/demo\"}}";
        var path = WorkspacePath(name);
        using var put = await client.PutAsync(path, body, ct);
        put.IsSuccessStatusCode.ShouldBeTrue(
            $"the workspace PUT must be accepted: {(int)put.StatusCode} {await put.Content.ReadAsStringAsync(ct)}");
        return path;
    }

    /// <summary>
    /// Puts a job that replays a scripted scenario, and returns its ARM path.
    ///
    /// <para>The body is the one the D4/D11 recipes established: <c>tags["burbuja.io/scenario"]</c> at the root
    /// (the scripted provider reads the scenario from there) and a <c>spec</c> under <c>properties</c>. A job PUT
    /// answers <c>202 Accepted</c>, not 200. <paramref name="modelId"/> is null by default — an unset model is
    /// the host's default, which is what a fact about the RUN wants; a fact about COST passes
    /// <see cref="PricedModelId"/>, because the host default is not in the price book and a job with no priced
    /// model is refused at PUT.</para>
    /// </summary>
    public static async Task<string> PutJobAsync(
        ArmTestClient client,
        string ws,
        string job,
        string scenario,
        string? modelId,
        bool? autoAccept,
        CancellationToken ct)
    {
        var model = modelId is null ? "" : "\"modelId\":\"" + modelId + "\",";
        var accept = (autoAccept ?? true) ? "true" : "false";
        var body = "{\"tags\":{\"" + ScenarioTag + "\":\"" + scenario + "\"},\"properties\":{" + model + "\"spec\":"
                   + "{\"kind\":\"implement\",\"title\":\"t1\",\"slug\":\"job\",\"brief\":\"do the work\","
                   + "\"language\":\"es\",\"targetEnv\":\"dev\",\"autoAccept\":" + accept + "}}}";
        var path = JobPath(ws, job);
        using var put = await client.PutAsync(path, body, ct);
        put.StatusCode.ShouldBe(
            System.Net.HttpStatusCode.Accepted,
            await put.Content.ReadAsStringAsync(ct));
        return path;
    }

    /// <summary>
    /// Waits for a job to reach a status, answered THROUGH ARM rather than from a page.
    ///
    /// <para>Returns the last status it saw rather than throwing, so the caller's <c>ShouldBe</c> prints the
    /// status the host actually reached — the version that threw made every miss read as a timeout.</para>
    /// </summary>
    public static async Task<string> WaitForJobAsync(
        ArmTestClient client,
        string path,
        string expected,
        int seconds,
        CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(seconds);
        var last = "unknown";
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var get = await client.GetAsync(path, ct);
            if (get.IsSuccessStatusCode)
            {
                var body = await ArmTestClient.ReadJsonAsync(get, ct);
                last = body?["properties"]?["status"]?.GetValue<string>() ?? "unknown";
                if (string.Equals(last, expected, StringComparison.OrdinalIgnoreCase))
                    return last;
            }

            await Task.Delay(250, ct);
        }

        return last;
    }

    /// <summary>
    /// The pending approval a parked job opened, as a FULL ARM path (so it can be POSTed to), or null.
    ///
    /// <para>Matched on the approval's own <c>request.streamId</c> against the job's path suffix: a subject
    /// string match would also catch a different job whose name shares a prefix.</para>
    /// </summary>
    public static async Task<string?> WaitForApprovalAsync(
        ArmTestClient client,
        string jobPath,
        int seconds,
        CancellationToken ct)
    {
        var suffix = jobPath[jobPath.IndexOf("/jobs/", StringComparison.Ordinal)..].ToLowerInvariant();
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(seconds);

        // D4's narrowing (2026-09-12), kept: the version that returned null in silence made every failure read
        // as "no approval" whether the list answered 400, answered 200 with an empty array, or answered 200
        // with approvals whose streamId did not match. The line below tells those three apart.
        var lastStatus = "never called";
        var lastBody = "";
        var lastStreams = "";
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var get = await client.GetAsync(ResourcesRoot + "/approvals?pendingOnly=true", ct);
            lastStatus = ((int)get.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (get.StatusCode == System.Net.HttpStatusCode.OK)
            {
                var body = await ArmTestClient.ReadJsonAsync(get, ct);
                lastBody = body?.ToJsonString() ?? "(null)";
                var streams = new List<string>();
                foreach (var item in body?["value"]?.AsArray() ?? [])
                {
                    var stream = item?["properties"]?["request"]?["streamId"]?.GetValue<string>();
                    streams.Add(stream ?? "(no streamId)");
                    if (stream is not null && stream.Contains(suffix, StringComparison.OrdinalIgnoreCase))
                        return item?["id"]?.GetValue<string>();
                }

                lastStreams = streams.Count == 0 ? "(value was empty)" : string.Join(" | ", streams);
            }

            await Task.Delay(250, ct);
        }

        Console.WriteLine($"[D4] WaitForApprovalAsync gave up for {jobPath}: lastStatus={lastStatus} streams={lastStreams} body={lastBody}");
        return null;
    }
}
