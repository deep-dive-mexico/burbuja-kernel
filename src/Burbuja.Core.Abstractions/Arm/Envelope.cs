using System.Text.Json.Nodes;

namespace Burbuja.Core.Abstractions.Arm;

public enum ProvisioningState
{
    Accepted,
    Creating,
    Updating,
    Deleting,
    Succeeded,
    Failed,
    Canceled,
}

public sealed record SystemData(
    DateTimeOffset? CreatedAt,
    string? CreatedBy,
    string? CreatedByType,
    DateTimeOffset? LastModifiedAt,
    string? LastModifiedBy,
    string? LastModifiedByType);

public sealed record ResourceEnvelope<TProps>(
    string Id,
    string Name,
    string Type,
    string? Location,
    IReadOnlyDictionary<string, string> Tags,
    TProps Properties,
    ProvisioningState ProvisioningState,
    SystemData SystemData,
    string ETag) : IResourceEnvelope
{
    object IResourceEnvelope.Properties => Properties!;
}

public interface IResourceEnvelope
{
    string Id { get; }
    string Name { get; }
    string Type { get; }
    string ETag { get; }
    object Properties { get; }
}

public interface IOutcomeView
{
    void Apply(ArmRequestContext context);
}

public sealed record PagedResult<T>(IReadOnlyList<T> Value, string? NextLink);

public sealed record ResourcePatch(IReadOnlyDictionary<string, string>? Tags, JsonObject? Properties);
