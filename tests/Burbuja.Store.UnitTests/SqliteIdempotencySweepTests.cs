using Burbuja.Core.Abstractions.Clock;
using Burbuja.Plugins.Store.Sqlite;
using Shouldly;
using Xunit;

namespace Burbuja.Store.UnitTests;

/// <summary>
/// M11 P6 — the bounded sweep of the idempotency table at boot, and the half of the cleanup the lazy purge
/// cannot do.
///
/// <para><b>What was wrong.</b> The only purge in the store lived inside <c>Put</c>: it ran when something
/// WROTE, and it removed rows that were already expired. A host whose reporter stopped — or, worse, whose
/// reporter is what filled the table — therefore kept every expired row forever and the file never shrank.
/// Measured on the M10 release host: the Discord gateway reported its state once a second, the ARM front
/// door stored every 2xx of it for 24 h, and the table went 5 → 28 124 rows while the database went
/// 294 KB → 16 MB in ten hours.</para>
///
/// <para><b>Why the fact reads raw SQL.</b> "The row is gone" has no behavioural proxy here: <c>Get</c>
/// already answers null for an expired row, so a fact that asked the store would pass with or without the
/// sweep. The only honest assertion is on what is physically in the table, which is also the only thing that
/// makes the file shrink.</para>
/// </summary>
public sealed class SqliteIdempotencySweepTests : IAsyncLifetime
{
    private readonly string _root = Directory.CreateTempSubdirectory("pomnibot-idem-sweep-").FullName;
    private readonly TestClock _clock = new();
    private string _databasePath = null!;

    public ValueTask InitializeAsync()
    {
        _databasePath = Path.Combine(_root, "pomnibot.db");
        return default;
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnectionPools.Clear();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory that will not delete is not this fact's business.
        }

        return default;
    }

    private const string ExpiredRow =
        "INSERT INTO idempotency(key, expires_at, payload) VALUES ($key, $expires, '{}')";

    private void Insert(SqliteDatabase db, string key, DateTimeOffset expires) =>
        db.Write<object?>((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = ExpiredRow;
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$expires", expires.ToString("O"));
            command.ExecuteNonQuery();
            return null;
        });

    private long Count(SqliteDatabase db) =>
        db.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM idempotency";
            return Convert.ToInt64(command.ExecuteScalar());
        });

    [Fact]
    public async Task The_boot_sweep_removes_expired_rows_and_leaves_live_ones()
    {
        // First boot: three rows already expired, two still live. Written with no clock on the database, so
        // nothing sweeps yet — this is the "a previous version let it grow" state.
        await using (var seeding = new SqliteDatabase(_databasePath))
        {
            for (var i = 0; i < 3; i++)
                Insert(seeding, $"expired-{i}", _clock.UtcNow.AddMinutes(-5));
            for (var i = 0; i < 2; i++)
                Insert(seeding, $"live-{i}", _clock.UtcNow.AddHours(4));

            Count(seeding).ShouldBe(5);
        }

        // Second boot, with the clock the store compares against. `EnsureInitializedAsync` is the boot hook
        // and runs on the first read, so the read is what triggers the sweep.
        await using var reopened = new SqliteDatabase(_databasePath, _clock);
        _ = reopened.Read(connection => 0);

        Count(reopened).ShouldBe(2, "the expired rows go at boot; the live ones are the cache and stay");

        // And the rows that survived are the right two, not just the right count.
        reopened.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT key FROM idempotency ORDER BY key";
            using var reader = command.ExecuteReader();
            var keys = new List<string>();
            while (reader.Read())
                keys.Add(reader.GetString(0));
            keys.ShouldBe(["live-0", "live-1"]);
            return 0;
        });
    }

    /// <summary>
    /// The sweep is BOUNDED: one boot removes at most <see cref="SqliteDatabase.SweepBatch"/> rows and the
    /// rest wait for the next boot or the next write. The bound is what keeps "open the database" from
    /// becoming a long transaction on a file a previous version let grow for months — and it is asserted
    /// rather than asserted-about, because a bound nobody tests is a bound that quietly becomes unbounded.
    /// </summary>
    [Fact]
    public async Task The_boot_sweep_is_bounded_and_leaves_the_rest_for_the_next_one()
    {
        const int over = SqliteDatabase.SweepBatch + 250;

        await using (var seeding = new SqliteDatabase(_databasePath))
        {
            seeding.Write<object?>((connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = ExpiredRow;
                command.Parameters.AddWithValue("$expires", _clock.UtcNow.AddMinutes(-5).ToString("O"));
                var key = command.CreateParameter();
                key.ParameterName = "$key";
                command.Parameters.Add(key);
                for (var i = 0; i < over; i++)
                {
                    key.Value = $"bulk-{i:D6}";
                    command.ExecuteNonQuery();
                }

                return null;
            });

            Count(seeding).ShouldBe(over);
        }

        await using var first = new SqliteDatabase(_databasePath, _clock);
        _ = first.Read(connection => 0);
        Count(first).ShouldBe(250, "one boot sweeps at most SweepBatch rows");

        // A second boot — the same host restarted, which is the normal shape of this — takes the rest.
        await using var second = new SqliteDatabase(_databasePath, _clock);
        _ = second.Read(connection => 0);
        Count(second).ShouldBe(0);
    }
}

/// <summary>A tiny seam so the fact can drop pooled handles before deleting its temp directory.</summary>
internal static class SqliteConnectionPools
{
    public static void Clear() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
}
