using System.Text.Json.Serialization;

namespace Pkws.Agent;

public sealed record AgentConfig
{
    public string DocumentRoot { get; init; } = string.Empty;
    public string DataRoot { get; init; } = string.Empty;
    public string CaptureDatabasePath { get; init; } = string.Empty;
    public int MaxWorksetCharacters { get; init; } = 40_000;
    public int MaxFragmentsPerWorkset { get; init; } = 50;
    public int ProposalBudgetPerSubmission { get; init; } = 3;
    public string AnalysisVersion { get; init; } = "codex-p2-v1";
}

public sealed record ScanSummary(
    int FilesSeen,
    int SourcesSeen,
    int AddedRevisions,
    int UnchangedSources,
    int RemovedSources,
    int FragmentsCreated,
    int Diagnostics);

public sealed record SourceRevision
{
    public required string RevisionId { get; init; }
    public required string SourceKey { get; init; }
    public string? EntryId { get; init; }
    public required string FilePath { get; init; }
    public required string Content { get; init; }
    public required string ContentHash { get; init; }
    public required string SourceType { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public string? SourceContextJson { get; init; }
    public required DateTimeOffset ObservedAt { get; init; }
    public string? PredecessorId { get; init; }
    public bool Active { get; init; }
}

public sealed record SemanticFragment
{
    public required string FragmentId { get; init; }
    public required string SourceRevisionId { get; init; }
    public required int Ordinal { get; init; }
    public required string Text { get; init; }
    public required int StartOffset { get; init; }
    public required int EndOffset { get; init; }
    public IReadOnlyList<string> Topics { get; init; } = [];
    public IReadOnlyList<string> Entities { get; init; } = [];
    public string EpistemicStatus { get; init; } = "unknown";
    public string Action { get; init; } = "index_only";
    public string ExtractorVersion { get; init; } = "paragraph-v1";
    public bool Active { get; init; }
}

public sealed record SearchHit(
    string FragmentId,
    string SourceRevisionId,
    string FilePath,
    string? EntryId,
    string SourceType,
    DateTimeOffset? CreatedAt,
    string Text,
    IReadOnlyList<string> Topics,
    double Rank);

public sealed record TopicRecord
{
    public required string TopicId { get; init; }
    public required string Name { get; init; }
    public IReadOnlyList<string> Aliases { get; init; } = [];
    public string? Direction { get; init; }
    public required string Origin { get; init; }
    public required string Status { get; init; }
    public bool Pinned { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record ArticleRecord
{
    public required string ArticleId { get; init; }
    public required string RelativePath { get; init; }
    public required string Title { get; init; }
    public required string Status { get; init; }
    public required string MaintenanceMode { get; init; }
    public required string CurrentHash { get; init; }
    public required int RevisionNumber { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record ChangeProposal
{
    public required string ProposalId { get; init; }
    public required string IdempotencyKey { get; init; }
    public string? ArticleId { get; init; }
    public required string TargetPath { get; init; }
    public required string ProposalType { get; init; }
    public required string RiskLevel { get; init; }
    public required string Status { get; init; }
    public string? BaseHash { get; init; }
    public required string Title { get; init; }
    public required string Rationale { get; init; }
    public required string ProposedContent { get; init; }
    public required IReadOnlyList<string> SourceRevisionIds { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ReviewedAt { get; init; }
    public DateTimeOffset? AppliedAt { get; init; }
    public string? ResultHash { get; init; }
    public string? ErrorCode { get; init; }
}

public sealed record AgentRun
{
    public required string RunId { get; init; }
    public required string RunType { get; init; }
    public required string Status { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public int ItemsProcessed { get; init; }
    public int ProposalsCreated { get; init; }
    public string? ErrorCode { get; init; }
}

public sealed record FragmentAnalysis(
    [property: JsonPropertyName("fragment_id")] string FragmentId,
    [property: JsonPropertyName("topics")] IReadOnlyList<string> Topics,
    [property: JsonPropertyName("entities")] IReadOnlyList<string> Entities,
    [property: JsonPropertyName("epistemic_status")] string EpistemicStatus,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("rationale")] string Rationale);

public sealed record TopicCandidate(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("direction")] string Direction,
    [property: JsonPropertyName("fragment_ids")] IReadOnlyList<string> FragmentIds);

public sealed record ArticleDraftOutput(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("markdown_body")] string MarkdownBody,
    [property: JsonPropertyName("rationale")] string Rationale,
    [property: JsonPropertyName("source_revision_ids")] IReadOnlyList<string> SourceRevisionIds);

public sealed record ArticlePatchOutput(
    [property: JsonPropertyName("rationale")] string Rationale,
    [property: JsonPropertyName("changes")] IReadOnlyList<ArticleTextChange> Changes,
    [property: JsonPropertyName("source_revision_ids")] IReadOnlyList<string> SourceRevisionIds);

public sealed record ArticleTextChange(
    [property: JsonPropertyName("find")] string Find,
    [property: JsonPropertyName("replace")] string Replace,
    [property: JsonPropertyName("reason")] string Reason);

public sealed record WeeklyReviewOutput(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("markdown_body")] string MarkdownBody,
    [property: JsonPropertyName("source_revision_ids")] IReadOnlyList<string> SourceRevisionIds);

public sealed record CodexSourceContextSummary(
    [property: JsonPropertyName("provider")] string? Provider,
    [property: JsonPropertyName("work_id")] string? WorkId,
    [property: JsonPropertyName("work_title")] string? WorkTitle,
    [property: JsonPropertyName("authors")] IReadOnlyList<string> Authors,
    [property: JsonPropertyName("chapter_id")] string? ChapterId,
    [property: JsonPropertyName("chapter_title")] string? ChapterTitle,
    [property: JsonPropertyName("has_selected_text")] bool HasSelectedText);

public sealed record CodexWorksetFragment(
    [property: JsonPropertyName("fragment_id")] string FragmentId,
    [property: JsonPropertyName("source_revision_id")] string SourceRevisionId,
    [property: JsonPropertyName("source_type")] string SourceType,
    [property: JsonPropertyName("source_path")] string SourcePath,
    [property: JsonPropertyName("created_at")] DateTimeOffset? CreatedAt,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("existing_topics")] IReadOnlyList<string> ExistingTopics,
    [property: JsonPropertyName("existing_epistemic_status")] string ExistingEpistemicStatus,
    [property: JsonPropertyName("source_context")] CodexSourceContextSummary? SourceContext);

public sealed record CodexArticleSummary(
    [property: JsonPropertyName("article_id")] string ArticleId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("relative_path")] string RelativePath,
    [property: JsonPropertyName("current_hash")] string CurrentHash,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("maintenance_mode")] string MaintenanceMode);

public sealed record CodexWorkset
{
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; init; } = 1;
    [JsonPropertyName("workset_id")] public required string WorksetId { get; init; }
    [JsonPropertyName("run_type")] public required string RunType { get; init; }
    [JsonPropertyName("topic_name")] public string? TopicName { get; init; }
    [JsonPropertyName("created_at")] public required DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("instructions")] public required IReadOnlyList<string> Instructions { get; init; }
    [JsonPropertyName("fragments")] public required IReadOnlyList<CodexWorksetFragment> Fragments { get; init; }
    [JsonPropertyName("topics")] public required IReadOnlyList<TopicRecord> Topics { get; init; }
    [JsonPropertyName("articles")] public required IReadOnlyList<CodexArticleSummary> Articles { get; init; }
}

public sealed record CodexWorksetRecord
{
    public required string WorksetId { get; init; }
    public required string RunType { get; init; }
    public string? TopicName { get; init; }
    public required string Status { get; init; }
    public required IReadOnlyList<string> FragmentIds { get; init; }
    public required IReadOnlyList<string> SourceRevisionIds { get; init; }
    public required IReadOnlyDictionary<string, string> ArticleBaselines { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? SubmissionHash { get; init; }
    public IReadOnlyList<string> ProposalIds { get; init; } = [];
    public string? ErrorCode { get; init; }
}

public sealed record CodexChangeRequest
{
    [JsonPropertyName("change_type")] public required string ChangeType { get; init; }
    [JsonPropertyName("topic_name")] public string? TopicName { get; init; }
    [JsonPropertyName("article_id")] public string? ArticleId { get; init; }
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("markdown_body")] public string? MarkdownBody { get; init; }
    [JsonPropertyName("rationale")] public required string Rationale { get; init; }
    [JsonPropertyName("source_revision_ids")] public required IReadOnlyList<string> SourceRevisionIds { get; init; }
    [JsonPropertyName("changes")] public IReadOnlyList<ArticleTextChange> Changes { get; init; } = [];
}

public sealed record CodexSubmission
{
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; init; }
    [JsonPropertyName("workset_id")] public required string WorksetId { get; init; }
    [JsonPropertyName("analyses")] public required IReadOnlyList<FragmentAnalysis> Analyses { get; init; }
    [JsonPropertyName("topic_candidates")] public required IReadOnlyList<TopicCandidate> TopicCandidates { get; init; }
    [JsonPropertyName("changes")] public required IReadOnlyList<CodexChangeRequest> Changes { get; init; }
}

public sealed record CodexSubmissionResult(
    string WorksetId,
    string Status,
    int FragmentsAnalyzed,
    int TopicsUpdated,
    IReadOnlyList<string> ProposalIds,
    bool Duplicate);

public sealed record ProposalApplyResult(string ProposalId, string Status, string? ArticleId, string? RelativePath, string? Message);

public sealed record AgentStatus(
    int ActiveSources,
    int ActiveFragments,
    int Topics,
    int Articles,
    int PendingProposals,
    int PendingWorksets,
    AgentRun? LastRun);
