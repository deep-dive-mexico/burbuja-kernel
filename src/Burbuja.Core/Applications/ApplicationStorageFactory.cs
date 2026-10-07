using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Burbuja.Core.Abstractions.Applications;
using Burbuja.Core.Abstractions.Approvals;
using Burbuja.Core.Abstractions.Arm;
using Burbuja.Core.Abstractions.Artifacts;
using Burbuja.Core.Abstractions.Clock;
using Burbuja.Core.Abstractions.Events;
using Burbuja.Core.Abstractions.Leases;
using Burbuja.Core.Abstractions.Outbox;
using Burbuja.Core.Abstractions.Redaction;
using Burbuja.Core.Abstractions.Store;
using Burbuja.Core.Arm;
using Burbuja.Core.InMemory;

namespace Burbuja.Core.Applications;

public sealed class ConnectedKernelOptions
{
    public bool Enabled { get; set; }
    public string Environment { get; set; } = "";
    public string StorageRoot { get; set; } = "";
    public string ControlProvider { get; set; } = "sqlite";
    public List<ApplicationRegistration> Applications { get; set; } = [];
}

public sealed class ApplicationRegistration
{
    public string Tenant { get; set; } = "";
    public string Application { get; set; } = "";
    public string Credential { get; set; } = "";
    public string Provider { get; set; } = "sqlite";
    public bool Writable { get; set; } = true;
    public List<string> WriteNamespaces { get; set; } = [];
    public List<string> EventPrefixes { get; set; } = [];
    public bool RuntimeEnabled { get; set; }
    public List<string> DelegatedRoles { get; set; } = ["Reader"];
    public decimal DailyHardStopUsd { get; set; } = 1m;
    /// <summary>
    /// The application hosts its own kernel runtime (a kernel-image host with the Store.Connected plugin) and
    /// the kernel only provides its isolated binding. Its runtime persists kernel-namespace resources, events
    /// and approvals inside that binding, so the reserved-namespace rules do not apply; it cannot combine with
    /// <see cref="RuntimeEnabled"/>, because the kernel then runs no authority for the scope that those writes
    /// could influence.
    /// </summary>
    public bool EmbeddedRuntime { get; set; }
}

/// <summary>One kernel owns each lazily opened binding for the lifetime of the service.</summary>
public sealed class ApplicationStorageFactory : IDisposable
{
    private sealed record Entry(byte[] CredentialHash, ApplicationScope Scope, bool Writable,
        IReadOnlySet<string> WriteNamespaces, IReadOnlySet<string> EventPrefixes, Lazy<ApplicationStores> Stores, bool Embedded);
    private readonly IReadOnlyList<Entry> _entries;
    public string Environment { get; }

    public ApplicationStorageFactory(ConnectedKernelOptions options, IEnumerable<IApplicationStorageProvider> providers)
    {
        Environment = options.Environment;
        if (string.IsNullOrWhiteSpace(Environment) || string.IsNullOrWhiteSpace(options.StorageRoot))
            throw new InvalidOperationException("Connected kernel requires an environment and server-owned storage root.");
        var adapters = providers.ToDictionary(provider => provider.Id, StringComparer.Ordinal);
        var identities = new HashSet<ApplicationScope>();
        var credentials = new HashSet<string>();
        var entries = new List<Entry>();
        foreach (var registration in options.Applications)
        {
            if (string.IsNullOrWhiteSpace(registration.Tenant) || string.IsNullOrWhiteSpace(registration.Application)
                || registration.Credential.Length < 32)
                throw new InvalidOperationException("Every application requires a tenant, identity and private credential (at least 32 characters).");
            var scope = new ApplicationScope(Environment, registration.Tenant, registration.Application);
            if (!identities.Add(scope) || !credentials.Add(registration.Credential))
                throw new InvalidOperationException("Duplicate application scope or credential.");
            if (!adapters.TryGetValue(registration.Provider, out var adapter))
                throw new InvalidOperationException("Unsupported application storage provider: " + registration.Provider);
            var writeNamespaces = new HashSet<string>(registration.WriteNamespaces, StringComparer.OrdinalIgnoreCase);
            var eventPrefixes = new HashSet<string>(registration.EventPrefixes, StringComparer.Ordinal);
            if (registration.EmbeddedRuntime)
            {
                if (registration.RuntimeEnabled || !registration.Writable)
                    throw new InvalidOperationException("An embedded-runtime application is writable and cannot also use the kernel's scoped runtime.");
            }
            else
            {
                string[] reserved = ["Burbuja.Agents", "Burbuja.Compute", "Burbuja.Resources", "Burbuja.Authorization",
                    "Burbuja.Connected", "Burbuja.Deployments", "Burbuja.Integrations"];
                if ((registration.Writable && writeNamespaces.Count == 0) || writeNamespaces.Any(ns =>
                    !System.Text.RegularExpressions.Regex.IsMatch(ns, "^[A-Za-z][A-Za-z0-9.]*$") || reserved.Contains(ns, StringComparer.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("Writable applications require explicit product namespaces; kernel namespaces are reserved.");
                string[] reservedEvents = ["agents.", "arm.", "approvals.", "compute.", "deployments.", "connected.", "kernel."];
                if ((registration.Writable && eventPrefixes.Count == 0) || eventPrefixes.Any(prefix =>
                    !System.Text.RegularExpressions.Regex.IsMatch(prefix, "^[A-Za-z][A-Za-z0-9.-]*\\.$") || reservedEvents.Any(reserved => prefix.StartsWith(reserved, StringComparison.Ordinal))))
                    throw new InvalidOperationException("Writable applications require explicit product event/topic prefixes; kernel prefixes are reserved.");
            }
            var identity = System.Text.Json.JsonSerializer.Serialize(scope);
            var directory = Path.Combine(Path.GetFullPath(options.StorageRoot), Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity))));
            var binding = new ApplicationStorageBinding(scope, adapter.Id, directory);
            entries.Add(new Entry(Hash(registration.Credential), scope, registration.Writable, writeNamespaces, eventPrefixes,
                new Lazy<ApplicationStores>(() => adapter.Open(binding), LazyThreadSafetyMode.ExecutionAndPublication), registration.EmbeddedRuntime));
        }
        _entries = entries;
    }

    public (ApplicationScope Scope, bool Writable, ApplicationStores Stores, IReadOnlySet<string> WriteNamespaces, IReadOnlySet<string> EventPrefixes, bool Embedded)? Authenticate(string credential)
    {
        var hash = Hash(credential);
        foreach (var entry in _entries)
            if (CryptographicOperations.FixedTimeEquals(hash, entry.CredentialHash))
                return (entry.Scope, entry.Writable, entry.Stores.Value, entry.WriteNamespaces, entry.EventPrefixes, entry.Embedded);
        return null;
    }

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    public void Dispose()
    {
        foreach (var entry in _entries)
            if (entry.Stores.IsValueCreated) entry.Stores.Value.Dispose();
    }
}

/// <summary>Test adapter using independent in-memory stores for each binding.</summary>
public sealed class InMemoryApplicationStorageProvider(IClock clock, IRedactor redactor) : IApplicationStorageProvider
{
    public string Id => "memory";
    public ApplicationStores Open(ApplicationStorageBinding binding) => new MemoryStores(clock, redactor, binding.StateDirectory);

    private sealed class MemoryStores : ApplicationStores
    {
        public MemoryStores(IClock clock, IRedactor redactor, string directory)
        {
            Resources = new InMemoryResourceStore(clock);
            Artifacts = new LocalFileArtifactStore(Path.Combine(directory, "artifacts"), redactor);
            Events = new InMemoryEventLog(redactor, clock, spill: Artifacts);
            Leases = new InMemoryLeaseStore(clock);
            Outbox = new InMemoryOutbox();
            Idempotency = new InMemoryIdempotencyStore(clock);
            Approvals = new InMemoryApprovalStore();
            Operations = new InMemoryOperationTracker(clock);
            Decisions = new InMemoryDecisionCache<JsonNode>();
        }
        public override IResourceStore Resources { get; }
        public override IEventLog Events { get; }
        public override IArtifactStore Artifacts { get; }
        public override ILeaseStore Leases { get; }
        public override IOutboxStore Outbox { get; }
        public override IIdempotencyStore Idempotency { get; }
        public override IApprovalStore Approvals { get; }
        public override IOperationTracker Operations { get; }
        public override IDecisionCache<JsonNode> Decisions { get; }
        public override void Dispose() { }
    }
}
