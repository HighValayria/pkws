using System.Text.Json.Serialization;

namespace Pkws.Core;

public sealed record SourceContext
{
    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    [JsonPropertyName("work_id")]
    public string? WorkId { get; init; }

    [JsonPropertyName("work_title")]
    public string? WorkTitle { get; init; }

    [JsonPropertyName("authors")]
    public IReadOnlyList<string> Authors { get; init; } = [];

    [JsonPropertyName("chapter_id")]
    public string? ChapterId { get; init; }

    [JsonPropertyName("chapter_title")]
    public string? ChapterTitle { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("selected_text")]
    public string? SelectedText { get; init; }

    [JsonPropertyName("selection_prefix")]
    public string? SelectionPrefix { get; init; }

    [JsonPropertyName("selection_suffix")]
    public string? SelectionSuffix { get; init; }

    [JsonPropertyName("captured_at")]
    public DateTimeOffset? CapturedAt { get; init; }

    [JsonPropertyName("extractor_version")]
    public string? ExtractorVersion { get; init; }
}

public sealed record CaptureRequest
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("request_id")]
    public required string RequestId { get; init; }

    [JsonPropertyName("entry_id")]
    public string? EntryId { get; init; }

    [JsonPropertyName("source_type")]
    public string SourceType { get; init; } = "desktop";

    [JsonPropertyName("content")]
    public required string Content { get; init; }

    [JsonPropertyName("occurred_at")]
    public DateTimeOffset? OccurredAt { get; init; }

    [JsonPropertyName("source_context")]
    public SourceContext? SourceContext { get; init; }
}

public sealed record CaptureResponse(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("request_id")] string RequestId,
    [property: JsonPropertyName("entry_id")] string EntryId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("local_date")] DateOnly LocalDate,
    [property: JsonPropertyName("relative_path")] string RelativePath,
    [property: JsonPropertyName("duplicate")] bool Duplicate);

public sealed record CaptureStatusResponse(
    [property: JsonPropertyName("request_id")] string RequestId,
    [property: JsonPropertyName("entry_id")] string EntryId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("error_code")] string? ErrorCode,
    [property: JsonPropertyName("relative_path")] string? RelativePath);

public sealed record CaptureLedgerRecord
{
    public required string RequestId { get; init; }
    public required string EntryId { get; init; }
    public required string PayloadHash { get; init; }
    public required int SchemaVersion { get; init; }
    public required string SourceType { get; init; }
    public required string SubmittedContent { get; init; }
    public string? SourceContextJson { get; init; }
    public required DateTimeOffset ReceivedAt { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required string TimeZone { get; init; }
    public required DateOnly LocalDate { get; init; }
    public required string Status { get; init; }
    public required string RelativePath { get; init; }
    public required string RenderedMarkdown { get; init; }
    public string? FileHashAfter { get; init; }
    public DateTimeOffset? CommittedAt { get; init; }
    public string? ErrorCode { get; init; }
}
