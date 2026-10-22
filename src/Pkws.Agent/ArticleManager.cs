using System.Text;
using System.Text.Json;
using Pkws.Core;

namespace Pkws.Agent;

public sealed class ArticleManager
{
    private const string SourcesMarker = "<!-- pkws:sources:v1 ";
    private readonly AgentPaths _paths;
    private readonly AgentDatabase _database;
    private readonly TimeProvider _timeProvider;

    public ArticleManager(AgentPaths paths, AgentDatabase database, TimeProvider? timeProvider = null)
    {
        _paths = paths;
        _database = database;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<int> SyncExternalArticlesAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectories();
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(_paths.ArticlesDirectory, "*.md", SearchOption.TopDirectoryOnly))
        {
            var content = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            if (!TryParseArticleMetadata(content, out var articleId, out var status, out var mode, out var title))
            {
                await _database.AddDiagnosticAsync("ARTICLE_METADATA_INVALID", "文章缺少有效的 PKWS frontmatter，未纳入自动维护。", _paths.ToRelativeDocumentPath(file), cancellationToken: cancellationToken).ConfigureAwait(false);
                continue;
            }
            var hash = MarkdownEntrySerializer.Sha256(content);
            var existing = await _database.GetArticleAsync(articleId, cancellationToken).ConfigureAwait(false);
            if (existing is not null && string.Equals(existing.CurrentHash, hash, StringComparison.OrdinalIgnoreCase)) continue;
            var article = new ArticleRecord
            {
                ArticleId = articleId,
                RelativePath = _paths.ToRelativeDocumentPath(file),
                Title = title,
                Status = status,
                MaintenanceMode = mode,
                CurrentHash = hash,
                RevisionNumber = (existing?.RevisionNumber ?? 0) + 1,
                UpdatedAt = _timeProvider.GetUtcNow()
            };
            await _database.UpsertArticleRevisionAsync(article, content, "external_unknown", ExtractSourceIds(content), null, cancellationToken).ConfigureAwait(false);
            count++;
        }
        return count;
    }

    public async Task<ChangeProposal> CreateArticleProposalAsync(ArticleDraftOutput output, string topicName, IReadOnlyCollection<string> allowedSourceIds, CancellationToken cancellationToken = default)
    {
        var sourceIds = ValidateSubmittedSourceIds(output.SourceRevisionIds, allowedSourceIds);
        var sources = await RequireSourcesAsync(sourceIds, cancellationToken).ConfigureAwait(false);
        // Topic name is the stable article identity in the P2 baseline. Codex may
        // suggest prose, but it cannot silently rename a user-pinned topic or cause a
        // second near-duplicate article on the next run.
        var title = topicName.Trim();
        if (title.Length is < 1 or > 160) throw new AgentValidationException("SUBMISSION_INVALID", "提交的文章标题无效。");
        var articleId = Guid.CreateVersion7(_timeProvider.GetUtcNow()).ToString();
        var relativePath = $"Writing/Articles/{SafeFileName(title)}--{articleId[..8]}.md";
        var content = ComposeArticle(articleId, title, StripFrontmatter(output.MarkdownBody), sources);
        var key = ProposalKey("create", articleId, null, sourceIds, content);
        return await _database.InsertProposalAsync(new ChangeProposal
        {
            ProposalId = Guid.CreateVersion7(_timeProvider.GetUtcNow()).ToString(),
            IdempotencyKey = key,
            ArticleId = articleId,
            TargetPath = relativePath,
            ProposalType = "create",
            RiskLevel = "medium",
            Status = "pending",
            Title = title,
            Rationale = output.Rationale.Trim(),
            ProposedContent = content,
            SourceRevisionIds = sourceIds,
            CreatedAt = _timeProvider.GetUtcNow()
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ChangeProposal> CreateRevisionProposalAsync(ArticleRecord article, ArticlePatchOutput output, IReadOnlyCollection<string> allowedSourceIds, CancellationToken cancellationToken = default)
    {
        var path = _paths.ResolveDocumentPath(article.RelativePath);
        var current = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var currentHash = MarkdownEntrySerializer.Sha256(current);
        if (!string.Equals(currentHash, article.CurrentHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("文章已在外部修改，请先同步文章状态后重新生成提案。");

        var sourceIds = ValidateSubmittedSourceIds(output.SourceRevisionIds, allowedSourceIds);
        var allSourceIds = ExtractSourceIds(current).Concat(sourceIds).Distinct(StringComparer.Ordinal).ToArray();
        var sources = await RequireSourcesAsync(allSourceIds, cancellationToken).ConfigureAwait(false);
        var proposed = ApplyTextChanges(current, output.Changes);
        proposed = ReplaceSourceSection(proposed, sources);
        var key = ProposalKey("revise", article.ArticleId, article.CurrentHash, sourceIds, proposed);
        return await _database.InsertProposalAsync(new ChangeProposal
        {
            ProposalId = Guid.CreateVersion7(_timeProvider.GetUtcNow()).ToString(),
            IdempotencyKey = key,
            ArticleId = article.ArticleId,
            TargetPath = article.RelativePath,
            ProposalType = "revise",
            RiskLevel = "high",
            Status = "pending",
            BaseHash = article.CurrentHash,
            Title = article.Title,
            Rationale = output.Rationale.Trim(),
            ProposedContent = proposed,
            SourceRevisionIds = sourceIds,
            CreatedAt = _timeProvider.GetUtcNow()
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ChangeProposal> CreateWeeklyReviewProposalAsync(WeeklyReviewOutput output, DateOnly weekDate, IReadOnlyCollection<string> allowedSourceIds, CancellationToken cancellationToken = default)
    {
        var sourceIds = ValidateSubmittedSourceIds(output.SourceRevisionIds, allowedSourceIds);
        var sources = await RequireSourcesAsync(sourceIds, cancellationToken).ConfigureAwait(false);
        var monday = weekDate.AddDays(-(((int)weekDate.DayOfWeek + 6) % 7));
        var title = string.IsNullOrWhiteSpace(output.Title) ? $"{monday:yyyy-MM-dd} 周回顾" : output.Title.Trim();
        var path = $"Reviews/Weekly/{monday:yyyy-MM-dd}.md";
        var content = $"# {title}\n\n{StripFrontmatter(output.MarkdownBody).Trim()}\n\n{RenderSources(sources)}";
        var target = _paths.ResolveDocumentPath(path);
        var baseHash = File.Exists(target) ? MarkdownEntrySerializer.Sha256(await File.ReadAllTextAsync(target, cancellationToken).ConfigureAwait(false)) : null;
        var key = ProposalKey("weekly_review", path, baseHash, sourceIds, content);
        return await _database.InsertProposalAsync(new ChangeProposal
        {
            ProposalId = Guid.CreateVersion7(_timeProvider.GetUtcNow()).ToString(),
            IdempotencyKey = key,
            ArticleId = null,
            TargetPath = path,
            ProposalType = "weekly_review",
            RiskLevel = "low",
            Status = "pending",
            BaseHash = baseHash,
            Title = title,
            Rationale = "根据本周活跃来源生成回顾。",
            ProposedContent = content,
            SourceRevisionIds = sourceIds,
            CreatedAt = _timeProvider.GetUtcNow()
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ChangeProposal> CreateMonthlyReviewProposalAsync(WeeklyReviewOutput output, DateOnly monthDate, IReadOnlyCollection<string> allowedSourceIds, CancellationToken cancellationToken = default)
    {
        var sourceIds = ValidateSubmittedSourceIds(output.SourceRevisionIds, allowedSourceIds);
        var sources = await RequireSourcesAsync(sourceIds, cancellationToken).ConfigureAwait(false);
        var month = new DateOnly(monthDate.Year, monthDate.Month, 1);
        var title = string.IsNullOrWhiteSpace(output.Title) ? $"{month:yyyy-MM} 月回顾" : output.Title.Trim();
        var path = $"Reviews/Monthly/{month:yyyy-MM}.md";
        var content = $"# {title}\n\n{StripFrontmatter(output.MarkdownBody).Trim()}\n\n{RenderSources(sources)}";
        var target = _paths.ResolveDocumentPath(path);
        var baseHash = File.Exists(target) ? MarkdownEntrySerializer.Sha256(await File.ReadAllTextAsync(target, cancellationToken).ConfigureAwait(false)) : null;
        var key = ProposalKey("monthly_review", path, baseHash, sourceIds, content);
        return await _database.InsertProposalAsync(new ChangeProposal
        {
            ProposalId = Guid.CreateVersion7(_timeProvider.GetUtcNow()).ToString(),
            IdempotencyKey = key,
            ArticleId = null,
            TargetPath = path,
            ProposalType = "monthly_review",
            RiskLevel = "medium",
            Status = "pending",
            BaseHash = baseHash,
            Title = title,
            Rationale = "根据本月活跃来源生成回顾与结构建议。",
            ProposedContent = content,
            SourceRevisionIds = sourceIds,
            CreatedAt = _timeProvider.GetUtcNow()
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProposalApplyResult> ApproveAndApplyAsync(string proposalId, CancellationToken cancellationToken = default)
    {
        var proposal = await _database.GetProposalAsync(proposalId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("未找到提案。");
        if (proposal.Status == "applied") return new ProposalApplyResult(proposalId, "applied", proposal.ArticleId, proposal.TargetPath, "提案此前已经应用，未重复写入。");
        if (proposal.Status is "rejected" or "conflicted") return new ProposalApplyResult(proposalId, proposal.Status, proposal.ArticleId, proposal.TargetPath, "提案已结束，不能应用。");

        var sources = await _database.GetSourcesByRevisionIdsAsync(proposal.SourceRevisionIds.ToArray(), cancellationToken).ConfigureAwait(false);
        if (sources.Count != proposal.SourceRevisionIds.Count || sources.Any(source => !source.Active))
        {
            await _database.UpdateProposalStatusAsync(proposalId, "failed", errorCode: "SOURCE_MISSING", cancellationToken: cancellationToken).ConfigureAwait(false);
            return new ProposalApplyResult(proposalId, "failed", proposal.ArticleId, proposal.TargetPath, "提案来源已缺失或失效，文件未修改。");
        }

        var target = _paths.ResolveDocumentPath(proposal.TargetPath);
        var exists = File.Exists(target);
        var current = exists ? await File.ReadAllTextAsync(target, cancellationToken).ConfigureAwait(false) : null;
        var currentHash = current is null ? null : MarkdownEntrySerializer.Sha256(current);
        var proposedHash = MarkdownEntrySerializer.Sha256(proposal.ProposedContent);
        if (string.Equals(currentHash, proposedHash, StringComparison.OrdinalIgnoreCase))
        {
            await CommitAppliedStateAsync(proposal, current!, proposedHash, cancellationToken).ConfigureAwait(false);
            return new ProposalApplyResult(proposalId, "applied", proposal.ArticleId, proposal.TargetPath, "检测到文件此前已成功写入，已恢复运行账本。");
        }
        var conflict = proposal.BaseHash is null ? exists : !string.Equals(proposal.BaseHash, currentHash, StringComparison.OrdinalIgnoreCase);
        if (conflict)
        {
            await _database.UpdateProposalStatusAsync(proposalId, "conflicted", errorCode: "BASE_HASH_MISMATCH", cancellationToken: cancellationToken).ConfigureAwait(false);
            return new ProposalApplyResult(proposalId, "conflicted", proposal.ArticleId, proposal.TargetPath, "目标文件与提案基线不一致，文件未修改。");
        }

        await _database.UpdateProposalStatusAsync(proposalId, "approved", cancellationToken: cancellationToken).ConfigureAwait(false);
        try
        {
            await DurableAtomicFile.WriteAllTextAsync(target, proposal.ProposedContent, cancellationToken).ConfigureAwait(false);
            var verified = await File.ReadAllTextAsync(target, cancellationToken).ConfigureAwait(false);
            var resultHash = MarkdownEntrySerializer.Sha256(verified);
            if (!string.Equals(resultHash, proposedHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("文章写入后回读校验失败。");
            await CommitAppliedStateAsync(proposal, verified, resultHash, cancellationToken).ConfigureAwait(false);
            return new ProposalApplyResult(proposalId, "applied", proposal.ArticleId, proposal.TargetPath, "已原子写入并回读验证。");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await _database.UpdateProposalStatusAsync(proposalId, "failed", errorCode: "ARTICLE_WRITE_FAILED", cancellationToken: cancellationToken).ConfigureAwait(false);
            return new ProposalApplyResult(proposalId, "failed", proposal.ArticleId, proposal.TargetPath, exception.Message);
        }
    }

    private async Task CommitAppliedStateAsync(ChangeProposal proposal, string verified, string resultHash, CancellationToken cancellationToken)
    {
        if (proposal.ArticleId is not null)
        {
            if (!TryParseArticleMetadata(verified, out var articleId, out var status, out var mode, out var title) || articleId != proposal.ArticleId)
                throw new IOException("文章元数据与提案身份不一致。");
            var existingArticle = await _database.GetArticleAsync(articleId, cancellationToken).ConfigureAwait(false);
            if (existingArticle is null || !string.Equals(existingArticle.CurrentHash, resultHash, StringComparison.OrdinalIgnoreCase))
            {
                var article = new ArticleRecord
                {
                    ArticleId = articleId,
                    RelativePath = proposal.TargetPath,
                    Title = title,
                    Status = status,
                    MaintenanceMode = mode,
                    CurrentHash = resultHash,
                    RevisionNumber = (existingArticle?.RevisionNumber ?? 0) + 1,
                    UpdatedAt = _timeProvider.GetUtcNow()
                };
                await _database.UpsertArticleRevisionAsync(article, verified, "agent:approved", proposal.SourceRevisionIds, proposal.ProposalId, cancellationToken).ConfigureAwait(false);
            }
        }
        await _database.UpdateProposalStatusAsync(proposal.ProposalId, "applied", proposal.ArticleId, resultHash, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task RejectAsync(string proposalId, CancellationToken cancellationToken = default)
    {
        var proposal = await _database.GetProposalAsync(proposalId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("未找到提案。");
        if (proposal.Status == "applied") throw new InvalidOperationException("已应用提案不能拒绝，请使用 rollback。");
        await _database.UpdateProposalStatusAsync(proposalId, "rejected", cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProposalApplyResult> RollbackArticleAsync(string articleId, CancellationToken cancellationToken = default)
    {
        var article = await _database.GetArticleAsync(articleId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("未找到文章。");
        if (article.RevisionNumber < 2) return new ProposalApplyResult("", "failed", articleId, article.RelativePath, "没有可回滚的前一版本。");
        var path = _paths.ResolveDocumentPath(article.RelativePath);
        var current = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(MarkdownEntrySerializer.Sha256(current), article.CurrentHash, StringComparison.OrdinalIgnoreCase))
            return new ProposalApplyResult("", "conflicted", articleId, article.RelativePath, "文章已被外部修改，未执行回滚。");
        var previous = await _database.GetArticleRevisionAsync(articleId, article.RevisionNumber - 1, cancellationToken).ConfigureAwait(false);
        if (previous is null) return new ProposalApplyResult("", "failed", articleId, article.RelativePath, "历史版本缺失。");

        await DurableAtomicFile.WriteAllTextAsync(path, previous.Value.Content, cancellationToken).ConfigureAwait(false);
        var verified = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var hash = MarkdownEntrySerializer.Sha256(verified);
        var rolledBack = article with { CurrentHash = hash, RevisionNumber = article.RevisionNumber + 1, UpdatedAt = _timeProvider.GetUtcNow() };
        await _database.UpsertArticleRevisionAsync(rolledBack, verified, "rollback", ExtractSourceIds(verified), null, cancellationToken).ConfigureAwait(false);
        return new ProposalApplyResult("", "applied", articleId, article.RelativePath, $"已恢复版本 {previous.Value.RevisionNumber}，并记录新的回滚 revision。");
    }

    public async Task<string> GetProposalDiffAsync(string proposalId, CancellationToken cancellationToken = default)
    {
        var proposal = await _database.GetProposalAsync(proposalId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("未找到提案。");
        var old = string.Empty;
        if (proposal.ArticleId is not null && proposal.BaseHash is not null)
            old = (await _database.GetArticleRevisionByHashAsync(proposal.ArticleId, proposal.BaseHash, cancellationToken).ConfigureAwait(false))?.Content ?? string.Empty;
        else if (proposal.BaseHash is not null)
        {
            var path = _paths.ResolveDocumentPath(proposal.TargetPath);
            if (File.Exists(path)) old = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        return TextDiff.Create(old, proposal.ProposedContent);
    }

    private async Task<IReadOnlyList<SourceRevision>> RequireSourcesAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0) throw new AgentValidationException("SOURCE_REQUIRED", "文章或回顾必须至少引用一个来源 revision。");
        var sources = await _database.GetSourcesByRevisionIdsAsync(ids.ToArray(), cancellationToken).ConfigureAwait(false);
        if (sources.Count != ids.Count || sources.Any(source => !source.Active))
            throw new AgentValidationException("SOURCE_MISSING", "提交引用了不存在或已失效的来源 revision。");
        return sources;
    }

    private static IReadOnlyList<string> ValidateSubmittedSourceIds(IEnumerable<string> returned, IReadOnlyCollection<string> allowed)
    {
        var allowedSet = allowed.ToHashSet(StringComparer.Ordinal);
        var result = returned.Distinct(StringComparer.Ordinal).ToArray();
        if (result.Length == 0)
            throw new AgentValidationException("SOURCE_REQUIRED", "提交必须至少引用一个本轮工作集来源。");
        if (result.Any(id => !allowedSet.Contains(id)))
            throw new AgentValidationException("SOURCE_OUT_OF_SCOPE", "提交引用了工作集之外的来源 ID。");
        return result;
    }

    private static string ComposeArticle(string articleId, string title, string body, IReadOnlyList<SourceRevision> sources) =>
        $"---\npkws_schema: 1\narticle_id: {articleId}\nstatus: seed\nmaintenance_mode: propose\ntitle: {YamlScalar(title)}\n---\n\n# {title}\n\n{body.Trim()}\n\n{RenderSources(sources)}";

    private static string RenderSources(IReadOnlyList<SourceRevision> sources)
    {
        var ids = JsonSerializer.Serialize(sources.Select(source => source.RevisionId));
        var builder = new StringBuilder();
        builder.AppendLine("## 来源记录").Append(SourcesMarker).Append(ids).AppendLine(" -->");
        foreach (var source in sources)
        {
            var date = source.CreatedAt?.ToString("yyyy-MM-dd HH:mm") ?? "时间未知";
            builder.Append("- [[").Append(source.FilePath.Replace(".md", string.Empty, StringComparison.OrdinalIgnoreCase))
                .Append('|').Append(date).Append("]] <!-- pkws:source:").Append(source.RevisionId).AppendLine(" -->");
        }
        return builder.ToString().TrimEnd() + "\n";
    }

    private static string ReplaceSourceSection(string content, IReadOnlyList<SourceRevision> sources)
    {
        var marker = content.IndexOf("\n## 来源记录", StringComparison.Ordinal);
        var head = marker < 0 ? content.TrimEnd() : content[..marker].TrimEnd();
        return head + "\n\n" + RenderSources(sources);
    }

    private static IReadOnlyList<string> ExtractSourceIds(string content)
    {
        var start = content.IndexOf(SourcesMarker, StringComparison.Ordinal);
        if (start < 0) return [];
        var end = content.IndexOf("-->", start + SourcesMarker.Length, StringComparison.Ordinal);
        if (end < 0) return [];
        try { return JsonSerializer.Deserialize<string[]>(content[(start + SourcesMarker.Length)..end].Trim()) ?? []; }
        catch (JsonException) { return []; }
    }

    private static string ApplyTextChanges(string content, IReadOnlyList<ArticleTextChange> changes)
    {
        if (changes.Count == 0) throw new AgentValidationException("SUBMISSION_INVALID", "修订提交没有包含任何局部修改。");
        var result = content;
        var bodyStart = result.IndexOf("---", 3, StringComparison.Ordinal);
        bodyStart = bodyStart < 0 ? 0 : bodyStart + 3;
        foreach (var change in changes)
        {
            if (string.IsNullOrEmpty(change.Find)) throw new AgentValidationException("SUBMISSION_INVALID", "局部修改的 find 不能为空。");
            var index = result.IndexOf(change.Find, StringComparison.Ordinal);
            if (index < bodyStart || index < 0 || result.IndexOf(change.Find, index + change.Find.Length, StringComparison.Ordinal) >= 0)
                throw new AgentValidationException("PATCH_ANCHOR_INVALID", "局部修改锚点缺失、重复或触及受保护元数据。");
            if (TouchesLockedRegion(result, index, change.Find.Length))
                throw new AgentValidationException("LOCKED_REGION", "局部修改触及用户锁定区域。");
            result = result[..index] + change.Replace + result[(index + change.Find.Length)..];
        }
        return result;
    }

    private static bool TouchesLockedRegion(string content, int start, int length)
    {
        const string open = "<!-- pkws:locked:start -->";
        const string close = "<!-- pkws:locked:end -->";
        var cursor = 0;
        while ((cursor = content.IndexOf(open, cursor, StringComparison.Ordinal)) >= 0)
        {
            var end = content.IndexOf(close, cursor + open.Length, StringComparison.Ordinal);
            if (end < 0) end = content.Length;
            if (start < end + close.Length && start + length > cursor) return true;
            cursor = Math.Min(content.Length, end + close.Length);
        }
        return false;
    }

    private static bool TryParseArticleMetadata(string content, out string articleId, out string status, out string mode, out string title)
    {
        articleId = status = mode = title = string.Empty;
        if (!content.StartsWith("---", StringComparison.Ordinal)) return false;
        var end = content.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0) return false;
        foreach (var line in content[3..end].Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            var separator = line.IndexOf(':');
            if (separator < 0) continue;
            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim().Trim('"').Replace("\\\"", "\"", StringComparison.Ordinal);
            if (key == "article_id") articleId = value;
            else if (key == "status") status = value;
            else if (key == "maintenance_mode") mode = value;
            else if (key == "title") title = value;
        }
        if (!Guid.TryParse(articleId, out _)) return false;
        if (string.IsNullOrWhiteSpace(status)) status = "active";
        if (string.IsNullOrWhiteSpace(mode)) mode = "propose";
        if (string.IsNullOrWhiteSpace(title))
        {
            var heading = content.IndexOf("\n# ", end, StringComparison.Ordinal);
            if (heading >= 0)
            {
                var lineEnd = content.IndexOf('\n', heading + 4);
                title = content[(heading + 3)..(lineEnd < 0 ? content.Length : lineEnd)].Trim();
            }
        }
        return !string.IsNullOrWhiteSpace(title);
    }

    private static string StripFrontmatter(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith("---", StringComparison.Ordinal)) return trimmed;
        var end = trimmed.IndexOf("\n---", 3, StringComparison.Ordinal);
        return end < 0 ? trimmed : trimmed[(end + 4)..].Trim();
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var cleaned = new string(value.Select(character => invalid.Contains(character) ? '-' : character).ToArray()).Trim().TrimEnd('.');
        return cleaned.Length > 80 ? cleaned[..80].TrimEnd() : cleaned;
    }

    private static string YamlScalar(string value) => $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    private static string ProposalKey(string type, string target, string? baseHash, IEnumerable<string> sourceIds, string content) =>
        MarkdownEntrySerializer.Sha256(string.Join('|', new[] { type, target, baseHash ?? "", string.Join(',', sourceIds.Order()), MarkdownEntrySerializer.Sha256(content) }));
}

public static class DurableAtomicFile
{
    public static async Task WriteAllTextAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("文件路径缺少目录。");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                await writer.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

public static class TextDiff
{
    public static string Create(string oldText, string newText)
    {
        var oldLines = oldText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var newLines = newText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var rows = oldLines.Length + 1;
        var columns = newLines.Length + 1;
        if ((long)rows * columns > 2_000_000)
            return $"--- baseline ({oldText.Length} chars)\n+++ proposed ({newText.Length} chars)\n[diff 过大，请打开提案全文审阅]";
        var lcs = new int[rows, columns];
        for (var i = oldLines.Length - 1; i >= 0; i--)
            for (var j = newLines.Length - 1; j >= 0; j--)
                lcs[i, j] = oldLines[i] == newLines[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        var builder = new StringBuilder("--- baseline\n+++ proposed\n");
        var x = 0; var y = 0;
        while (x < oldLines.Length || y < newLines.Length)
        {
            if (x < oldLines.Length && y < newLines.Length && oldLines[x] == newLines[y]) { builder.Append(' ').AppendLine(oldLines[x]); x++; y++; }
            else if (y < newLines.Length && (x == oldLines.Length || lcs[x, y + 1] >= lcs[x + 1, y])) { builder.Append('+').AppendLine(newLines[y++]); }
            else { builder.Append('-').AppendLine(oldLines[x++]); }
        }
        return builder.ToString();
    }
}
