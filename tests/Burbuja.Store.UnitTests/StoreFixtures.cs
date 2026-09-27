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
using Burbuja.Core.Redaction;
using Burbuja.Plugins.Store.Sqlite;

namespace Burbuja.Store.UnitTests;

public sealed record SampleDecision(string Verdict, int Score);

/// <summary>Mutable clock so lease expiry and retry windows can be forced deterministically.</summary>
public sealed class TestClock(DateTimeOffset start) : IClock
{
    private DateTimeOffset _now = start;

    public TestClock()
        : this(DateTimeOffset.Parse("2026-09-10T12:00:00+00:00", System.Globalization.CultureInfo.InvariantCulture))
    {
    }

    public DateTimeOffset UtcNow => _now;

    public void Advance(TimeSpan by) => _now += by;

    public void Set(DateTimeOffset value) => _now = value;
}

public interface IStoreFixture : IAsyncDisposable
{
    IResourceStore Resources { get; }
    IEventLog Events { get; }
    ILeaseStore Leases { get; }
    IOutboxStore Outbox { get; }
    IDecisionCache<SampleDecision> Decisions { get; }
    IArtifactStore Artifacts { get; }
    IOperationTracker Operations { get; }
    IIdempotencyStore Idempotency { get; }
    IApprovalStore Approvals { get; }
    TestClock Clock { get; }
}

public sealed class InMemoryStoreFixture : IStoreFixture
{
    private readonly string _root = Directory.CreateTempSubdirectory("pomnibot-store-mem-").FullName;
    private readonly IRedactor _redactor = new CaptureGroupRedactor();

    public TestClock Clock { get; } = new();

    public IResourceStore Resources { get; }

    public IEventLog Events { get; }

    public ILeaseStore Leases { get; }

    public IOutboxStore Outbox { get; } = new InMemoryOutbox();

    public IDecisionCache<SampleDecision> Decisions { get; } = new InMemoryDecisionCache<SampleDecision>();

    public IArtifactStore Artifacts { get; }

    public IOperationTracker Operations { get; }

    public IIdempotencyStore Idempotency { get; }

    public IApprovalStore Approvals { get; } = new InMemoryApprovalStore();

    public InMemoryStoreFixture()
    {
        Resources = new InMemoryResourceStore(Clock);
        Artifacts = new LocalFileArtifactStore(_root, _redactor);
        Events = new InMemoryEventLog(_redactor, Clock, 256, Artifacts);
        Leases = new InMemoryLeaseStore(Clock);
        Operations = new InMemoryOperationTracker(Clock);
        Idempotency = new InMemoryIdempotencyStore(Clock);
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }

        return default;
    }
}

public sealed class SqliteStoreFixture : IStoreFixture
{
    private readonly string _root = Directory.CreateTempSubdirectory("pomnibot-store-sqlite-").FullName;
    private readonly IRedactor _redactor = new CaptureGroupRedactor();
    private readonly SqliteDatabase _database;
    private readonly SqliteOutbox _outbox;

    public TestClock Clock { get; } = new();

    public string DatabasePath { get; }

    public IResourceStore Resources { get; }

    public IEventLog Events { get; }

    public ILeaseStore Leases { get; }

    public IOutboxStore Outbox => _outbox;

    public IDecisionCache<SampleDecision> Decisions { get; }

    public IArtifactStore Artifacts { get; }

    public IOperationTracker Operations { get; }

    public IIdempotencyStore Idempotency { get; }

    public IApprovalStore Approvals { get; }

    public SqliteStoreFixture()
    {
        DatabasePath = Path.Combine(_root, "pomnibot.db");
        _database = new SqliteDatabase(DatabasePath);
        _outbox = new SqliteOutbox(_database);
        Resources = new SqliteResourceStore(_database, Clock);
        Artifacts = new FileArtifactStore(Path.Combine(_root, "artifacts"), _redactor);
        Events = new SqliteEventLog(_database, _redactor, Clock, Artifacts);
        Leases = new SqliteLeaseStore(_database, Clock);
        Decisions = new SqliteDecisionCache<SampleDecision>(_database);
        Operations = new SqliteOperationTracker(_database, Clock);
        Idempotency = new SqliteIdempotencyStore(_database, Clock);
        Approvals = new SqliteApprovalStore(_database);
    }

    public async ValueTask DisposeAsync()
    {
        await _database.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
