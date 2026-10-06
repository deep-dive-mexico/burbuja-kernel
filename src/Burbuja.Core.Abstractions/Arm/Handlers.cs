using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Burbuja.Core.Abstractions.Arm;

public record ResourceRequest
{
    public required ResourceId Id { get; init; }
    public required string ApiVersion { get; init; }
    public required ArmPrincipal Principal { get; init; }
    public required string RequestId { get; init; }
    public string? CorrelationId { get; init; }
    public string? IfMatch { get; init; }
    public string? IfNoneMatch { get; init; }
    public IReadOnlyDictionary<string, string> Query { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>();
    public string? Location { get; init; }
    public required IServiceProvider Services { get; init; }
}

public sealed record ResourceListRequest : ResourceRequest
{
    public int Top { get; init; } = 100;
    public string? ContinuationToken { get; init; }
    public string? Filter { get; init; }
    public ArmListFilter? ParsedFilter { get; init; }
}

public sealed record ResourceActionRequest : ResourceRequest
{
    public required string Action { get; init; }
    public JsonObject? Body { get; init; }
}

[JsonConverter(typeof(PutOutcomeJsonConverterFactory))]
public abstract record PutOutcome<TProps> : IOutcomeView where TProps : class
{
    public sealed record Created(ResourceEnvelope<TProps> Resource) : PutOutcome<TProps>
    {
        public override void Apply(ArmRequestContext context)
        {
            context.StatusCode = 201;
            context.Result = Resource;
            context.ResponseHeaders["ETag"] = Resource.ETag;
        }
    }

    public sealed record Updated(ResourceEnvelope<TProps> Resource) : PutOutcome<TProps>
    {
        public override void Apply(ArmRequestContext context)
        {
            context.StatusCode = 200;
            context.Result = Resource;
            context.ResponseHeaders["ETag"] = Resource.ETag;
        }
    }

    public sealed record Accepted(ResourceEnvelope<TProps> Resource, string OperationId, TimeSpan RetryAfter) : PutOutcome<TProps>
    {
        public override void Apply(ArmRequestContext context)
        {
            context.StatusCode = 202;
            context.Result = Resource;
            context.ResponseHeaders["ETag"] = Resource.ETag;
            context.OperationId = OperationId;
            context.RetryAfter = RetryAfter;
        }
    }

    public sealed record Rejected(ArmError Error) : PutOutcome<TProps>
    {
        public override void Apply(ArmRequestContext context) => context.Error = Error;
    }

    public abstract void Apply(ArmRequestContext context);
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(Deleted), "deleted")]
[JsonDerivedType(typeof(NotFound), "notFound")]
[JsonDerivedType(typeof(Accepted), "accepted")]
[JsonDerivedType(typeof(Rejected), "rejected")]
public abstract record DeleteOutcome : IOutcomeView
{
    public sealed record Deleted : DeleteOutcome
    {
        public override void Apply(ArmRequestContext context) => context.StatusCode = 200;
    }

    public sealed record NotFound : DeleteOutcome
    {
        public override void Apply(ArmRequestContext context) => context.StatusCode = 204;
    }

    public sealed record Accepted(string OperationId, TimeSpan RetryAfter) : DeleteOutcome
    {
        public override void Apply(ArmRequestContext context)
        {
            context.StatusCode = 202;
            context.OperationId = OperationId;
            context.RetryAfter = RetryAfter;
        }
    }

    public sealed record Rejected(ArmError Error) : DeleteOutcome
    {
        public override void Apply(ArmRequestContext context) => context.Error = Error;
    }

    public abstract void Apply(ArmRequestContext context);
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(Ok), "ok")]
[JsonDerivedType(typeof(NoContent), "noContent")]
[JsonDerivedType(typeof(Accepted), "accepted")]
[JsonDerivedType(typeof(Rejected), "rejected")]
public abstract record ActionOutcome : IOutcomeView
{
    public sealed record Ok(JsonNode? Body) : ActionOutcome
    {
        public override void Apply(ArmRequestContext context)
        {
            context.StatusCode = 200;
            context.Result = Body;
        }
    }

    public sealed record NoContent : ActionOutcome
    {
        public override void Apply(ArmRequestContext context) => context.StatusCode = 204;
    }

    public sealed record Accepted(string OperationId, TimeSpan RetryAfter, JsonNode? Body) : ActionOutcome
    {
        public override void Apply(ArmRequestContext context)
        {
            context.StatusCode = 202;
            context.Result = Body;
            context.OperationId = OperationId;
            context.RetryAfter = RetryAfter;
        }
    }

    public sealed record Rejected(ArmError Error) : ActionOutcome
    {
        public override void Apply(ArmRequestContext context) => context.Error = Error;
    }

    public abstract void Apply(ArmRequestContext context);
}

public interface IResourceHandler<TProps> where TProps : class
{
    ValueTask<ResourceEnvelope<TProps>?> GetAsync(ResourceRequest req, CancellationToken ct);
    ValueTask<PutOutcome<TProps>> PutAsync(ResourceRequest req, TProps desired, CancellationToken ct);
    ValueTask<PutOutcome<TProps>> PatchAsync(ResourceRequest req, ResourcePatch patch, CancellationToken ct);
    ValueTask<DeleteOutcome> DeleteAsync(ResourceRequest req, CancellationToken ct);
    ValueTask<PagedResult<ResourceEnvelope<TProps>>> ListAsync(ResourceListRequest req, CancellationToken ct);
    ValueTask<ActionOutcome> InvokeAsync(ResourceActionRequest req, CancellationToken ct);
}

public sealed record ResourceStoreQuery(
    string? SubscriptionId,
    string? ResourceGroup,
    string? Namespace,
    string? Type,
    int Top,
    string? ContinuationToken,
    string? IdPrefix = null,
    ArmListFilter? Filter = null);

public sealed record ArmListFilter(
    string? NameEq,
    string? NameStartsWith,
    string? TagKey,
    string? TagEq,
    string? PropertyKey,
    string? PropertyEq);

public sealed record SkipToken(DateTimeOffset CreatedAt, string Id);

public interface IResourceStore
{
    /// <summary>Atomically creates an absent key. Existing keys fail with 412; unsupported adapters must refuse, never upsert.</summary>
    ValueTask<ResourceEnvelope<JsonObject>> CreateAsync(ResourceEnvelope<JsonObject> envelope, CancellationToken ct) =>
        throw new NotSupportedException("This resource adapter does not support atomic create.");
    ValueTask<ResourceEnvelope<JsonObject>?> GetAsync(string key, CancellationToken ct);
    ValueTask<ResourceEnvelope<JsonObject>> UpsertAsync(ResourceEnvelope<JsonObject> envelope, string? ifMatchETag, CancellationToken ct);
    ValueTask DeleteAsync(string key, string? ifMatchETag, CancellationToken ct);
    ValueTask<PagedResult<ResourceEnvelope<JsonObject>>> ListAsync(ResourceStoreQuery query, CancellationToken ct);
}
