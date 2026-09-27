using System.Text.Json;
using System.Text.Json.Nodes;
using Burbuja.Compute.Abstractions;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Json;
using Burbuja.Core.Arm;

namespace Burbuja.Compute;

public sealed class ComputeOptions
{
    public const string Section = "Compute";

    public LocalDirectoryOptions LocalDirectory { get; set; } = new();

    public GitOptions Git { get; set; } = new();

    public TimeSpan MaxTtl { get; set; } = TimeSpan.FromHours(12);

    public TimeSpan DefaultIdleTimeout { get; set; } = TimeSpan.FromMinutes(30);

    public TimeSpan ReapInterval { get; set; } = TimeSpan.FromMinutes(1);

    public int ExecMaxOutputBytes { get; set; } = 256 * 1024;

    public string Profile { get; set; } = "";
}

public sealed class LocalDirectoryOptions
{
    public string Root { get; set; } = "";
}

public sealed class GitOptions
{
    public string Root { get; set; } = "";

    /// <summary>Remote URL prefixes a git-worktree workspace may clone from.</summary>
    public List<string> AllowedRemotes { get; set; } = [];
}

public sealed class BackendRegistry(IEnumerable<IComputeBackend> backends)
{
    private readonly Dictionary<string, IComputeBackend> _byKind =
        backends.ToDictionary(b => b.Kind, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<IComputeBackend> All => _byKind.Values;

    public bool TryGet(string kind, out IComputeBackend backend) => _byKind.TryGetValue(kind, out backend!);

    public IReadOnlyList<string> Kinds => _byKind.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
}

/// <summary>Re-read + If-Match retry over the resource store, like the Deployments state store.</summary>
public sealed class ComputeStateStore(IResourceStore store)
{
    public async ValueTask<ResourceEnvelope<ComputeWorkspaceProperties>?> GetAsync(string key, CancellationToken ct)
    {
        var env = await store.GetAsync(key, ct);
        return env is null ? null : Map(env);
    }

    public async ValueTask<ResourceEnvelope<ComputeWorkspaceProperties>> PersistAsync(
        ResourceEnvelope<ComputeWorkspaceProperties> envelope,
        CancellationToken ct)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                return Map(await store.UpsertAsync(ToJson(envelope), envelope.ETag, ct));
            }
            catch (ArmException ex) when (ex.Error.HttpStatus == 412 && attempt < 7)
            {
                var latest = await store.GetAsync(envelope.Id.ToLowerInvariant(), ct);
                if (latest is null)
                    throw;
                envelope = envelope with { ETag = latest.ETag };
            }
        }

        throw new ArmException(ArmError.PreconditionFailed("Could not persist compute workspace state."));
    }

    public async ValueTask<ResourceEnvelope<ComputeWorkspaceProperties>> MutateAsync(
        string key,
        Action<ComputeWorkspaceProperties> mutate,
        CancellationToken ct,
        ProvisioningState? provisioningState = null)
    {
        ArmException? last = null;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var env = await GetAsync(key, ct) ?? throw new InvalidOperationException(key);
            mutate(env.Properties);
            if (provisioningState is { } state)
                env = env with { ProvisioningState = state };
            try
            {
                return await PersistAsync(env, ct);
            }
            catch (ArmException ex) when (ex.Error.HttpStatus == 412)
            {
                last = ex;
            }
        }

        throw last ?? new ArmException(ArmError.PreconditionFailed("Could not persist compute workspace state."));
    }

    public static ResourceEnvelope<JsonObject> ToJson(ResourceEnvelope<ComputeWorkspaceProperties> env) =>
        new(
            env.Id,
            env.Name,
            env.Type,
            env.Location,
            env.Tags,
            JsonSerializer.SerializeToNode(env.Properties, JsonWire.Options)!.AsObject(),
            env.ProvisioningState,
            env.SystemData,
            env.ETag);

    public static ResourceEnvelope<ComputeWorkspaceProperties> Map(ResourceEnvelope<JsonObject> env) =>
        new(
            env.Id,
            env.Name,
            env.Type,
            env.Location,
            env.Tags,
            env.Properties.Deserialize<ComputeWorkspaceProperties>(JsonWire.Options) ?? new ComputeWorkspaceProperties(),
            env.ProvisioningState,
            env.SystemData,
            env.ETag);
}

/// <summary>Stable spec hash used for async-PUT replay detection.</summary>
public static class ComputeSpecHash
{
    public static string Compute(ComputeWorkspaceProperties spec)
    {
        var clone = new JsonObject
        {
            ["displayName"] = spec.DisplayName,
            ["backend"] = spec.Backend,
            ["image"] = spec.Image,
            ["size"] = spec.Size,
            ["repo"] = JsonSerializer.SerializeToNode(spec.Repo, JsonWire.Options),
            ["envAllowlist"] = new JsonArray(spec.EnvAllowlist.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()),
            ["ttl"] = spec.Ttl?.ToString(),
            ["idleTimeout"] = spec.IdleTimeout?.ToString(),
        };
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(clone.ToJsonString()))).ToLowerInvariant();
    }
}
