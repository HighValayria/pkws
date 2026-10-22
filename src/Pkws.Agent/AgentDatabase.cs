using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Pkws.Agent;

public sealed class AgentDatabase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _connectionString;
    private readonly string _indexDatabasePath;

    public AgentDatabase(string databasePath, string? indexDatabasePath = null)
    {
        var directory = Path.GetDirectoryName(databasePath) ?? throw new ArgumentException("数据库路径缺少目录。", nameof(databasePath));
        Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString();
        _indexDatabasePath = Path.GetFullPath(indexDatabasePath ?? Path.Combine(directory, "index.db"));
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

            CREATE TABLE IF NOT EXISTS source_revisions (
                revision_id TEXT PRIMARY KEY,
                source_key TEXT NOT NULL,
                entry_id TEXT NULL,
                file_path TEXT NOT NULL,
                content TEXT NOT NULL,
                content_hash TEXT NOT NULL,
                source_type TEXT NOT NULL,
                created_at TEXT NULL,
                source_context_json TEXT NULL,
                observed_at TEXT NOT NULL,
                predecessor_id TEXT NULL,
                active INTEGER NOT NULL,
                FOREIGN KEY(predecessor_id) REFERENCES source_revisions(revision_id)
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_source_active
                ON source_revisions(source_key) WHERE active = 1;
            CREATE INDEX IF NOT EXISTS ix_source_entry ON source_revisions(entry_id);
            CREATE INDEX IF NOT EXISTS ix_source_file ON source_revisions(file_path);

            CREATE TABLE IF NOT EXISTS fragments (
                fragment_id TEXT PRIMARY KEY,
                source_revision_id TEXT NOT NULL,
                ordinal INTEGER NOT NULL,
                text TEXT NOT NULL,
                text_hash TEXT NOT NULL,
                start_offset INTEGER NOT NULL,
                end_offset INTEGER NOT NULL,
                topics_json TEXT NOT NULL DEFAULT '[]',
                entities_json TEXT NOT NULL DEFAULT '[]',
                epistemic_status TEXT NOT NULL DEFAULT 'unknown',
                action TEXT NOT NULL DEFAULT 'index_only',
                extractor_version TEXT NOT NULL,
                analysis_version TEXT NULL,
                active INTEGER NOT NULL,
                UNIQUE(source_revision_id, ordinal, text_hash),
                FOREIGN KEY(source_revision_id) REFERENCES source_revisions(revision_id)
            );
            CREATE INDEX IF NOT EXISTS ix_fragments_source ON fragments(source_revision_id);
            CREATE INDEX IF NOT EXISTS ix_fragments_active ON fragments(active, analysis_version);

            CREATE TABLE IF NOT EXISTS topics (
                topic_id TEXT PRIMARY KEY,
                name TEXT NOT NULL COLLATE NOCASE UNIQUE,
                aliases_json TEXT NOT NULL DEFAULT '[]',
                direction TEXT NULL,
                origin TEXT NOT NULL,
                status TEXT NOT NULL,
                pinned INTEGER NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS articles (
                article_id TEXT PRIMARY KEY,
                relative_path TEXT NOT NULL UNIQUE,
                title TEXT NOT NULL,
                status TEXT NOT NULL,
                maintenance_mode TEXT NOT NULL,
                current_hash TEXT NOT NULL,
                revision_number INTEGER NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS article_revisions (
                revision_id TEXT PRIMARY KEY,
                article_id TEXT NOT NULL,
                revision_number INTEGER NOT NULL,
                content TEXT NOT NULL,
                content_hash TEXT NOT NULL,
                actor TEXT NOT NULL,
                source_revision_ids_json TEXT NOT NULL,
                change_id TEXT NULL,
                created_at TEXT NOT NULL,
                UNIQUE(article_id, revision_number),
                FOREIGN KEY(article_id) REFERENCES articles(article_id)
            );

            CREATE TABLE IF NOT EXISTS proposals (
                proposal_id TEXT PRIMARY KEY,
                idempotency_key TEXT NOT NULL UNIQUE,
                article_id TEXT NULL,
                target_path TEXT NOT NULL,
                proposal_type TEXT NOT NULL,
                risk_level TEXT NOT NULL,
                status TEXT NOT NULL,
                base_hash TEXT NULL,
                title TEXT NOT NULL,
                rationale TEXT NOT NULL,
                proposed_content TEXT NOT NULL,
                source_revision_ids_json TEXT NOT NULL,
                created_at TEXT NOT NULL,
                reviewed_at TEXT NULL,
                applied_at TEXT NULL,
                result_hash TEXT NULL,
                error_code TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_proposals_status ON proposals(status, created_at);

            CREATE TABLE IF NOT EXISTS runs (
                run_id TEXT PRIMARY KEY,
                run_type TEXT NOT NULL,
                status TEXT NOT NULL,
                started_at TEXT NOT NULL,
                finished_at TEXT NULL,
                items_processed INTEGER NOT NULL DEFAULT 0,
                proposals_created INTEGER NOT NULL DEFAULT 0,
                error_code TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS codex_worksets (
                workset_id TEXT PRIMARY KEY,
                run_type TEXT NOT NULL,
                topic_name TEXT NULL,
                status TEXT NOT NULL,
                fragment_ids_json TEXT NOT NULL,
                source_revision_ids_json TEXT NOT NULL,
                article_baselines_json TEXT NOT NULL,
                created_at TEXT NOT NULL,
                completed_at TEXT NULL,
                submission_hash TEXT NULL,
                proposal_ids_json TEXT NOT NULL DEFAULT '[]',
                error_code TEXT NULL,
                UNIQUE(submission_hash)
            );
            CREATE INDEX IF NOT EXISTS ix_codex_worksets_status ON codex_worksets(status, created_at);

            CREATE TABLE IF NOT EXISTS diagnostics (
                diagnostic_id TEXT PRIMARY KEY,
                category TEXT NOT NULL,
                file_path TEXT NULL,
                source_key TEXT NULL,
                message TEXT NOT NULL,
                created_at TEXT NOT NULL
            );

            INSERT OR IGNORE INTO schema_migrations(version, applied_at) VALUES (1, $now);
            INSERT OR IGNORE INTO schema_migrations(version, applied_at) VALUES (2, $now);
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await EnsureColumnAsync(connection, "runs", "items_processed", "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
        await EnsureColumnAsync(connection, "runs", "proposals_created", "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);

        await EnsureFtsAsync(connection, cancellationToken).ConfigureAwait(false);
        await SynchronizeFtsIfNeededAsync(connection, cancellationToken).ConfigureAwait(false);
        await RecoverInterruptedRunsAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SourceRevision?> GetActiveSourceAsync(string sourceKey, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM source_revisions WHERE source_key=$key AND active=1";
        command.Parameters.AddWithValue("$key", sourceKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadSource(reader) : null;
    }

    public async Task<IReadOnlyList<SourceRevision>> GetActiveSourcesAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<SourceRevision>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM source_revisions WHERE active=1 ORDER BY file_path, created_at, source_key";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) results.Add(ReadSource(reader));
        return results;
    }

    public async Task<IReadOnlyList<SourceRevision>> GetSourcesByRevisionIdsAsync(IReadOnlyCollection<string> revisionIds, CancellationToken cancellationToken = default)
    {
        if (revisionIds.Count == 0) return [];
        var results = new List<SourceRevision>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var placeholders = AddListParameters(command, "$revision", revisionIds);
        command.CommandText = $"SELECT * FROM source_revisions WHERE revision_id IN ({placeholders}) ORDER BY created_at, file_path";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) results.Add(ReadSource(reader));
        return results;
    }

    public async Task InsertSourceRevisionAsync(SourceRevision revision, IReadOnlyList<SemanticFragment> fragments, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var deactivate = connection.CreateCommand())
        {
            deactivate.Transaction = transaction;
            deactivate.CommandText = """
                UPDATE fragments SET active=0
                WHERE source_revision_id IN (SELECT revision_id FROM source_revisions WHERE source_key=$key AND active=1);
                DELETE FROM search_index.fragment_fts
                WHERE fragment_id IN (
                    SELECT fragment_id FROM fragments
                    WHERE source_revision_id IN (SELECT revision_id FROM source_revisions WHERE source_key=$key AND active=1)
                );
                UPDATE source_revisions SET active=0 WHERE source_key=$key AND active=1;
                """;
            deactivate.Parameters.AddWithValue("$key", revision.SourceKey);
            await deactivate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO source_revisions(
                    revision_id, source_key, entry_id, file_path, content, content_hash,
                    source_type, created_at, source_context_json, observed_at, predecessor_id, active)
                VALUES($id,$key,$entry,$path,$content,$hash,$type,$created,$context,$observed,$predecessor,1)
                """;
            insert.Parameters.AddWithValue("$id", revision.RevisionId);
            insert.Parameters.AddWithValue("$key", revision.SourceKey);
            insert.Parameters.AddWithValue("$entry", DbValue(revision.EntryId));
            insert.Parameters.AddWithValue("$path", revision.FilePath);
            insert.Parameters.AddWithValue("$content", revision.Content);
            insert.Parameters.AddWithValue("$hash", revision.ContentHash);
            insert.Parameters.AddWithValue("$type", revision.SourceType);
            insert.Parameters.AddWithValue("$created", DbValue(revision.CreatedAt?.ToString("O")));
            insert.Parameters.AddWithValue("$context", DbValue(revision.SourceContextJson));
            insert.Parameters.AddWithValue("$observed", revision.ObservedAt.ToString("O"));
            insert.Parameters.AddWithValue("$predecessor", DbValue(revision.PredecessorId));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var fragment in fragments)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO fragments(
                    fragment_id, source_revision_id, ordinal, text, text_hash, start_offset, end_offset,
                    topics_json, entities_json, epistemic_status, action, extractor_version, analysis_version, active)
                VALUES($id,$source,$ordinal,$text,$hash,$start,$end,'[]','[]','unknown','index_only',$extractor,NULL,1);
                INSERT INTO search_index.fragment_fts(fragment_id, text) VALUES($id,$text);
                """;
            insert.Parameters.AddWithValue("$id", fragment.FragmentId);
            insert.Parameters.AddWithValue("$source", fragment.SourceRevisionId);
            insert.Parameters.AddWithValue("$ordinal", fragment.Ordinal);
            insert.Parameters.AddWithValue("$text", fragment.Text);
            insert.Parameters.AddWithValue("$hash", Pkws.Core.MarkdownEntrySerializer.Sha256(fragment.Text));
            insert.Parameters.AddWithValue("$start", fragment.StartOffset);
            insert.Parameters.AddWithValue("$end", fragment.EndOffset);
            insert.Parameters.AddWithValue("$extractor", fragment.ExtractorVersion);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> MarkMissingSourcesAsync(IReadOnlySet<string> seenSourceKeys, CancellationToken cancellationToken = default)
    {
        var active = await GetActiveSourcesAsync(cancellationToken).ConfigureAwait(false);
        var missing = active.Where(item => !seenSourceKeys.Contains(item.SourceKey)).ToArray();
        if (missing.Length == 0) return 0;

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var source in missing)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM search_index.fragment_fts WHERE fragment_id IN (SELECT fragment_id FROM fragments WHERE source_revision_id=$revision);
                UPDATE fragments SET active=0 WHERE source_revision_id=$revision;
                UPDATE source_revisions SET active=0 WHERE revision_id=$revision;
                """;
            command.Parameters.AddWithValue("$revision", source.RevisionId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return missing.Length;
    }

    public async Task<IReadOnlyList<SemanticFragment>> GetUnanalyzedFragmentsAsync(int limit, CancellationToken cancellationToken = default)
    {
        var results = new List<SemanticFragment>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM fragments WHERE active=1 AND analysis_version IS NULL ORDER BY rowid LIMIT $limit";
        command.Parameters.AddWithValue("$limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) results.Add(ReadFragment(reader));
        return results;
    }

    public async Task<IReadOnlyList<SemanticFragment>> GetFragmentsByIdsAsync(IReadOnlyCollection<string> fragmentIds, CancellationToken cancellationToken = default)
    {
        if (fragmentIds.Count == 0) return [];
        var results = new List<SemanticFragment>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var placeholders = AddListParameters(command, "$id", fragmentIds);
        command.CommandText = $"SELECT * FROM fragments WHERE active=1 AND fragment_id IN ({placeholders}) ORDER BY rowid";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) results.Add(ReadFragment(reader));
        return results;
    }

    public async Task<IReadOnlyList<SemanticFragment>> GetFragmentsBySourceRevisionIdsAsync(IReadOnlyCollection<string> revisionIds, CancellationToken cancellationToken = default)
    {
        if (revisionIds.Count == 0) return [];
        var results = new List<SemanticFragment>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var placeholders = AddListParameters(command, "$revision", revisionIds);
        command.CommandText = $"SELECT * FROM fragments WHERE active=1 AND source_revision_id IN ({placeholders}) ORDER BY rowid";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) results.Add(ReadFragment(reader));
        return results;
    }

    public async Task UpdateFragmentAnalysisAsync(FragmentAnalysis analysis, string analysisVersion, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE fragments SET topics_json=$topics, entities_json=$entities,
                epistemic_status=$epistemic, action=$action, analysis_version=$version
            WHERE fragment_id=$id AND active=1
            """;
        command.Parameters.AddWithValue("$topics", JsonSerializer.Serialize(NormalizeTerms(analysis.Topics), JsonOptions));
        command.Parameters.AddWithValue("$entities", JsonSerializer.Serialize(NormalizeTerms(analysis.Entities), JsonOptions));
        command.Parameters.AddWithValue("$epistemic", analysis.EpistemicStatus);
        command.Parameters.AddWithValue("$action", analysis.Action);
        command.Parameters.AddWithValue("$version", analysisVersion);
        command.Parameters.AddWithValue("$id", analysis.FragmentId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int limit = 20, string? sourceType = null, DateTimeOffset? from = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var results = new List<SearchHit>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.*, s.file_path, s.entry_id, s.source_type, s.created_at,
                   bm25(fragment_fts) AS rank
            FROM search_index.fragment_fts AS fragment_fts
            JOIN fragments f ON f.fragment_id=fragment_fts.fragment_id
            JOIN source_revisions s ON s.revision_id=f.source_revision_id
            WHERE fragment_fts MATCH $query AND f.active=1 AND s.active=1
              AND ($source_type IS NULL OR s.source_type=$source_type)
              AND ($from IS NULL OR s.created_at >= $from)
            ORDER BY rank, s.created_at DESC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$query", EscapeFtsQuery(query));
        command.Parameters.AddWithValue("$source_type", DbValue(sourceType));
        command.Parameters.AddWithValue("$from", DbValue(from?.ToString("O")));
        command.Parameters.AddWithValue("$limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new SearchHit(
                reader.GetString(reader.GetOrdinal("fragment_id")),
                reader.GetString(reader.GetOrdinal("source_revision_id")),
                reader.GetString(reader.GetOrdinal("file_path")),
                GetNullableString(reader, "entry_id"),
                reader.GetString(reader.GetOrdinal("source_type")),
                ParseNullableDate(GetNullableString(reader, "created_at")),
                reader.GetString(reader.GetOrdinal("text")),
                DeserializeList(reader.GetString(reader.GetOrdinal("topics_json"))),
                reader.GetDouble(reader.GetOrdinal("rank"))));
        }

        await reader.DisposeAsync().ConfigureAwait(false);
        if (results.Count == 0)
        {
            await using var fallback = connection.CreateCommand();
            fallback.CommandText = """
                SELECT f.*, s.file_path, s.entry_id, s.source_type, s.created_at
                FROM fragments f JOIN source_revisions s ON s.revision_id=f.source_revision_id
                WHERE f.active=1 AND s.active=1 AND f.text LIKE $query ESCAPE '\'
                  AND ($source_type IS NULL OR s.source_type=$source_type)
                  AND ($from IS NULL OR s.created_at >= $from)
                ORDER BY s.created_at DESC LIMIT $limit
                """;
            var like = query.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
            fallback.Parameters.AddWithValue("$query", $"%{like}%");
            fallback.Parameters.AddWithValue("$source_type", DbValue(sourceType));
            fallback.Parameters.AddWithValue("$from", DbValue(from?.ToString("O")));
            fallback.Parameters.AddWithValue("$limit", limit);
            await using var fallbackReader = await fallback.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await fallbackReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                results.Add(new SearchHit(
                    fallbackReader.GetString(fallbackReader.GetOrdinal("fragment_id")),
                    fallbackReader.GetString(fallbackReader.GetOrdinal("source_revision_id")),
                    fallbackReader.GetString(fallbackReader.GetOrdinal("file_path")),
                    GetNullableString(fallbackReader, "entry_id"),
                    fallbackReader.GetString(fallbackReader.GetOrdinal("source_type")),
                    ParseNullableDate(GetNullableString(fallbackReader, "created_at")),
                    fallbackReader.GetString(fallbackReader.GetOrdinal("text")),
                    DeserializeList(fallbackReader.GetString(fallbackReader.GetOrdinal("topics_json"))),
                    0));
            }
        }

        return results;
    }

    public async Task<IReadOnlyList<SearchHit>> FindByTopicAsync(string topic, int limit = 50, CancellationToken cancellationToken = default)
    {
        var results = new List<SearchHit>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.*, s.file_path, s.entry_id, s.source_type, s.created_at
            FROM fragments f JOIN source_revisions s ON s.revision_id=f.source_revision_id
            WHERE f.active=1 AND s.active=1 AND
                  (EXISTS (SELECT 1 FROM json_each(f.topics_json) topic WHERE topic.value=$topic COLLATE NOCASE)
                   OR instr(f.text, $topic) > 0)
            ORDER BY s.created_at, f.ordinal LIMIT $limit
            """;
        command.Parameters.AddWithValue("$topic", topic);
        command.Parameters.AddWithValue("$limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new SearchHit(
                reader.GetString(reader.GetOrdinal("fragment_id")),
                reader.GetString(reader.GetOrdinal("source_revision_id")),
                reader.GetString(reader.GetOrdinal("file_path")),
                GetNullableString(reader, "entry_id"),
                reader.GetString(reader.GetOrdinal("source_type")),
                ParseNullableDate(GetNullableString(reader, "created_at")),
                reader.GetString(reader.GetOrdinal("text")),
                DeserializeList(reader.GetString(reader.GetOrdinal("topics_json"))),
                0));
        }

        return results;
    }

    public async Task<TopicRecord> UpsertTopicAsync(string name, string? direction, string origin, bool pinned, CancellationToken cancellationToken = default)
    {
        var normalized = name.Trim();
        if (normalized.Length is < 1 or > 120) throw new ArgumentException("主题名称长度必须在 1–120 之间。", nameof(name));
        var existing = await GetTopicAsync(normalized, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var topic = existing is null
            ? new TopicRecord { TopicId = Guid.CreateVersion7().ToString(), Name = normalized, Direction = direction?.Trim(), Origin = origin, Status = "active", Pinned = pinned, UpdatedAt = now }
            : existing with { Direction = direction?.Trim() ?? existing.Direction, Pinned = pinned || existing.Pinned, UpdatedAt = now };

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO topics(topic_id,name,aliases_json,direction,origin,status,pinned,updated_at)
            VALUES($id,$name,$aliases,$direction,$origin,$status,$pinned,$updated)
            ON CONFLICT(name) DO UPDATE SET direction=excluded.direction, pinned=excluded.pinned, updated_at=excluded.updated_at
            """;
        command.Parameters.AddWithValue("$id", topic.TopicId);
        command.Parameters.AddWithValue("$name", topic.Name);
        command.Parameters.AddWithValue("$aliases", JsonSerializer.Serialize(topic.Aliases, JsonOptions));
        command.Parameters.AddWithValue("$direction", DbValue(topic.Direction));
        command.Parameters.AddWithValue("$origin", topic.Origin);
        command.Parameters.AddWithValue("$status", topic.Status);
        command.Parameters.AddWithValue("$pinned", topic.Pinned ? 1 : 0);
        command.Parameters.AddWithValue("$updated", topic.UpdatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return topic;
    }

    public async Task<TopicRecord?> GetTopicAsync(string idOrName, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM topics WHERE topic_id=$value OR name=$value COLLATE NOCASE LIMIT 1";
        command.Parameters.AddWithValue("$value", idOrName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadTopic(reader) : null;
    }

    public async Task<IReadOnlyList<TopicRecord>> ListTopicsAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<TopicRecord>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM topics WHERE status='active' ORDER BY pinned DESC, updated_at DESC, name";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) results.Add(ReadTopic(reader));
        return results;
    }

    public async Task<ArticleRecord?> GetArticleAsync(string articleId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM articles WHERE article_id=$id";
        command.Parameters.AddWithValue("$id", articleId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadArticle(reader) : null;
    }

    public async Task<ArticleRecord?> FindArticleByTitleAsync(string title, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM articles WHERE title=$title COLLATE NOCASE LIMIT 1";
        command.Parameters.AddWithValue("$title", title);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadArticle(reader) : null;
    }

    public async Task<IReadOnlyList<ArticleRecord>> ListArticlesAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<ArticleRecord>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM articles ORDER BY updated_at DESC";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) results.Add(ReadArticle(reader));
        return results;
    }

    public async Task UpsertArticleRevisionAsync(ArticleRecord article, string content, string actor, IReadOnlyList<string> sourceRevisionIds, string? changeId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO articles(article_id,relative_path,title,status,maintenance_mode,current_hash,revision_number,updated_at)
                VALUES($id,$path,$title,$status,$mode,$hash,$revision,$updated)
                ON CONFLICT(article_id) DO UPDATE SET relative_path=excluded.relative_path,title=excluded.title,
                    status=excluded.status,maintenance_mode=excluded.maintenance_mode,current_hash=excluded.current_hash,
                    revision_number=excluded.revision_number,updated_at=excluded.updated_at
                """;
            command.Parameters.AddWithValue("$id", article.ArticleId);
            command.Parameters.AddWithValue("$path", article.RelativePath);
            command.Parameters.AddWithValue("$title", article.Title);
            command.Parameters.AddWithValue("$status", article.Status);
            command.Parameters.AddWithValue("$mode", article.MaintenanceMode);
            command.Parameters.AddWithValue("$hash", article.CurrentHash);
            command.Parameters.AddWithValue("$revision", article.RevisionNumber);
            command.Parameters.AddWithValue("$updated", article.UpdatedAt.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO article_revisions(
                    revision_id,article_id,revision_number,content,content_hash,actor,source_revision_ids_json,change_id,created_at)
                VALUES($id,$article,$revision,$content,$hash,$actor,$sources,$change,$created)
                """;
            command.Parameters.AddWithValue("$id", Guid.CreateVersion7().ToString());
            command.Parameters.AddWithValue("$article", article.ArticleId);
            command.Parameters.AddWithValue("$revision", article.RevisionNumber);
            command.Parameters.AddWithValue("$content", content);
            command.Parameters.AddWithValue("$hash", article.CurrentHash);
            command.Parameters.AddWithValue("$actor", actor);
            command.Parameters.AddWithValue("$sources", JsonSerializer.Serialize(sourceRevisionIds, JsonOptions));
            command.Parameters.AddWithValue("$change", DbValue(changeId));
            command.Parameters.AddWithValue("$created", article.UpdatedAt.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<(string Content, string Hash, int RevisionNumber)?> GetArticleRevisionAsync(string articleId, int revisionNumber, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT content,content_hash,revision_number FROM article_revisions WHERE article_id=$article AND revision_number=$revision";
        command.Parameters.AddWithValue("$article", articleId);
        command.Parameters.AddWithValue("$revision", revisionNumber);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return (reader.GetString(0), reader.GetString(1), reader.GetInt32(2));
    }

    public async Task<(string Content, string Hash, int RevisionNumber)?> GetArticleRevisionByHashAsync(string articleId, string hash, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT content,content_hash,revision_number FROM article_revisions WHERE article_id=$article AND content_hash=$hash ORDER BY revision_number DESC LIMIT 1";
        command.Parameters.AddWithValue("$article", articleId);
        command.Parameters.AddWithValue("$hash", hash);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return (reader.GetString(0), reader.GetString(1), reader.GetInt32(2));
    }

    public async Task<ChangeProposal> InsertProposalAsync(ChangeProposal proposal, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO proposals(
                proposal_id,idempotency_key,article_id,target_path,proposal_type,risk_level,status,base_hash,title,rationale,
                proposed_content,source_revision_ids_json,created_at,reviewed_at,applied_at,result_hash,error_code)
            VALUES($id,$key,$article,$path,$type,$risk,$status,$base,$title,$reason,$content,$sources,$created,NULL,NULL,NULL,NULL)
            """;
        command.Parameters.AddWithValue("$id", proposal.ProposalId);
        command.Parameters.AddWithValue("$key", proposal.IdempotencyKey);
        command.Parameters.AddWithValue("$article", DbValue(proposal.ArticleId));
        command.Parameters.AddWithValue("$path", proposal.TargetPath);
        command.Parameters.AddWithValue("$type", proposal.ProposalType);
        command.Parameters.AddWithValue("$risk", proposal.RiskLevel);
        command.Parameters.AddWithValue("$status", proposal.Status);
        command.Parameters.AddWithValue("$base", DbValue(proposal.BaseHash));
        command.Parameters.AddWithValue("$title", proposal.Title);
        command.Parameters.AddWithValue("$reason", proposal.Rationale);
        command.Parameters.AddWithValue("$content", proposal.ProposedContent);
        command.Parameters.AddWithValue("$sources", JsonSerializer.Serialize(proposal.SourceRevisionIds, JsonOptions));
        command.Parameters.AddWithValue("$created", proposal.CreatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var read = connection.CreateCommand();
        read.CommandText = "SELECT * FROM proposals WHERE idempotency_key=$key";
        read.Parameters.AddWithValue("$key", proposal.IdempotencyKey);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return ReadProposal(reader);
    }

    public async Task<ChangeProposal?> GetProposalAsync(string proposalId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM proposals WHERE proposal_id=$id";
        command.Parameters.AddWithValue("$id", proposalId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadProposal(reader) : null;
    }

    public async Task<IReadOnlyList<ChangeProposal>> ListProposalsAsync(string? status = "pending", CancellationToken cancellationToken = default)
    {
        var results = new List<ChangeProposal>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM proposals WHERE ($status IS NULL OR status=$status) ORDER BY created_at DESC";
        command.Parameters.AddWithValue("$status", DbValue(status));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) results.Add(ReadProposal(reader));
        return results;
    }

    public async Task UpdateProposalStatusAsync(string proposalId, string status, string? articleId = null, string? resultHash = null, string? errorCode = null, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE proposals SET status=$status, article_id=COALESCE($article,article_id),
                reviewed_at=CASE WHEN $status IN ('approved','rejected','applied','conflicted','failed') THEN COALESCE(reviewed_at,$now) ELSE reviewed_at END,
                applied_at=CASE WHEN $status='applied' THEN $now ELSE applied_at END,
                result_hash=COALESCE($hash,result_hash), error_code=$error
            WHERE proposal_id=$id
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$article", DbValue(articleId));
        command.Parameters.AddWithValue("$hash", DbValue(resultHash));
        command.Parameters.AddWithValue("$error", DbValue(errorCode));
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$id", proposalId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> StartRunAsync(string runType, CancellationToken cancellationToken = default)
    {
        var id = Guid.CreateVersion7().ToString();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO runs(run_id,run_type,status,started_at) VALUES($id,$type,'running',$started)";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$type", runType);
        command.Parameters.AddWithValue("$started", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return id;
    }

    public async Task CompleteRunAsync(string runId, string status, int itemsProcessed, int proposalsCreated, string? errorCode = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE runs SET status=$status,finished_at=$finished,items_processed=$items,
                proposals_created=$proposals,error_code=$error WHERE run_id=$id
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$finished", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$items", itemsProcessed);
        command.Parameters.AddWithValue("$proposals", proposalsCreated);
        command.Parameters.AddWithValue("$error", DbValue(errorCode));
        command.Parameters.AddWithValue("$id", runId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> HasSuccessfulRunSinceAsync(string runType, DateTimeOffset since, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM runs WHERE run_type=$type AND status='succeeded' AND finished_at >= $since)";
        command.Parameters.AddWithValue("$type", runType);
        command.Parameters.AddWithValue("$since", since.ToUniversalTime().ToString("O"));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 1;
    }

    public async Task InsertWorksetAsync(CodexWorksetRecord workset, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO codex_worksets(workset_id,run_type,topic_name,status,fragment_ids_json,
                source_revision_ids_json,article_baselines_json,created_at,proposal_ids_json)
            VALUES($id,$type,$topic,$status,$fragments,$sources,$articles,$created,'[]')
            """;
        command.Parameters.AddWithValue("$id", workset.WorksetId);
        command.Parameters.AddWithValue("$type", workset.RunType);
        command.Parameters.AddWithValue("$topic", DbValue(workset.TopicName));
        command.Parameters.AddWithValue("$status", workset.Status);
        command.Parameters.AddWithValue("$fragments", JsonSerializer.Serialize(workset.FragmentIds, JsonOptions));
        command.Parameters.AddWithValue("$sources", JsonSerializer.Serialize(workset.SourceRevisionIds, JsonOptions));
        command.Parameters.AddWithValue("$articles", JsonSerializer.Serialize(workset.ArticleBaselines, JsonOptions));
        command.Parameters.AddWithValue("$created", workset.CreatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<CodexWorksetRecord?> GetWorksetAsync(string worksetId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM codex_worksets WHERE workset_id=$id";
        command.Parameters.AddWithValue("$id", worksetId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadWorkset(reader) : null;
    }

    public async Task<IReadOnlyList<CodexWorksetRecord>> ListWorksetsAsync(string? status = "pending", CancellationToken cancellationToken = default)
    {
        var results = new List<CodexWorksetRecord>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = status is null
            ? "SELECT * FROM codex_worksets ORDER BY created_at DESC"
            : "SELECT * FROM codex_worksets WHERE status=$status ORDER BY created_at DESC";
        if (status is not null) command.Parameters.AddWithValue("$status", status);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) results.Add(ReadWorkset(reader));
        return results;
    }

    public async Task CompleteWorksetAsync(string worksetId, string submissionHash, IReadOnlyList<string> proposalIds, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE codex_worksets SET status='completed',completed_at=$completed,submission_hash=$hash,
                proposal_ids_json=$proposals,error_code=NULL WHERE workset_id=$id
            """;
        command.Parameters.AddWithValue("$completed", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$hash", submissionHash);
        command.Parameters.AddWithValue("$proposals", JsonSerializer.Serialize(proposalIds, JsonOptions));
        command.Parameters.AddWithValue("$id", worksetId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task FailWorksetAsync(string worksetId, string errorCode, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE codex_worksets SET error_code=$error WHERE workset_id=$id AND status='pending'";
        command.Parameters.AddWithValue("$error", errorCode);
        command.Parameters.AddWithValue("$id", worksetId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AbandonWorksetAsync(string worksetId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE codex_worksets SET status='abandoned',completed_at=$completed,error_code='ABANDONED_BY_USER' WHERE workset_id=$id AND status='pending'";
        command.Parameters.AddWithValue("$completed", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", worksetId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            throw new InvalidOperationException("只能放弃存在的 pending 工作集。");
    }

    public async Task AddDiagnosticAsync(string category, string message, string? filePath = null, string? sourceKey = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO diagnostics(diagnostic_id,category,file_path,source_key,message,created_at) VALUES($id,$category,$file,$source,$message,$created)";
        command.Parameters.AddWithValue("$id", Guid.CreateVersion7().ToString());
        command.Parameters.AddWithValue("$category", category);
        command.Parameters.AddWithValue("$file", DbValue(filePath));
        command.Parameters.AddWithValue("$source", DbValue(sourceKey));
        command.Parameters.AddWithValue("$message", message.Length > 1000 ? message[..1000] : message);
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgentStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        async Task<int> ScalarAsync(string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        }

        AgentRun? lastRun = null;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT * FROM runs ORDER BY started_at DESC LIMIT 1";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) lastRun = ReadRun(reader);
        }

        return new AgentStatus(
            await ScalarAsync("SELECT COUNT(*) FROM source_revisions WHERE active=1"),
            await ScalarAsync("SELECT COUNT(*) FROM fragments WHERE active=1"),
            await ScalarAsync("SELECT COUNT(*) FROM topics WHERE status='active'"),
            await ScalarAsync("SELECT COUNT(*) FROM articles"),
            await ScalarAsync("SELECT COUNT(*) FROM proposals WHERE status='pending'"),
            await ScalarAsync("SELECT COUNT(*) FROM codex_worksets WHERE status='pending'"),
            lastRun);
    }

    public async Task RebuildFtsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        await EnsureFtsAsync(connection, cancellationToken).ConfigureAwait(false);
        command.CommandText = "DELETE FROM search_index.fragment_fts; INSERT INTO search_index.fragment_fts(fragment_id,text) SELECT fragment_id,text FROM fragments WHERE active=1;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureFtsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE VIRTUAL TABLE IF NOT EXISTS search_index.fragment_fts USING fts5(fragment_id UNINDEXED, text, tokenize='trigram');";
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            command.CommandText = "CREATE VIRTUAL TABLE IF NOT EXISTS search_index.fragment_fts USING fts5(fragment_id UNINDEXED, text, tokenize='unicode61');";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task SynchronizeFtsIfNeededAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT (SELECT COUNT(*) FROM fragments WHERE active=1), (SELECT COUNT(*) FROM search_index.fragment_fts)";
        await using var reader = await count.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (reader.GetInt64(0) == reader.GetInt64(1)) return;
        await reader.DisposeAsync().ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM search_index.fragment_fts; INSERT INTO search_index.fragment_fts(fragment_id,text) SELECT fragment_id,text FROM fragments WHERE active=1;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task RecoverInterruptedRunsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE runs SET status='interrupted',finished_at=$now,error_code='PROCESS_INTERRUPTED' WHERE status='running'";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureColumnAsync(SqliteConnection connection, string table, string column, string definition, CancellationToken cancellationToken)
    {
        await using var check = connection.CreateCommand();
        check.CommandText = $"PRAGMA table_info({table})";
        await using var reader = await check.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return;
        await reader.DisposeAsync().ConfigureAwait(false);
        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "ATTACH DATABASE $index AS search_index; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA search_index.journal_mode=WAL; PRAGMA search_index.synchronous=FULL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
        command.Parameters.AddWithValue("$index", _indexDatabasePath);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static SourceRevision ReadSource(SqliteDataReader reader) => new()
    {
        RevisionId = reader.GetString(reader.GetOrdinal("revision_id")),
        SourceKey = reader.GetString(reader.GetOrdinal("source_key")),
        EntryId = GetNullableString(reader, "entry_id"),
        FilePath = reader.GetString(reader.GetOrdinal("file_path")),
        Content = reader.GetString(reader.GetOrdinal("content")),
        ContentHash = reader.GetString(reader.GetOrdinal("content_hash")),
        SourceType = reader.GetString(reader.GetOrdinal("source_type")),
        CreatedAt = ParseNullableDate(GetNullableString(reader, "created_at")),
        SourceContextJson = GetNullableString(reader, "source_context_json"),
        ObservedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("observed_at"))),
        PredecessorId = GetNullableString(reader, "predecessor_id"),
        Active = reader.GetInt32(reader.GetOrdinal("active")) == 1
    };

    private static SemanticFragment ReadFragment(SqliteDataReader reader) => new()
    {
        FragmentId = reader.GetString(reader.GetOrdinal("fragment_id")),
        SourceRevisionId = reader.GetString(reader.GetOrdinal("source_revision_id")),
        Ordinal = reader.GetInt32(reader.GetOrdinal("ordinal")),
        Text = reader.GetString(reader.GetOrdinal("text")),
        StartOffset = reader.GetInt32(reader.GetOrdinal("start_offset")),
        EndOffset = reader.GetInt32(reader.GetOrdinal("end_offset")),
        Topics = DeserializeList(reader.GetString(reader.GetOrdinal("topics_json"))),
        Entities = DeserializeList(reader.GetString(reader.GetOrdinal("entities_json"))),
        EpistemicStatus = reader.GetString(reader.GetOrdinal("epistemic_status")),
        Action = reader.GetString(reader.GetOrdinal("action")),
        ExtractorVersion = reader.GetString(reader.GetOrdinal("extractor_version")),
        Active = reader.GetInt32(reader.GetOrdinal("active")) == 1
    };

    private static TopicRecord ReadTopic(SqliteDataReader reader) => new()
    {
        TopicId = reader.GetString(reader.GetOrdinal("topic_id")),
        Name = reader.GetString(reader.GetOrdinal("name")),
        Aliases = DeserializeList(reader.GetString(reader.GetOrdinal("aliases_json"))),
        Direction = GetNullableString(reader, "direction"),
        Origin = reader.GetString(reader.GetOrdinal("origin")),
        Status = reader.GetString(reader.GetOrdinal("status")),
        Pinned = reader.GetInt32(reader.GetOrdinal("pinned")) == 1,
        UpdatedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("updated_at")))
    };

    private static ArticleRecord ReadArticle(SqliteDataReader reader) => new()
    {
        ArticleId = reader.GetString(reader.GetOrdinal("article_id")),
        RelativePath = reader.GetString(reader.GetOrdinal("relative_path")),
        Title = reader.GetString(reader.GetOrdinal("title")),
        Status = reader.GetString(reader.GetOrdinal("status")),
        MaintenanceMode = reader.GetString(reader.GetOrdinal("maintenance_mode")),
        CurrentHash = reader.GetString(reader.GetOrdinal("current_hash")),
        RevisionNumber = reader.GetInt32(reader.GetOrdinal("revision_number")),
        UpdatedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("updated_at")))
    };

    private static ChangeProposal ReadProposal(SqliteDataReader reader) => new()
    {
        ProposalId = reader.GetString(reader.GetOrdinal("proposal_id")),
        IdempotencyKey = reader.GetString(reader.GetOrdinal("idempotency_key")),
        ArticleId = GetNullableString(reader, "article_id"),
        TargetPath = reader.GetString(reader.GetOrdinal("target_path")),
        ProposalType = reader.GetString(reader.GetOrdinal("proposal_type")),
        RiskLevel = reader.GetString(reader.GetOrdinal("risk_level")),
        Status = reader.GetString(reader.GetOrdinal("status")),
        BaseHash = GetNullableString(reader, "base_hash"),
        Title = reader.GetString(reader.GetOrdinal("title")),
        Rationale = reader.GetString(reader.GetOrdinal("rationale")),
        ProposedContent = reader.GetString(reader.GetOrdinal("proposed_content")),
        SourceRevisionIds = DeserializeList(reader.GetString(reader.GetOrdinal("source_revision_ids_json"))),
        CreatedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
        ReviewedAt = ParseNullableDate(GetNullableString(reader, "reviewed_at")),
        AppliedAt = ParseNullableDate(GetNullableString(reader, "applied_at")),
        ResultHash = GetNullableString(reader, "result_hash"),
        ErrorCode = GetNullableString(reader, "error_code")
    };

    private static AgentRun ReadRun(SqliteDataReader reader) => new()
    {
        RunId = reader.GetString(reader.GetOrdinal("run_id")),
        RunType = reader.GetString(reader.GetOrdinal("run_type")),
        Status = reader.GetString(reader.GetOrdinal("status")),
        StartedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("started_at"))),
        FinishedAt = ParseNullableDate(GetNullableString(reader, "finished_at")),
        ItemsProcessed = reader.GetInt32(reader.GetOrdinal("items_processed")),
        ProposalsCreated = reader.GetInt32(reader.GetOrdinal("proposals_created")),
        ErrorCode = GetNullableString(reader, "error_code")
    };

    private static CodexWorksetRecord ReadWorkset(SqliteDataReader reader) => new()
    {
        WorksetId = reader.GetString(reader.GetOrdinal("workset_id")),
        RunType = reader.GetString(reader.GetOrdinal("run_type")),
        TopicName = GetNullableString(reader, "topic_name"),
        Status = reader.GetString(reader.GetOrdinal("status")),
        FragmentIds = DeserializeList(reader.GetString(reader.GetOrdinal("fragment_ids_json"))),
        SourceRevisionIds = DeserializeList(reader.GetString(reader.GetOrdinal("source_revision_ids_json"))),
        ArticleBaselines = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(reader.GetOrdinal("article_baselines_json")), JsonOptions) ?? new Dictionary<string, string>(),
        CreatedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
        CompletedAt = ParseNullableDate(GetNullableString(reader, "completed_at")),
        SubmissionHash = GetNullableString(reader, "submission_hash"),
        ProposalIds = DeserializeList(reader.GetString(reader.GetOrdinal("proposal_ids_json"))),
        ErrorCode = GetNullableString(reader, "error_code")
    };

    private static object DbValue(string? value) => value is null ? DBNull.Value : value;
    private static string? GetNullableString(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }
    private static DateTimeOffset? ParseNullableDate(string? value) => value is null ? null : DateTimeOffset.Parse(value);
    private static IReadOnlyList<string> DeserializeList(string json) => JsonSerializer.Deserialize<string[]>(json, JsonOptions) ?? [];
    private static IReadOnlyList<string> NormalizeTerms(IEnumerable<string> values) => values.Select(value => value.Trim()).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToArray();
    private static string EscapeFtsQuery(string query) => $"\"{query.Trim().Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    private static string AddListParameters(SqliteCommand command, string prefix, IEnumerable<string> values)
    {
        var names = new List<string>();
        var index = 0;
        foreach (var value in values)
        {
            var name = $"{prefix}{index++}";
            command.Parameters.AddWithValue(name, value);
            names.Add(name);
        }
        return string.Join(',', names);
    }
}
