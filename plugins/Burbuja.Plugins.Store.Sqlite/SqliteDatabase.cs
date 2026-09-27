using Microsoft.Data.Sqlite;

namespace Burbuja.Plugins.Store.Sqlite;

/// <summary>
/// One SQLite file, WAL, serialized writer. The schema is created idempotently; a plugin
/// instance owns one database and hands connections to the stores.
/// </summary>
/// <param name="path">The file.</param>
/// <param name="clock">
/// The clock the boot sweep reads, when the caller has one. Optional so the eight existing call sites —
/// most of them tests — keep working, and so a database that no store ever asks to sweep needs no clock at
/// all. It must be the SAME clock the idempotency store compares expiries with, or a test that advances a
/// fake clock forward would find the boot sweep has already deleted the rows it was about to assert on.
/// </param>
public sealed class SqliteDatabase(string path, Burbuja.Core.Abstractions.Clock.IClock? clock = null) : IAsyncDisposable, IDisposable
{
    private readonly SqliteConnectionStringBuilder _builder = new()
    {
        DataSource = path,
        Cache = SqliteCacheMode.Shared,
        Pooling = true,
    };

    private readonly SemaphoreSlim _initGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private bool _initialized;
    private bool _disposed;

    public string Path { get; } = path;

    public async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized)
            return;
        await _initGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized)
                return;
            var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            SQLitePCL.Batteries_V2.Init();
            await using var connection = await OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = Schema;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await MigrateAsync(connection, ct).ConfigureAwait(false);
            await SweepIdempotencyAsync(connection, ct).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initGate.Release();
        }
    }

    /// <summary>
    /// How many expired idempotency rows one boot sweep removes. Bounded so a host that opens a database a
    /// previous version let grow without limit cannot spend a minute in boot on the delete.
    /// </summary>
    public const int SweepBatch = 5000;

    /// <summary>
    /// M11 P6 — the bounded sweep at boot, and the half of the cleanup the lazy purge in
    /// <c>SqliteIdempotencyStore.Put</c> cannot do.
    ///
    /// <para><b>Why it is needed.</b> The lazy purge only runs when something WRITES, and it only removes
    /// rows that are already expired. A host whose reporter stopped — or whose reporter is what filled the
    /// table — therefore keeps every expired row forever, and the file never shrinks. Measured on the M10
    /// release host: the table went 5 → 28 124 rows in ten hours and the database 294 KB → 16 MB, all of it
    /// responses to a per-second state report.</para>
    ///
    /// <para><b>Why it is bounded and why it is here.</b> Boot is the one moment nothing is waiting on the
    /// store, so it is the right place for a delete that may have a lot of rows to chew through; the LIMIT
    /// is what keeps "a lot" from becoming "all of them at once" on a file that has been growing for
    /// months. This is the store plugin's own table and its own boot hook, so the sweep needs no hosted
    /// service and the plugin keeps its "no trust entry" property.</para>
    /// </summary>
    private async Task SweepIdempotencyAsync(SqliteConnection connection, CancellationToken ct)
    {
        var now = (clock?.UtcNow ?? DateTimeOffset.UtcNow).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        await using var sweep = connection.CreateCommand();
        sweep.CommandText =
            "DELETE FROM idempotency WHERE key IN "
            + "(SELECT key FROM idempotency WHERE expires_at <= $now LIMIT $batch)";
        sweep.Parameters.AddWithValue("$now", now);
        sweep.Parameters.AddWithValue("$batch", SweepBatch);
        await sweep.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Current schema version this build writes.</summary>
    public const int SchemaVersion = 1;

    /// <summary>
    /// H1: the 0 → 1 migration. A database created before the chain existed has the three columns only
    /// because <c>CREATE TABLE IF NOT EXISTS</c> did nothing to it, so they are added here and
    /// <b>backfilled</b> — an unmigrated row would carry <c>stream_seq = 0</c> and a null hash, and a
    /// verifier reading it would report every pre-existing event as tampered. Backfilling runs the chain
    /// forward from the oldest event of each stream, which is the only order in which it can be computed.
    ///
    /// <para>The version is <c>PRAGMA user_version</c>, which SQLite keeps in the file header: it needs no
    /// table of its own and cannot drift out of sync with the schema it describes.</para>
    /// </summary>
    private async Task MigrateAsync(SqliteConnection connection, CancellationToken ct)
    {
        var version = await UserVersionAsync(connection, ct).ConfigureAwait(false);
        if (version >= SchemaVersion)
            return;

        if (version < 1)
        {
            foreach (var ddl in new[]
            {
                "ALTER TABLE events ADD COLUMN stream_seq INTEGER NOT NULL DEFAULT 0",
                "ALTER TABLE events ADD COLUMN previous_hash TEXT",
                "ALTER TABLE events ADD COLUMN hash TEXT",
            })
            {
                try
                {
                    await using var alter = connection.CreateCommand();
                    alter.CommandText = ddl;
                    await alter.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 1 && ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase))
                {
                    // Already there: the table was created by this build's Schema, which includes the
                    // columns, and only the version stamp is behind.
                }
            }

            // After the columns, never before: see the note in Schema.
            await using (var index = connection.CreateCommand())
            {
                index.CommandText = "CREATE INDEX IF NOT EXISTS ix_events_stream_seq ON events(stream_id, stream_seq)";
                await index.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await BackfillChainAsync(connection, ct).ConfigureAwait(false);
        }

        await using var stamp = connection.CreateCommand();
        stamp.CommandText = $"PRAGMA user_version = {SchemaVersion}";
        await stamp.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<int> UserVersionAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0);
    }

    private async Task BackfillChainAsync(SqliteConnection connection, CancellationToken ct)
    {
        // Stream by stream, oldest first. A single pass over `seq` would interleave streams and compute
        // each link against the wrong predecessor.
        var streams = new List<string>();
        await using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT DISTINCT stream_id FROM events";
            await using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                streams.Add(reader.GetString(0));
        }

        foreach (var stream in streams)
        {
            string? previous = null;
            long streamSeq = 0;
            var updates = new List<(long Seq, long StreamSeq, string Previous, string Hash)>();
            await using (var read = connection.CreateCommand())
            {
                read.CommandText = "SELECT seq, stream_id, sub_stream_id, type, summary, payload, lanes, subject, value, stream_type, at FROM events WHERE stream_id = $stream ORDER BY seq";
                read.Parameters.AddWithValue("$stream", stream);
                await using var reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var chained = MapRow(reader) with { StreamSeq = streamSeq + 1, PreviousHash = previous ?? Burbuja.Core.Abstractions.Events.EventChain.Genesis };
                    var hash = Burbuja.Core.Abstractions.Events.EventChain.HashOf(chained);
                    streamSeq++;
                    updates.Add((chained.Seq, streamSeq, chained.PreviousHash!, hash));
                    previous = hash;
                }
            }

            foreach (var (seq, sseq, prev, hash) in updates)
            {
                await using var update = connection.CreateCommand();
                update.CommandText = "UPDATE events SET stream_seq = $sseq, previous_hash = $prev, hash = $hash WHERE seq = $seq";
                update.Parameters.AddWithValue("$sseq", sseq);
                update.Parameters.AddWithValue("$prev", prev);
                update.Parameters.AddWithValue("$hash", hash);
                update.Parameters.AddWithValue("$seq", seq);
                await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// One event row, by column position. Lives here rather than in the event log because the migration
    /// has to read rows the same way the log does — a second mapper would be a second definition of what
    /// a row means, and the chain is computed over exactly that.
    /// </summary>
    internal static Burbuja.Core.Abstractions.Events.LedgerEvent MapRow(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.IsDBNull(5) ? null : System.Text.Json.Nodes.JsonNode.Parse(reader.GetString(5)),
        (Burbuja.Core.Abstractions.Events.RenderLane)reader.GetInt32(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.IsDBNull(8) ? null : decimal.Parse(reader.GetString(8), System.Globalization.CultureInfo.InvariantCulture),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        DateTimeOffset.Parse(reader.GetString(10), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind));

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_builder.ToString());
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
        await pragma.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return connection;
    }

    public async Task<T> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, Task<T>> work, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(ct).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            var result = await work(connection, transaction).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<T> ReadAsync<T>(Func<SqliteConnection, Task<T>> work, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        return await work(connection).ConfigureAwait(false);
    }

    /// <summary>Synchronous open for the sync-seamed stores (idempotency, approvals).</summary>
    public SqliteConnection Open()
    {
        EnsureInitializedAsync(CancellationToken.None).GetAwaiter().GetResult();
        var connection = new SqliteConnection(_builder.ToString());
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    public T Write<T>(Func<SqliteConnection, SqliteTransaction, T> work)
    {
        EnsureInitializedAsync(CancellationToken.None).GetAwaiter().GetResult();
        _writeGate.Wait();
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            var result = work(connection, transaction);
            transaction.Commit();
            return result;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public T Read<T>(Func<SqliteConnection, T> work)
    {
        EnsureInitializedAsync(CancellationToken.None).GetAwaiter().GetResult();
        using var connection = Open();
        return work(connection);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _initGate.Dispose();
        _writeGate.Dispose();
        SqliteConnection.ClearAllPools();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return default;
    }

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS resources(
            key TEXT PRIMARY KEY,
            id TEXT NOT NULL,
            name TEXT NOT NULL,
            type TEXT NOT NULL,
            location TEXT,
            tags TEXT NOT NULL,
            properties TEXT NOT NULL,
            provisioning_state INTEGER NOT NULL,
            system_data TEXT NOT NULL,
            etag TEXT NOT NULL,
            version INTEGER NOT NULL,
            created_at TEXT NOT NULL);

        CREATE INDEX IF NOT EXISTS ix_resources_type ON resources(type);
        CREATE INDEX IF NOT EXISTS ix_resources_sub ON resources(created_at, id);

        CREATE TABLE IF NOT EXISTS events(
            seq INTEGER PRIMARY KEY AUTOINCREMENT,
            stream_id TEXT NOT NULL,
            sub_stream_id TEXT,
            type TEXT NOT NULL,
            summary TEXT NOT NULL,
            payload TEXT,
            lanes INTEGER NOT NULL,
            subject TEXT,
            value TEXT,
            stream_type TEXT,
            at TEXT NOT NULL,
            -- H1: the tamper-evident chain. `stream_seq` is the event's dense position within its
            -- stream and `previous_hash` links to that stream's previous event; `hash` covers this one's
            -- canonical form. Defaulted so a database written before this migration still opens.
            stream_seq INTEGER NOT NULL DEFAULT 0,
            previous_hash TEXT,
            hash TEXT);

        CREATE INDEX IF NOT EXISTS ix_events_stream ON events(stream_id, seq);
        -- NOTE: the index over `stream_seq` is created by the MIGRATION, not here. On a database written
        -- before the chain existed, `CREATE TABLE IF NOT EXISTS` is a no-op and this batch would then try
        -- to index a column the ALTER has not added yet — SQLite fails the whole batch, the migration
        -- never runs, and every read afterwards dies with `no such column: stream_seq`. Found by the
        -- pre-chain migration fact.

        CREATE TABLE IF NOT EXISTS leases(
            resource TEXT PRIMARY KEY,
            owner TEXT NOT NULL,
            fencing_token TEXT NOT NULL,
            expires_at TEXT NOT NULL);

        -- Fence counters outlive the lease row itself, so releasing and re-acquiring
        -- always moves the fencing token forward.
        CREATE TABLE IF NOT EXISTS lease_fences(
            resource TEXT PRIMARY KEY,
            fence INTEGER NOT NULL);

        CREATE TABLE IF NOT EXISTS outbox(
            message_id TEXT PRIMARY KEY,
            aggregate_id TEXT NOT NULL,
            topic TEXT NOT NULL,
            payload TEXT NOT NULL,
            status INTEGER NOT NULL,
            attempts INTEGER NOT NULL,
            next_attempt_at TEXT NOT NULL,
            dead_reason TEXT);

        CREATE TABLE IF NOT EXISTS decision_cache(
            stream_id TEXT NOT NULL,
            decision_key TEXT NOT NULL,
            value TEXT NOT NULL,
            PRIMARY KEY(stream_id, decision_key));

        -- M4.1: idempotency keys, approvals + grants, and operation tracking survive a restart.
        CREATE TABLE IF NOT EXISTS idempotency(
            key TEXT PRIMARY KEY,
            expires_at TEXT NOT NULL,
            payload TEXT NOT NULL);

        CREATE TABLE IF NOT EXISTS approvals(
            id TEXT PRIMARY KEY,
            opened_at TEXT NOT NULL,
            payload TEXT NOT NULL);

        CREATE TABLE IF NOT EXISTS approval_grants(
            grant_key TEXT PRIMARY KEY,
            expires_at TEXT NOT NULL,
            payload TEXT NOT NULL);

        CREATE TABLE IF NOT EXISTS operations(
            operation_id TEXT PRIMARY KEY,
            id TEXT NOT NULL,
            start_time TEXT NOT NULL,
            payload TEXT NOT NULL);

        CREATE INDEX IF NOT EXISTS ix_operations_id ON operations(id);
        """;
}
