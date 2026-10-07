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

namespace Burbuja.Plugins.Store.Sqlite;

public sealed class SqliteApplicationStorageProvider(IClock clock, IRedactor redactor) : IApplicationStorageProvider
{
    public string Id => "sqlite";
    public ApplicationStores Open(ApplicationStorageBinding binding) => new Stores(binding.StateDirectory, clock, redactor);

    private sealed class Stores : ApplicationStores
    {
        private readonly SqliteDatabase _database;
        private readonly FileStream _writer;
        public Stores(string directory, IClock clock, IRedactor redactor)
        {
            Directory.CreateDirectory(directory);
            // The designated kernel writer holds this handle for the entire binding lifetime.
            _writer = new FileStream(Path.Combine(directory, "kernel-writer.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            _database = new SqliteDatabase(Path.Combine(directory, "burbuja.db"), clock);
            Resources = new SqliteResourceStore(_database, clock);
            Artifacts = new FileArtifactStore(Path.Combine(directory, "artifacts"), redactor);
            Events = new SqliteEventLog(_database, redactor, clock, Artifacts);
            Leases = new SqliteLeaseStore(_database, clock);
            Outbox = new SqliteOutbox(_database);
            Idempotency = new SqliteIdempotencyStore(_database, clock);
            Approvals = new SqliteApprovalStore(_database);
            Operations = new SqliteOperationTracker(_database, clock);
            Decisions = new SqliteDecisionCache<JsonNode>(_database);
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
        public override void Dispose() { _database.Dispose(); _writer.Dispose(); }
    }
}
