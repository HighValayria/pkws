using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pkws.Core;

public sealed class MarkdownEntrySerializer
{
    private const string EntryMarkerPrefix = "<!-- pkws:entry:v1 ";
    private static readonly JsonSerializerOptions MetadataJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string Render(string entryId, DateTimeOffset createdAt, string timeZoneId, string sourceType, string content)
    {
        var closeMarker = ClosingMarker(entryId);
        if (content.Contains(closeMarker, StringComparison.Ordinal))
        {
            throw new CaptureException(
                "RESERVED_MARKER",
                "内容包含当前记录的保留结束标记，请删除该标记后重试。",
                false,
                400);
        }

        var metadata = new EntryMetadata(
            1,
            entryId,
            createdAt.ToUniversalTime().ToString("O"),
            timeZoneId,
            sourceType,
            Sha256(content),
            Encoding.UTF8.GetByteCount(content));

        var metadataJson = JsonSerializer.Serialize(metadata, MetadataJsonOptions);
        var localTime = TimeZoneInfo.ConvertTime(createdAt, TimeZoneResolver.Resolve(timeZoneId));
        var builder = new StringBuilder(content.Length + 320);
        builder.Append("<!-- pkws:entry:v1 ").Append(metadataJson).AppendLine(" -->");
        builder.AppendLine();
        builder.Append("### ").AppendLine(localTime.ToString("HH:00"));
        builder.AppendLine();
        builder.Append(content);
        if (!content.EndsWith('\n'))
        {
            builder.AppendLine();
        }

        builder.AppendLine();
        builder.AppendLine(closeMarker);
        return builder.ToString();
    }

    public static string OpeningPrefix(string entryId) => $"<!-- pkws:entry:v1 {{\"v\":1,\"id\":\"{entryId}\"";

    public static string ClosingMarker(string entryId) => $"<!-- /pkws:entry:{entryId} -->";

    public static bool ContainsCompleteEntry(string markdown, string entryId) =>
        markdown.Contains(OpeningPrefix(entryId), StringComparison.Ordinal) &&
        markdown.Contains(ClosingMarker(entryId), StringComparison.Ordinal);

    public static bool ContainsOpening(string markdown, string entryId) =>
        markdown.Contains(OpeningPrefix(entryId), StringComparison.Ordinal);

    public IReadOnlyList<MarkdownNoteBlock> ParseEntries(string markdown)
    {
        var entries = new List<MarkdownNoteBlock>();
        var searchFrom = 0;
        while (searchFrom < markdown.Length)
        {
            var openingStart = markdown.IndexOf(EntryMarkerPrefix, searchFrom, StringComparison.Ordinal);
            if (openingStart < 0)
            {
                break;
            }

            var openingEnd = markdown.IndexOf("-->", openingStart + EntryMarkerPrefix.Length, StringComparison.Ordinal);
            if (openingEnd < 0)
            {
                break;
            }

            EntryMetadata? metadata;
            try
            {
                var json = markdown[(openingStart + EntryMarkerPrefix.Length)..openingEnd].Trim();
                metadata = JsonSerializer.Deserialize<EntryMetadata>(json, MetadataJsonOptions);
            }
            catch (JsonException)
            {
                searchFrom = openingEnd + 3;
                continue;
            }

            if (metadata is null || string.IsNullOrWhiteSpace(metadata.Id))
            {
                searchFrom = openingEnd + 3;
                continue;
            }

            var closingMarker = ClosingMarker(metadata.Id);
            var closingStart = markdown.IndexOf(closingMarker, openingEnd + 3, StringComparison.Ordinal);
            if (closingStart < 0)
            {
                break;
            }

            var bodyStart = SkipAllLineBreaks(markdown, openingEnd + 3);
            var headingEnd = IndexOfLineEnd(markdown, bodyStart);
            if (headingEnd < 0)
            {
                searchFrom = closingStart + closingMarker.Length;
                continue;
            }

            var heading = markdown[bodyStart..headingEnd].Trim();
            var contentStart = SkipLineBreaks(markdown, headingEnd, 2);
            var rawContent = markdown[contentStart..closingStart];
            var content = RestoreContent(rawContent, metadata.ContentSha256);
            var hour = ResolveHour(metadata, heading);
            entries.Add(new MarkdownNoteBlock(metadata.Id, hour, content));
            searchFrom = closingStart + closingMarker.Length;
        }

        return entries;
    }

    public static string Sha256(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(hash);
    }

    private static int IndexOfLineEnd(string value, int start)
    {
        var index = value.IndexOfAny(['\r', '\n'], start);
        return index < 0 ? value.Length : index;
    }

    private static int SkipLineBreaks(string value, int start, int count)
    {
        var index = start;
        for (var i = 0; i < count && index < value.Length; i++)
        {
            if (value[index] == '\r')
            {
                index++;
                if (index < value.Length && value[index] == '\n')
                {
                    index++;
                }
            }
            else if (value[index] == '\n')
            {
                index++;
            }
            else
            {
                break;
            }
        }

        return index;
    }

    private static int SkipAllLineBreaks(string value, int start)
    {
        var index = start;
        while (index < value.Length && value[index] is '\r' or '\n')
        {
            index = SkipLineBreaks(value, index, 1);
        }

        return index;
    }

    private static string RestoreContent(string rawContent, string contentSha256)
    {
        foreach (var candidate in ContentCandidates(rawContent))
        {
            if (string.Equals(Sha256(candidate), contentSha256, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return rawContent.TrimEnd('\r', '\n');
    }

    private static IEnumerable<string> ContentCandidates(string rawContent)
    {
        yield return RemoveTrailingLineBreaks(rawContent, 2);
        yield return RemoveTrailingLineBreaks(rawContent, 1);
        yield return rawContent;
    }

    private static string RemoveTrailingLineBreaks(string value, int count)
    {
        var end = value.Length;
        for (var i = 0; i < count && end > 0; i++)
        {
            if (value[end - 1] == '\n')
            {
                end--;
                if (end > 0 && value[end - 1] == '\r')
                {
                    end--;
                }
            }
            else if (value[end - 1] == '\r')
            {
                end--;
            }
            else
            {
                break;
            }
        }

        return value[..end];
    }

    private static string ResolveHour(EntryMetadata metadata, string heading)
    {
        if (DateTimeOffset.TryParse(metadata.CreatedAt, out var createdAt))
        {
            try
            {
                return TimeZoneInfo.ConvertTime(createdAt, TimeZoneResolver.Resolve(metadata.TimeZone)).ToString("HH:00");
            }
            catch (TimeZoneNotFoundException)
            {
                // Fall through to the human-readable heading.
            }
        }

        if (heading.StartsWith("### ", StringComparison.Ordinal) && heading.Length >= 8)
        {
            return $"{heading.AsSpan(4, 2)}:00";
        }

        return "--:00";
    }

    private sealed record EntryMetadata(
        [property: JsonPropertyName("v")] int Version,
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("created_at")] string CreatedAt,
        [property: JsonPropertyName("tz")] string TimeZone,
        [property: JsonPropertyName("source")] string Source,
        [property: JsonPropertyName("content_sha256")] string ContentSha256,
        [property: JsonPropertyName("content_bytes")] int ContentBytes);
}

public sealed record MarkdownNoteBlock(string EntryId, string Hour, string Content);
