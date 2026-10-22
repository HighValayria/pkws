using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Pkws.Core;

namespace Pkws.Agent;

public sealed class DailyNoteIndexer
{
    private readonly AgentPaths _paths;
    private readonly AgentDatabase _database;
    private readonly DailyMarkdownParser _parser = new();
    private readonly SemanticFragmenter _fragmenter = new();
    private readonly CaptureContextReader? _contexts;
    private readonly TimeProvider _timeProvider;

    public DailyNoteIndexer(AgentPaths paths, AgentDatabase database, string? captureDatabasePath = null, TimeProvider? timeProvider = null)
    {
        _paths = paths;
        _database = database;
        _contexts = !string.IsNullOrWhiteSpace(captureDatabasePath) && File.Exists(captureDatabasePath)
            ? new CaptureContextReader(captureDatabasePath)
            : null;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ScanSummary> ScanAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectories();
        var dailyRoot = Path.Combine(_paths.DocumentRoot, "Daily");
        var files = Directory.Exists(dailyRoot)
            ? Directory.EnumerateFiles(dailyRoot, "*.md", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray()
            : [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var seenEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unreadableFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        var unchanged = 0;
        var fragmentsCreated = 0;
        var diagnostics = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = _paths.ToRelativeDocumentPath(file);
            string markdown;
            try
            {
                markdown = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                unreadableFiles.Add(relative);
                diagnostics++;
                await _database.AddDiagnosticAsync("SOURCE_READ_FAILED", exception.Message, relative, cancellationToken: cancellationToken).ConfigureAwait(false);
                continue;
            }

            ParsedDailyDocument parsed;
            try
            {
                parsed = _parser.Parse(markdown, relative);
            }
            catch (Exception exception) when (exception is JsonException or FormatException)
            {
                unreadableFiles.Add(relative);
                diagnostics++;
                await _database.AddDiagnosticAsync("MARKDOWN_PARSE_FAILED", exception.Message, relative, cancellationToken: cancellationToken).ConfigureAwait(false);
                continue;
            }

            foreach (var diagnostic in parsed.Diagnostics)
            {
                diagnostics++;
                await _database.AddDiagnosticAsync("MARKDOWN_PARSE_WARNING", diagnostic, relative, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            foreach (var source in parsed.Sources)
            {
                var sourceKey = source.EntryId is null ? $"manual:{relative.ToLowerInvariant()}" : $"entry:{source.EntryId.ToLowerInvariant()}";
                if (!seen.Add(sourceKey))
                {
                    diagnostics++;
                    await _database.AddDiagnosticAsync("DUPLICATE_SOURCE_KEY", "扫描中出现重复的稳定来源键，已保留首次出现。", relative, sourceKey, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (source.EntryId is not null && !seenEntries.Add(source.EntryId))
                {
                    diagnostics++;
                    await _database.AddDiagnosticAsync("DUPLICATE_ENTRY_ID", "多个位置出现同一 Entry ID，已隔离重复项。", relative, sourceKey, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var sourceContext = source.EntryId is null || _contexts is null
                    ? null
                    : await _contexts.GetSourceContextAsync(source.EntryId, cancellationToken).ConfigureAwait(false);
                var hash = MarkdownEntrySerializer.Sha256(source.Content);
                var existing = await _database.GetActiveSourceAsync(sourceKey, cancellationToken).ConfigureAwait(false);
                if (existing is not null &&
                    string.Equals(existing.ContentHash, hash, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.FilePath, relative, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.SourceType, source.SourceType, StringComparison.Ordinal) &&
                    string.Equals(existing.SourceContextJson, sourceContext, StringComparison.Ordinal))
                {
                    unchanged++;
                    continue;
                }

                var revisionId = Guid.CreateVersion7(_timeProvider.GetUtcNow()).ToString();
                var revision = new SourceRevision
                {
                    RevisionId = revisionId,
                    SourceKey = sourceKey,
                    EntryId = source.EntryId,
                    FilePath = relative,
                    Content = source.Content,
                    ContentHash = hash,
                    SourceType = source.SourceType,
                    CreatedAt = source.CreatedAt,
                    SourceContextJson = sourceContext,
                    ObservedAt = _timeProvider.GetUtcNow(),
                    PredecessorId = existing?.RevisionId,
                    Active = true
                };
                var fragments = _fragmenter.Split(revisionId, source.Content);
                await _database.InsertSourceRevisionAsync(revision, fragments, cancellationToken).ConfigureAwait(false);
                added++;
                fragmentsCreated += fragments.Count;
            }
        }

        if (unreadableFiles.Count > 0)
        {
            var active = await _database.GetActiveSourcesAsync(cancellationToken).ConfigureAwait(false);
            foreach (var source in active.Where(source => unreadableFiles.Contains(source.FilePath)))
                seen.Add(source.SourceKey);
        }

        var removed = await _database.MarkMissingSourcesAsync(seen, cancellationToken).ConfigureAwait(false);
        return new ScanSummary(files.Length, seen.Count, added, unchanged, removed, fragmentsCreated, diagnostics);
    }
}

public sealed class DailyMarkdownParser
{
    private const string Marker = "<!-- pkws:entry:v1 ";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public ParsedDailyDocument Parse(string markdown, string relativePath)
    {
        var sources = new List<ParsedSource>();
        var diagnostics = new List<string>();
        var manual = new StringBuilder();
        var cursor = 0;

        while (cursor < markdown.Length)
        {
            var opening = markdown.IndexOf(Marker, cursor, StringComparison.Ordinal);
            if (opening < 0)
            {
                AppendManual(manual, markdown[cursor..]);
                break;
            }

            AppendManual(manual, markdown[cursor..opening]);
            var openingEnd = markdown.IndexOf("-->", opening + Marker.Length, StringComparison.Ordinal);
            if (openingEnd < 0)
            {
                diagnostics.Add($"{relativePath}: 存在未闭合的 PKWS 开始标记。剩余内容按手工区域处理。");
                AppendManual(manual, markdown[opening..]);
                break;
            }

            EntryMetadata? metadata;
            try
            {
                var json = markdown[(opening + Marker.Length)..openingEnd].Trim();
                metadata = JsonSerializer.Deserialize<EntryMetadata>(json, JsonOptions);
            }
            catch (JsonException)
            {
                diagnostics.Add($"{relativePath}: PKWS 元数据 JSON 无法解析。该区域未作为受控记录索引。");
                cursor = openingEnd + 3;
                continue;
            }

            if (metadata is null || string.IsNullOrWhiteSpace(metadata.Id))
            {
                diagnostics.Add($"{relativePath}: PKWS 元数据缺少 Entry ID。");
                cursor = openingEnd + 3;
                continue;
            }

            var close = MarkdownEntrySerializer.ClosingMarker(metadata.Id);
            var closing = markdown.IndexOf(close, openingEnd + 3, StringComparison.Ordinal);
            if (closing < 0)
            {
                diagnostics.Add($"{relativePath}: Entry {metadata.Id} 缺少结束标记，已隔离。");
                cursor = openingEnd + 3;
                continue;
            }

            var bodyStart = SkipLineBreaks(markdown, openingEnd + 3);
            var headingEnd = IndexOfLineEnd(markdown, bodyStart);
            if (headingEnd < 0 || !markdown[bodyStart..headingEnd].TrimStart().StartsWith("### ", StringComparison.Ordinal))
            {
                diagnostics.Add($"{relativePath}: Entry {metadata.Id} 缺少时间标题，已使用剩余正文。");
                headingEnd = bodyStart;
            }

            var contentStart = SkipLineBreaks(markdown, headingEnd);
            var raw = markdown[contentStart..closing];
            var content = RestoreContent(raw, metadata.ContentHash);
            DateTimeOffset? createdAt = DateTimeOffset.TryParse(metadata.CreatedAt, out var parsed) ? parsed : null;
            sources.Add(new ParsedSource(metadata.Id, metadata.Source ?? "desktop", content, createdAt));
            cursor = closing + close.Length;
        }

        var manualContent = NormalizeManual(manual.ToString());
        if (!string.IsNullOrWhiteSpace(manualContent))
        {
            sources.Add(new ParsedSource(null, "manual", manualContent, InferDate(relativePath)));
        }

        return new ParsedDailyDocument(sources, diagnostics);
    }

    private static void AppendManual(StringBuilder builder, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (builder.Length > 0) builder.AppendLine().AppendLine();
        builder.Append(value.Trim());
    }

    private static string NormalizeManual(string value)
    {
        var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (normalized.StartsWith("# ", StringComparison.Ordinal) && normalized.IndexOf('\n') < 0)
        {
            return string.Empty;
        }
        var firstBreak = normalized.IndexOf('\n');
        if (normalized.StartsWith("# ", StringComparison.Ordinal) && firstBreak >= 0)
        {
            normalized = normalized[(firstBreak + 1)..].Trim();
        }
        return normalized;
    }

    private static DateTimeOffset? InferDate(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return DateOnly.TryParse(name, out var date)
            ? new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
            : null;
    }

    private static string RestoreContent(string raw, string? expectedHash)
    {
        var candidates = new[] { RemoveTrailingLineBreaks(raw, 2), RemoveTrailingLineBreaks(raw, 1), raw, raw.TrimEnd('\r', '\n') };
        if (!string.IsNullOrWhiteSpace(expectedHash))
        {
            var exact = candidates.FirstOrDefault(candidate => string.Equals(MarkdownEntrySerializer.Sha256(candidate), expectedHash, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;
        }
        return raw.TrimEnd('\r', '\n');
    }

    private static string RemoveTrailingLineBreaks(string value, int count)
    {
        var end = value.Length;
        for (var i = 0; i < count && end > 0; i++)
        {
            if (value[end - 1] == '\n') { end--; if (end > 0 && value[end - 1] == '\r') end--; }
            else if (value[end - 1] == '\r') end--;
            else break;
        }
        return value[..end];
    }

    private static int SkipLineBreaks(string value, int start)
    {
        var index = start;
        while (index < value.Length && value[index] is '\r' or '\n') index++;
        return index;
    }

    private static int IndexOfLineEnd(string value, int start)
    {
        var index = value.IndexOfAny(['\r', '\n'], start);
        return index < 0 ? value.Length : index;
    }

    private sealed record EntryMetadata(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("created_at")] string? CreatedAt,
        [property: JsonPropertyName("source")] string? Source,
        [property: JsonPropertyName("content_sha256")] string? ContentHash);
}

public sealed class SemanticFragmenter
{
    public const string Version = "paragraph-v1";
    private const int MaxFragmentLength = 3000;

    public IReadOnlyList<SemanticFragment> Split(string revisionId, string content)
    {
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        var ranges = ParagraphRanges(normalized).ToList();
        var fragments = new List<SemanticFragment>();
        var ordinal = 0;
        foreach (var range in MergeShortRanges(normalized, ranges))
        {
            var start = range.Start;
            var remaining = range.Length;
            while (remaining > 0)
            {
                var take = Math.Min(remaining, MaxFragmentLength);
                if (take < remaining)
                {
                    var split = normalized.LastIndexOfAny(['。', '！', '？', '.', '!', '?', '\n'], start + take - 1, take);
                    if (split > start + MaxFragmentLength / 2) take = split - start + 1;
                }
                var raw = normalized.Substring(start, take);
                var leading = raw.Length - raw.TrimStart().Length;
                var text = raw.Trim();
                if (text.Length > 0)
                {
                    var actualStart = start + leading;
                    fragments.Add(new SemanticFragment
                    {
                        FragmentId = Guid.CreateVersion7().ToString(),
                        SourceRevisionId = revisionId,
                        Ordinal = ordinal++,
                        Text = text,
                        StartOffset = actualStart,
                        EndOffset = actualStart + text.Length,
                        ExtractorVersion = Version,
                        Active = true
                    });
                }
                start += take;
                remaining -= take;
            }
        }
        return fragments;
    }

    private static IEnumerable<(int Start, int Length)> ParagraphRanges(string text)
    {
        var start = 0;
        for (var index = 0; index <= text.Length; index++)
        {
            var boundary = index == text.Length || (text[index] == '\n' && index + 1 < text.Length && text[index + 1] == '\n');
            if (!boundary) continue;
            if (index > start) yield return (start, index - start);
            while (index + 1 < text.Length && text[index + 1] == '\n') index++;
            start = index + 1;
        }
    }

    private static IEnumerable<(int Start, int Length)> MergeShortRanges(string text, IReadOnlyList<(int Start, int Length)> ranges)
    {
        for (var index = 0; index < ranges.Count; index++)
        {
            var current = ranges[index];
            var trimmedLength = text.Substring(current.Start, current.Length).Trim().Length;
            if (trimmedLength < 24 && index + 1 < ranges.Count)
            {
                var next = ranges[index + 1];
                if (next.Start + next.Length - current.Start <= 1200)
                {
                    yield return (current.Start, next.Start + next.Length - current.Start);
                    index++;
                    continue;
                }
            }
            yield return current;
        }
    }
}

public sealed class CaptureContextReader
{
    private readonly string _connectionString;
    public CaptureContextReader(string databasePath) => _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly }.ToString();

    public async Task<string?> GetSourceContextAsync(string entryId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT source_context_json FROM captures WHERE entry_id=$id LIMIT 1";
            command.Parameters.AddWithValue("$id", entryId);
            return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        }
        catch (SqliteException)
        {
            return null;
        }
    }
}

public sealed record ParsedDailyDocument(IReadOnlyList<ParsedSource> Sources, IReadOnlyList<string> Diagnostics);
public sealed record ParsedSource(string? EntryId, string SourceType, string Content, DateTimeOffset? CreatedAt);
