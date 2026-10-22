using System.Text;
using Microsoft.Data.Sqlite;

namespace DirPorter.Core.Ledger;

/// <summary>
/// SQLite 台账（单批次条目超过 <see cref="LedgerSchema.SqliteThreshold"/> 时自动切换）。
///
/// schema（表名固定，便于外部工具查询）：
/// <code>
/// batches(batch_id PK, started_at, completed_at, host, schema_version)
/// entries(rowid, batch_id, entry_id, source_path, parking_path, target_path, link_path,
///         link_type, volume_source, volume_target, checksum_method, checksum,
///         file_count, total_bytes, result, payload)
/// operations(rowid, batch_id, entry_id, step, at, status, detail)
/// </code>
/// <c>payload</c> 保存完整 entry JSON：即使将来新增字段，也能原样还给调用方，
/// 保证"台账是权威记录"这一点不会因 schema 变更而丢失信息。
/// </summary>
public sealed class SqliteMigrationLedger : IMigrationLedger
{
    private readonly object _writeLock = new();
    private readonly string _dbPath;

    public SqliteMigrationLedger(string ledgerDirectory, string batchId, string host, DateTimeOffset? startedAt = null)
    {
        LedgerDirectory = ledgerDirectory;
        BatchId = batchId;
        Directory.CreateDirectory(ledgerDirectory);

        _dbPath = Path.Combine(ledgerDirectory, batchId + ".db");

        EnsureSchema();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT OR IGNORE INTO batches(batch_id, started_at, completed_at, host, schema_version)
            VALUES ($id, $started, NULL, $host, $version);
            """;
        command.Parameters.AddWithValue("$id", batchId);
        command.Parameters.AddWithValue("$started", (startedAt ?? DateTimeOffset.Now).ToString("O"));
        command.Parameters.AddWithValue("$host", host);
        command.Parameters.AddWithValue("$version", LedgerSchema.Current);
        command.ExecuteNonQuery();
    }

    public string BatchId { get; }
    public string LedgerDirectory { get; }
    public LedgerStorageKind StorageKind => LedgerStorageKind.Sqlite;
    public string PrimaryFilePath => _dbPath;
    public string MarkdownSummaryPath => Path.Combine(LedgerDirectory, BatchId + ".md");

    public void AppendEvent(string entryId, string step, string status, string? detail = null)
    {
        lock (_writeLock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO operations(batch_id, entry_id, step, at, status, detail) VALUES ($b, $e, $s, $a, $st, $d);";
            command.Parameters.AddWithValue("$b", BatchId);
            command.Parameters.AddWithValue("$e", entryId);
            command.Parameters.AddWithValue("$s", step);
            command.Parameters.AddWithValue("$a", DateTimeOffset.Now.ToString("O"));
            command.Parameters.AddWithValue("$st", status);
            command.Parameters.AddWithValue("$d", (object?)detail ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
    }

    public void WriteEntry(LedgerEntry entry)
    {
        lock (_writeLock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO entries(batch_id, entry_id, source_path, parking_path, target_path, link_path,
                                    link_type, volume_source, volume_target, checksum_method, checksum,
                                    file_count, total_bytes, result, payload)
                VALUES ($b, $e, $sp, $pp, $tp, $lp, $lt, $vs, $vt, $cm, $cs, $fc, $tb, $r, $payload);
                """;
            command.Parameters.AddWithValue("$b", BatchId);
            command.Parameters.AddWithValue("$e", entry.EntryId);
            command.Parameters.AddWithValue("$sp", entry.SourcePath);
            command.Parameters.AddWithValue("$pp", entry.ParkingPath);
            command.Parameters.AddWithValue("$tp", entry.TargetPath);
            command.Parameters.AddWithValue("$lp", entry.LinkPath);
            command.Parameters.AddWithValue("$lt", entry.LinkType);
            command.Parameters.AddWithValue("$vs", entry.VolumeSource);
            command.Parameters.AddWithValue("$vt", entry.VolumeTarget);
            command.Parameters.AddWithValue("$cm", entry.ChecksumMethod);
            command.Parameters.AddWithValue("$cs", entry.Checksum);
            command.Parameters.AddWithValue("$fc", entry.FileCount);
            command.Parameters.AddWithValue("$tb", entry.TotalBytes);
            command.Parameters.AddWithValue("$r", entry.Result);
            command.Parameters.AddWithValue("$payload", LedgerJson.Serialize(entry));
            command.ExecuteNonQuery();
        }
    }

    public void AppendRestore(string entryId, string detail)
        => AppendEvent(entryId, "RESTORE", "ok", detail);

    public void CompleteBatch(string? archiveDirectory = null)
    {
        lock (_writeLock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE batches SET completed_at = $now WHERE batch_id = $id;";
            command.Parameters.AddWithValue("$now", DateTimeOffset.Now.ToString("O"));
            command.Parameters.AddWithValue("$id", BatchId);
            command.ExecuteNonQuery();
        }

        var batch = Read();
        File.WriteAllText(MarkdownSummaryPath, LedgerMarkdown.Build(batch), new UTF8Encoding(false));

        if (!string.IsNullOrWhiteSpace(archiveDirectory))
            LedgerArchive.CopyTo(LedgerDirectory, BatchId, archiveDirectory!);
    }

    public LedgerBatch Read()
    {
        using var connection = OpenConnection();

        var header = ReadHeader(connection) ?? new LedgerBatchHeader
        {
            BatchId = BatchId,
            Host = Environment.MachineName,
            StartedAt = DateTimeOffset.Now,
        };

        var entries = new List<LedgerEntry>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT payload FROM entries WHERE batch_id = $b ORDER BY rowid;";
            command.Parameters.AddWithValue("$b", BatchId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var payload = reader.GetString(0);
                var entry = LedgerJson.Deserialize<LedgerEntry>(payload);
                if (entry is not null) entries.Add(entry);
            }
        }

        var events = new List<LedgerEventRecord>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT entry_id, step, at, status, detail FROM operations WHERE batch_id = $b ORDER BY rowid;";
            command.Parameters.AddWithValue("$b", BatchId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                events.Add(new LedgerEventRecord
                {
                    BatchId = BatchId,
                    EntryId = reader.GetString(0),
                    Step = reader.GetString(1),
                    At = DateTimeOffset.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture),
                    Status = reader.GetString(3),
                    Detail = reader.IsDBNull(4) ? null : reader.GetString(4),
                });
            }
        }

        return new LedgerBatch(header, entries, events, Array.Empty<LedgerRestoreRecord>());
    }

    /// <summary>打开既有 db 文件（不新建）。</summary>
    public static SqliteMigrationLedger OpenExisting(string dbPath, string host = "")
    {
        var batchId = Path.GetFileNameWithoutExtension(dbPath);
        return new SqliteMigrationLedger(Path.GetDirectoryName(dbPath)!, batchId, host);
    }

    private LedgerBatchHeader? ReadHeader(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT batch_id, started_at, completed_at, host, schema_version FROM batches WHERE batch_id = $id;";
        command.Parameters.AddWithValue("$id", BatchId);

        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;

        return new LedgerBatchHeader
        {
            BatchId = reader.GetString(0),
            StartedAt = DateTimeOffset.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture),
            CompletedAt = reader.IsDBNull(2)
                ? null
                : DateTimeOffset.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture),
            Host = reader.GetString(3),
            SchemaVersion = reader.GetInt32(4),
        };
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());

        connection.Open();
        return connection;
    }

    private void EnsureSchema()
    {
        using var connection = OpenConnection();

        // WAL：写入更耐崩溃，且允许并发读（排障时能一边跑一边查）。
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;";
            pragma.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS batches(
                batch_id TEXT PRIMARY KEY, started_at TEXT NOT NULL, completed_at TEXT,
                host TEXT NOT NULL, schema_version INTEGER NOT NULL);

            CREATE TABLE IF NOT EXISTS entries(
                batch_id TEXT NOT NULL, entry_id TEXT NOT NULL, source_path TEXT NOT NULL,
                parking_path TEXT NOT NULL, target_path TEXT NOT NULL, link_path TEXT NOT NULL,
                link_type TEXT NOT NULL, volume_source TEXT NOT NULL, volume_target TEXT NOT NULL,
                checksum_method TEXT NOT NULL, checksum TEXT NOT NULL, file_count INTEGER NOT NULL,
                total_bytes INTEGER NOT NULL, result TEXT NOT NULL, payload TEXT NOT NULL);

            CREATE INDEX IF NOT EXISTS ix_entries_batch ON entries(batch_id);
            CREATE INDEX IF NOT EXISTS ix_entries_link ON entries(link_path);

            CREATE TABLE IF NOT EXISTS operations(
                batch_id TEXT NOT NULL, entry_id TEXT NOT NULL, step TEXT NOT NULL,
                at TEXT NOT NULL, status TEXT NOT NULL, detail TEXT);
            """;
        command.ExecuteNonQuery();
    }
}
