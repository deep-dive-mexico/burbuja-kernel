namespace Burbuja.TestKit;

/// <summary>
/// A throwaway git repository with one commit, for the workflows that insist on a real repo
/// (Pomnibot's worktrees, Burbuja.Compute's git backend). Created under the caller's state
/// directory so a failed run leaves the evidence behind.
/// </summary>
public static class ScratchRepo
{
    public static async Task<string> CreateAsync(
        string directory,
        string branch = "main",
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(directory);
        await GitAsync(directory, ["init", "-b", branch], ct);
        await GitAsync(directory, ["config", "user.email", "e2e@burbuja.local"], ct);
        await GitAsync(directory, ["config", "user.name", "Burbuja E2E"], ct);
        await File.WriteAllTextAsync(Path.Combine(directory, "README.md"), "# scratch\n", ct);
        await GitAsync(directory, ["add", "-A"], ct);

        // Only commit when there is something to commit. The helper used to run `git commit` unconditionally,
        // which throws `nothing to commit` (exit 1) the SECOND time it is called on the same directory — and
        // a lane whose fixture prepares its scratch repo before every run calls it exactly that way. Found by
        // the pomnibot real lane's second run, which failed in the fixture rather than in any case.
        var staged = await GitAsync(directory, ["status", "--porcelain"], ct);
        if (staged.Trim().Length > 0)
            await GitAsync(directory, ["commit", "-m", "initial commit"], ct);

        return directory;
    }

    public static async Task<string> HeadAsync(string directory, CancellationToken ct = default) =>
        (await GitAsync(directory, ["rev-parse", "HEAD"], ct)).Trim();

    private static async Task<string> GitAsync(string directory, string[] args, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync(
            "git",
            args,
            directory,
            new Dictionary<string, string?> { ["GIT_TERMINAL_PROMPT"] = "0" },
            ct);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} failed ({result.ExitCode}): {result.StandardError.Trim()}");
        }

        return result.StandardOutput;
    }
}
