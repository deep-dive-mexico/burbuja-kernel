using Burbuja.Agents.Abstractions.Registry;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Burbuja.Agents.Runtime;

public sealed class YamlRepoRegistry : IRepoRegistry
{
    private readonly Dictionary<string, RepoFacts> _repos;

    public YamlRepoRegistry(string? path)
    {
        _repos = new Dictionary<string, RepoFacts>(StringComparer.OrdinalIgnoreCase)
        {
            ["demo"] = new(
                "demo",
                "scratch",
                "main",
                ["main"],
                ["agent/"],
                false,
                [],
                [],
                [],
                []),
        };
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;
        var yaml = File.ReadAllText(path);
        var deserializer = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).IgnoreUnmatchedProperties().Build();
        var doc = deserializer.Deserialize<ReposFile>(yaml);
        foreach (var row in doc?.Repos ?? [])
        {
            if (string.IsNullOrWhiteSpace(row.RepoId))
                continue;
            _repos[row.RepoId] = new RepoFacts(
                row.RepoId,
                row.Remote ?? "scratch",
                row.BaseBranch ?? "main",
                row.ProtectedBranches ?? ["main"],
                row.PushAllowedPrefixes ?? ["agent/"],
                row.PullRequests,
                (row.Envs ?? []).Select(e => new RepoEnv(e.Name ?? "", e.Hosts ?? [])).ToArray(),
                row.Bootstrap ?? [],
                row.Verify ?? [],
                row.SecretStores ?? []);
        }
    }

    public RepoFacts? Get(string repoId) => _repos.TryGetValue(repoId, out var facts) ? facts : null;

    public IReadOnlyList<RepoFacts> List() => _repos.Values.OrderBy(r => r.RepoId, StringComparer.Ordinal).ToArray();

    private sealed class ReposFile
    {
        public List<RepoRow>? Repos { get; set; }
    }

    private sealed class RepoRow
    {
        public string? RepoId { get; set; }
        public string? Remote { get; set; }
        public string? BaseBranch { get; set; }
        public List<string>? ProtectedBranches { get; set; }
        public List<string>? PushAllowedPrefixes { get; set; }
        public bool PullRequests { get; set; }
        public List<EnvRow>? Envs { get; set; }
        public List<string>? Bootstrap { get; set; }
        public List<string>? Verify { get; set; }
        public List<string>? SecretStores { get; set; }
    }

    private sealed class EnvRow
    {
        public string? Name { get; set; }
        public List<string>? Hosts { get; set; }
    }
}
