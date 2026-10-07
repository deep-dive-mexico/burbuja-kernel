using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Approvals;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Artifacts;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Leases;
using Burbuja.Core.Abstractions.Outbox;
using Burbuja.Core.Abstractions.Store;

namespace Burbuja.Core.Abstractions.Applications;

/// <summary>Immutable authority resolved from registered credentials, never from a resource path.</summary>
public sealed record ApplicationScope(string Environment, string Tenant, string Application);

/// <summary>A server-owned persistence binding. Paths and database credentials never reach clients.</summary>
public sealed record ApplicationStorageBinding(ApplicationScope Scope, string Provider, string StateDirectory);

/// <summary>One coherent store set, with shared concurrency and recovery semantics within its binding.</summary>
public abstract class ApplicationStores : IDisposable
{
    public abstract IResourceStore Resources { get; }
    public abstract IEventLog Events { get; }
    public abstract IArtifactStore Artifacts { get; }
    public abstract ILeaseStore Leases { get; }
    public abstract IOutboxStore Outbox { get; }
    public abstract IIdempotencyStore Idempotency { get; }
    public abstract IApprovalStore Approvals { get; }
    public abstract IOperationTracker Operations { get; }
    public abstract IDecisionCache<JsonNode> Decisions { get; }
    public abstract void Dispose();
}

/// <summary>Backend adapter; a kernel factory selects it only from authenticated registration.</summary>
public interface IApplicationStorageProvider
{
    string Id { get; }
    ApplicationStores Open(ApplicationStorageBinding binding);
}

public sealed record ApplicationHandshake(string Protocol, string KernelVersion, ApplicationScope Scope,
    IReadOnlyList<string> Capabilities);

/// <summary>Actor is delegated audit identity, not a kernel role or a binding selector.</summary>
public sealed record ApplicationStorageCall(string Operation, JsonObject Arguments, string RequestId, string? Actor = null);
public sealed record ApplicationStorageReply(JsonNode? Value, ArmError? Error = null);
