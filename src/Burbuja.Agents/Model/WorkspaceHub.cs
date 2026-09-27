namespace Burbuja.Agents.Model;

/// <summary>
/// K3: what a workspace HOLDS, in one read.
///
/// <para><b>The question this answers.</b> A workspace is a repo record with a job list, a schedule list
/// and a budget; an operator asking "what is going on in here" had to list jobs, list schedules, read each
/// job's cost and add them up, and stitch the answer themselves — which the portal did in C# and the CLI
/// did differently. The hub is that stitch, computed by the kernel, so every reader gets the same numbers
/// and a disagreement between two surfaces becomes impossible rather than unlikely.</para>
///
/// <para><b>Filled on a single-resource READ, and deliberately not on a list.</b> The same choice
/// <c>MandateProperties</c> makes for its rung and halt latch, with one addition: a list of a hundred
/// workspaces must not scan a hundred job sets, so <see cref="WorkspaceProperties.Hub"/> is null on every
/// row of a list and filled when one workspace is asked for by name. A fact asserts both halves, because
/// "the hub is there" and "the hub is not there in a list" are two claims and only one of them is
/// obvious.</para>
/// </summary>
public sealed class WorkspaceHub
{
    /// <summary>Every job the workspace holds, inside the scan bound.</summary>
    public int Jobs { get; set; }

    /// <summary>
    /// The statuses, grouped. The groups are a PARTITION of <c>JobExecutionStatus</c> and a fact asserts
    /// they cover it — a status added to the enum without deciding which group it belongs in is a red test
    /// rather than a count that quietly misses it.
    /// </summary>
    public Dictionary<string, int> ByStatus { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The most recent jobs, newest first. Bounded, and the bound is in the code that fills it.</summary>
    public List<WorkspaceHubJob> Recent { get; set; } = [];

    /// <summary>
    /// What the workspace's jobs have cost, summed from the jobs themselves.
    ///
    /// <para><b>Why the jobs and not the ledger.</b> The ledger is keyed by DAY and its entries carry job
    /// ids, not workspace ids: summing it per workspace would mean reading every day and resolving every
    /// job id back to a workspace, which is a different (and much larger) question. Each job already
    /// carries its own cost, and the sum of the jobs in the workspace is the answer to "what has this
    /// workspace spent" for every job that still exists.</para>
    /// </summary>
    public decimal SpendUsd { get; set; }

    /// <summary>The workspace's per-job default, not a cap: a job may be PUT with its own.</summary>
    public decimal? DefaultMaxUsd { get; set; }

    public int Schedules { get; set; }

    public int PausedSchedules { get; set; }

    /// <summary>The soonest window among the schedules that are not paused, as the store holds it.</summary>
    public DateTimeOffset? NextScheduleRun { get; set; }

    /// <summary>How many job rows the projection read. Equals <see cref="Jobs"/> unless the scan bound was
    /// reached, which is what makes <see cref="Truncated"/> readable rather than mysterious.</summary>
    public int Scanned { get; set; }

    /// <summary>True when the scan bound was reached, so the counts are a floor rather than a total.</summary>
    public bool Truncated { get; set; }

    public DateTimeOffset GeneratedAt { get; set; }
}

/// <summary>One row of the hub's recent list: enough to decide whether to open the job.</summary>
public sealed class WorkspaceHubJob
{
    public string Name { get; set; } = "";

    public string Status { get; set; } = "";

    public string Title { get; set; } = "";

    public decimal Usd { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>Set while the job waits for a human, so the hub can point at the card rather than only at
    /// the job.</summary>
    public string? PendingApprovalId { get; set; }
}
