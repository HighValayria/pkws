using Microsoft.Data.Sqlite;

namespace Pkws.Core;

public sealed class CaptureLedger
{
    private readonly string _connectionString;

    public CaptureLedger(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath) ?? throw new ArgumentException("数据库路径缺少目录。", nameof(databasePath));
        Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version INTEGER PRIMARY KEY,
                applied_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS captures (
                request_id TEXT PRIMARY KEY,
                entry_id TEXT NOT NULL UNIQUE,
                payload_hash TEXT NOT NULL,
                schema_version INTEGER NOT NULL,
                source_type TEXT NOT NULL,
                submitted_content TEXT NOT NULL,
                source_context_json TEXT NULL,
                received_at TEXT NOT NULL,
                created_at TEXT NOT NULL,
                timezone TEXT NOT NULL,
                local_date TEXT NOT NULL,
                status TEXT NOT NULL,
                relative_path TEXT NOT NULL,
                rendered_markdown TEXT NOT NULL,
                file_hash_after TEXT NULL,
                committed_at TEXT NULL,
                error_code TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_captures_status ON captures(status);
            INSERT OR IGNORE INTO schema_migrations(version, applied_at) VALUES (1, $applied_at);
            """;
        command.Parameters.AddWithValue("$applied_at", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<CaptureLedgerRecord?> GetAsync(string requestId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM captures WHERE request_id = $request_id";
        command.Parameters.AddWithValue("$request_id", requestId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRecord(reader) : null;
    }

    public async Task<CaptureLedgerRecord?> GetByEntryIdAsync(string entryId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM captures WHERE entry_id = $entry_id";
        command.Parameters.AddWithValue("$entry_id", entryId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRecord(reader) : null;
    }

    public async Task<IReadOnlyList<CaptureLedgerRecord>> GetRecoverableAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<CaptureLedgerRecord>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM captures WHERE status IN ('received', 'writing', 'recoverable') ORDER BY received_at";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(ReadRecord(reader));
        }

        return result;
    }

    public async Task InsertReceivedAsync(CaptureLedgerRecord record, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO captures(
                request_id, entry_id, payload_hash, schema_version, source_type,
                submitted_content, source_context_json, received_at, created_at,
                timezone, local_date, status, relative_path, rendered_markdown)
            VALUES(
                $request_id, $entry_id, $payload_hash, $schema_version, $source_type,
                $submitted_content, $source_context_json, $received_at, $created_at,
                $timezone, $local_date, 'received', $relative_path, $rendered_markdown)
            """;
        AddRecordParameters(command, record);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task MarkWritingAsync(string requestId, CancellationToken cancellationToken = default) =>
        UpdateStateAsync(requestId, "writing", null, null, null, cancellationToken);

    public Task MarkCommittedAsync(
        string requestId,
        string fileHashAfter,
        DateTimeOffset committedAt,
        CancellationToken cancellationToken = default) =>
        UpdateStateAsync(requestId, "committed", null, fileHashAfter, committedAt, cancellationToken);

    public Task MarkRecoverableAsync(string requestId, string errorCode, CancellationToken cancellationToken = default) =>
        UpdateStateAsync(requestId, "recoverable", errorCode, null, null, cancellationToken);

    public Task MarkFailedAsync(string requestId, string errorCode, CancellationToken cancellationToken = default) =>
        UpdateStateAsync(requestId, "failed", errorCode, null, null, cancellationToken);

    private async Task UpdateStateAsync(
        string requestId,
        string status,
        string? errorCode,
        string? fileHashAfter,
        DateTimeOffset? committedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            UPDATE captures
            SET status = $status,
                error_code = $error_code,
                file_hash_after = COALESCE($file_hash_after, file_hash_after),
                committed_at = COALESCE($committed_at, committed_at)
            WHERE request_id = $request_id
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$error_code", (object?)errorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$file_hash_after", (object?)fileHashAfter ?? DBNull.Value);
        command.Parameters.AddWithValue("$committed_at", committedAt?.ToString("O") is { } value ? value : DBNull.Value);
        command.Parameters.AddWithValue("$request_id", requestId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static void AddRecordParameters(SqliteCommand command, CaptureLedgerRecord record)
    {
        command.Parameters.AddWithValue("$request_id", record.RequestId);
        command.Parameters.AddWithValue("$entry_id", record.EntryId);
        command.Parameters.AddWithValue("$payload_hash", record.PayloadHash);
        command.Parameters.AddWithValue("$schema_version", record.SchemaVersion);
        command.Parameters.AddWithValue("$source_type", record.SourceType);
        command.Parameters.AddWithValue("$submitted_content", record.SubmittedContent);
        command.Parameters.AddWithValue("$source_context_json", (object?)record.SourceContextJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$received_at", record.ReceivedAt.ToString("O"));
        command.Parameters.AddWithValue("$created_at", record.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$timezone", record.TimeZone);
        command.Parameters.AddWithValue("$local_date", record.LocalDate.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("$relative_path", record.RelativePath);
        command.Parameters.AddWithValue("$rendered_markdown", record.RenderedMarkdown);
    }

    private static CaptureLedgerRecord ReadRecord(SqliteDataReader reader) => new()
    {
        RequestId = reader.GetString(reader.GetOrdinal("request_id")),
        EntryId = reader.GetString(reader.GetOrdinal("entry_id")),
        PayloadHash = reader.GetString(reader.GetOrdinal("payload_hash")),
        SchemaVersion = reader.GetInt32(reader.GetOrdinal("schema_version")),
        SourceType = reader.GetString(reader.GetOrdinal("source_type")),
        SubmittedContent = reader.GetString(reader.GetOrdinal("submitted_content")),
        SourceContextJson = GetNullableString(reader, "source_context_json"),
        ReceivedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("received_at"))),
        CreatedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
        TimeZone = reader.GetString(reader.GetOrdinal("timezone")),
        LocalDate = DateOnly.Parse(reader.GetString(reader.GetOrdinal("local_date"))),
        Status = reader.GetString(reader.GetOrdinal("status")),
        RelativePath = reader.GetString(reader.GetOrdinal("relative_path")),
        RenderedMarkdown = reader.GetString(reader.GetOrdinal("rendered_markdown")),
        FileHashAfter = GetNullableString(reader, "file_hash_after"),
        CommittedAt = GetNullableString(reader, "committed_at") is { } committed ? DateTimeOffset.Parse(committed) : null,
        ErrorCode = GetNullableString(reader, "error_code")
    };

    private static string? GetNullableString(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }
}
