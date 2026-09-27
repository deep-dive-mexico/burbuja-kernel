namespace Burbuja.Agents.Abstractions.Registry;

public sealed record RepoEnv(string Name, IReadOnlyList<string> Hosts);

public sealed record RepoFacts(
    string RepoId,
    string Remote,
    string BaseBranch,
    IReadOnlyList<string> ProtectedBranches,
    IReadOnlyList<string> PushAllowedPrefixes,
    bool PullRequests,
    IReadOnlyList<RepoEnv> Envs,
    IReadOnlyList<string> Bootstrap,
    IReadOnlyList<string> Verify,
    IReadOnlyList<string> SecretStores);

public interface IRepoRegistry
{
    RepoFacts? Get(string repoId);
    IReadOnlyList<RepoFacts> List();
}
