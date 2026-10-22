using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pkws.Core;

public interface ICaptureFaultInjector
{
    Task AfterFileVerifiedAsync(CaptureLedgerRecord record, CancellationToken cancellationToken);
}

public sealed class NoCaptureFaultInjector : ICaptureFaultInjector
{
    public static NoCaptureFaultInjector Instance { get; } = new();
    public Task AfterFileVerifiedAsync(CaptureLedgerRecord record, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class CaptureService
{
    public const int MaxContentBytes = 1024 * 1024;
    public const int MaxSelectedTextBytes = 128 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly PkwsConfig _config;
    private readonly CaptureLedger _ledger;
    private readonly IDailyNoteStore _store;
    private readonly MarkdownEntrySerializer _serializer;
    private readonly WereadMarkdownComposer _wereadComposer = new();
    private readonly TimeProvider _timeProvider;
    private readonly ICaptureFaultInjector _faultInjector;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public CaptureService(
        PkwsConfig config,
        CaptureLedger ledger,
        IDailyNoteStore store,
        MarkdownEntrySerializer serializer,
        TimeProvider? timeProvider = null,
        ICaptureFaultInjector? faultInjector = null)
    {
        _config = config;
        _ledger = ledger;
        _store = store;
        _serializer = serializer;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _faultInjector = faultInjector ?? NoCaptureFaultInjector.Instance;
    }

    public async Task<CaptureResponse> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        var payloadHash = ComputePayloadHash(request);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await _ledger.GetAsync(request.RequestId, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (!CryptographicOperations.FixedTimeEquals(
                        Encoding.ASCII.GetBytes(existing.PayloadHash),
                        Encoding.ASCII.GetBytes(payloadHash)))
                {
                    throw new CaptureException("IDEMPOTENCY_CONFLICT", "相同 request_id 已用于不同内容。", false, 409);
                }

                return await RecoverOrReturnAsync(existing, true, cancellationToken).ConfigureAwait(false);
            }

            var now = _timeProvider.GetUtcNow();
            var timeZone = TimeZoneResolver.Resolve(_config.TimeZoneId);
            var localTime = TimeZoneInfo.ConvertTime(now, timeZone);
            var localDate = DateOnly.FromDateTime(localTime.DateTime);
            var entryId = request.EntryId ?? Guid.CreateVersion7(now).ToString();
            if (!Guid.TryParse(entryId, out _))
            {
                throw new CaptureException("VALIDATION_FAILED", "entry_id 必须是有效 UUID。", false, 400);
            }

            if (await _ledger.GetByEntryIdAsync(entryId, cancellationToken).ConfigureAwait(false) is not null)
            {
                throw new CaptureException("ENTRY_ID_CONFLICT", "entry_id 已属于另一个请求。", false, 409);
            }

            var relativePath = _store.GetRelativePath(localDate);
            var visibleContent = request.SourceType == "weread"
                ? _wereadComposer.Compose(request.SourceContext!, request.Content)
                : request.Content;
            var rendered = _serializer.Render(entryId, now, _config.TimeZoneId, request.SourceType, visibleContent);
            var record = new CaptureLedgerRecord
            {
                RequestId = request.RequestId,
                EntryId = entryId,
                PayloadHash = payloadHash,
                SchemaVersion = request.SchemaVersion,
                SourceType = request.SourceType,
                SubmittedContent = request.Content,
                SourceContextJson = request.SourceContext is null ? null : JsonSerializer.Serialize(request.SourceContext, JsonOptions),
                ReceivedAt = now,
                CreatedAt = now,
                TimeZone = _config.TimeZoneId,
                LocalDate = localDate,
                Status = "received",
                RelativePath = relativePath,
                RenderedMarkdown = rendered
            };

            await _ledger.InsertReceivedAsync(record, cancellationToken).ConfigureAwait(false);
            return await WriteRecordAsync(record, false, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<int> RecoverPendingAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_config.DocumentRoot) || !Directory.Exists(_config.DocumentRoot))
        {
            return 0;
        }

        var recovered = 0;
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var record in await _ledger.GetRecoverableAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    var before = await _store.ReadAsync(record.RelativePath, cancellationToken).ConfigureAwait(false);
                    if (MarkdownEntrySerializer.ContainsCompleteEntry(before, record.EntryId))
                    {
                        await _ledger.MarkCommittedAsync(
                            record.RequestId,
                            MarkdownEntrySerializer.Sha256(before),
                            _timeProvider.GetUtcNow(),
                            cancellationToken).ConfigureAwait(false);
                        recovered++;
                    }
                    else if (MarkdownEntrySerializer.ContainsOpening(before, record.EntryId))
                    {
                        await _ledger.MarkFailedAsync(record.RequestId, "PARTIAL_WRITE_DETECTED", cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await WriteRecordAsync(record, true, cancellationToken).ConfigureAwait(false);
                        recovered++;
                    }
                }
                catch (CaptureException exception)
                {
                    try
                    {
                        await _ledger.MarkRecoverableAsync(
                            record.RequestId,
                            exception.Code,
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                        // A ledger failure must not prevent the desktop host from starting.
                    }
                }
                catch
                {
                    try
                    {
                        await _ledger.MarkRecoverableAsync(
                            record.RequestId,
                            "RECOVERY_FAILED",
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                        // A ledger failure must not prevent the desktop host from starting.
                    }
                }
            }
        }
        finally
        {
            _writeGate.Release();
        }

        return recovered;
    }

    public async Task<CaptureStatusResponse?> GetStatusAsync(string requestId, CancellationToken cancellationToken = default)
    {
        var record = await _ledger.GetAsync(requestId, cancellationToken).ConfigureAwait(false);
        return record is null
            ? null
            : new CaptureStatusResponse(record.RequestId, record.EntryId, record.Status, record.ErrorCode, record.RelativePath);
    }

    public async Task<(string RelativePath, string Content)> ReadTodayAsync(CancellationToken cancellationToken = default)
    {
        var local = TimeZoneInfo.ConvertTime(_timeProvider.GetUtcNow(), TimeZoneResolver.Resolve(_config.TimeZoneId));
        var relative = _store.GetRelativePath(DateOnly.FromDateTime(local.DateTime));
        return (relative, await _store.ReadAsync(relative, cancellationToken).ConfigureAwait(false));
    }

    private async Task<CaptureResponse> RecoverOrReturnAsync(
        CaptureLedgerRecord record,
        bool duplicate,
        CancellationToken cancellationToken)
    {
        if (record.Status == "committed")
        {
            return ToResponse(record, duplicate);
        }

        if (record.Status == "failed")
        {
            throw new CaptureException(record.ErrorCode ?? "CAPTURE_FAILED", "该请求处于不可自动恢复状态，请检查恢复报告。", false, 409);
        }

        var current = await _store.ReadAsync(record.RelativePath, cancellationToken).ConfigureAwait(false);
        if (MarkdownEntrySerializer.ContainsCompleteEntry(current, record.EntryId))
        {
            await _ledger.MarkCommittedAsync(
                record.RequestId,
                MarkdownEntrySerializer.Sha256(current),
                _timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
            return ToResponse(record with { Status = "committed" }, duplicate);
        }

        if (MarkdownEntrySerializer.ContainsOpening(current, record.EntryId))
        {
            await _ledger.MarkFailedAsync(record.RequestId, "PARTIAL_WRITE_DETECTED", cancellationToken).ConfigureAwait(false);
            throw new CaptureException("PARTIAL_WRITE_DETECTED", "检测到不完整记录块，已停止自动追加以避免损坏扩大。", false, 409);
        }

        return await WriteRecordAsync(record, duplicate, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CaptureResponse> WriteRecordAsync(
        CaptureLedgerRecord record,
        bool duplicate,
        CancellationToken cancellationToken)
    {
        try
        {
            await _ledger.MarkWritingAsync(record.RequestId, cancellationToken).ConfigureAwait(false);
            var append = await _store.AppendAndVerifyAsync(
                record.RelativePath,
                record.LocalDate,
                record.EntryId,
                record.RenderedMarkdown,
                cancellationToken).ConfigureAwait(false);
            await _faultInjector.AfterFileVerifiedAsync(record, cancellationToken).ConfigureAwait(false);
            await _ledger.MarkCommittedAsync(
                record.RequestId,
                append.FileHashAfter,
                _timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
            return ToResponse(record with { Status = "committed", FileHashAfter = append.FileHashAfter }, duplicate);
        }
        catch (CaptureException exception)
        {
            await _ledger.MarkRecoverableAsync(record.RequestId, exception.Code, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            await _ledger.MarkRecoverableAsync(record.RequestId, "WRITE_FAILED", CancellationToken.None).ConfigureAwait(false);
            throw new CaptureException("WRITE_FAILED", "记录未能安全保存，可以使用相同 request_id 重试。", true, 503, exception);
        }
    }

    private static CaptureResponse ToResponse(CaptureLedgerRecord record, bool duplicate) => new(
        record.SchemaVersion,
        record.RequestId,
        record.EntryId,
        "committed",
        record.CreatedAt,
        record.LocalDate,
        record.RelativePath,
        duplicate);

    private static void ValidateRequest(CaptureRequest request)
    {
        if (request.SchemaVersion != 1)
        {
            throw new CaptureException("SCHEMA_UNSUPPORTED", "仅支持 schema_version=1。", false, 400);
        }

        if (!Guid.TryParse(request.RequestId, out _))
        {
            throw new CaptureException("VALIDATION_FAILED", "request_id 必须是有效 UUID。", false, 400);
        }

        if (request.Content is null)
        {
            throw new CaptureException("VALIDATION_FAILED", "content 必须是字符串。", false, 400);
        }

        if (request.SourceType is not ("desktop" or "weread"))
        {
            throw new CaptureException("VALIDATION_FAILED", "source_type 必须是 desktop 或 weread。", false, 400);
        }

        if (request.SourceType == "desktop" && string.IsNullOrWhiteSpace(request.Content))
        {
            throw new CaptureException("VALIDATION_FAILED", "记录内容不能为空。", false, 400);
        }

        if (Encoding.UTF8.GetByteCount(request.Content) > MaxContentBytes)
        {
            throw new CaptureException("CONTENT_TOO_LARGE", $"单条记录不能超过 {MaxContentBytes} 字节。", false, 413);
        }

        if (request.SourceType == "weread")
        {
            ValidateWereadContext(request.SourceContext);
            if (string.IsNullOrWhiteSpace(request.Content) && string.IsNullOrWhiteSpace(request.SourceContext!.SelectedText))
            {
                throw new CaptureException("VALIDATION_FAILED", "微信读书记录必须包含引用或笔记。", false, 400);
            }
        }
    }

    private static void ValidateWereadContext(SourceContext? source)
    {
        if (source is null || !string.Equals(source.Provider, "weread", StringComparison.Ordinal))
        {
            throw new CaptureException("VALIDATION_FAILED", "微信读书记录缺少有效来源上下文。", false, 400);
        }

        if (!Uri.TryCreate(source.Url, UriKind.Absolute, out var url) ||
            url.Scheme != Uri.UriSchemeHttps ||
            !(string.Equals(url.Host, "weread.qq.com", StringComparison.OrdinalIgnoreCase) ||
              url.Host.EndsWith(".weread.qq.com", StringComparison.OrdinalIgnoreCase)))
        {
            throw new CaptureException("VALIDATION_FAILED", "微信读书来源 URL 无效。", false, 400);
        }

        CheckLength(source.WorkId, 256, "work_id");
        CheckLength(source.WorkTitle, 500, "work_title");
        CheckLength(source.ChapterId, 256, "chapter_id");
        CheckLength(source.ChapterTitle, 500, "chapter_title");
        CheckLength(source.Url, 4096, "url");
        CheckLength(source.SelectionPrefix, 1000, "selection_prefix");
        CheckLength(source.SelectionSuffix, 1000, "selection_suffix");
        CheckLength(source.ExtractorVersion, 100, "extractor_version");
        if (source.Authors is null || source.Authors.Count > 20 || source.Authors.Any(author => author is null || author.Length > 200))
        {
            throw new CaptureException("VALIDATION_FAILED", "authors 超出允许长度。", false, 400);
        }

        if (Encoding.UTF8.GetByteCount(source.SelectedText ?? string.Empty) > MaxSelectedTextBytes)
        {
            throw new CaptureException("CONTENT_TOO_LARGE", $"引用原文不能超过 {MaxSelectedTextBytes} 字节。", false, 413);
        }
    }

    private static void CheckLength(string? value, int maxLength, string field)
    {
        if (value?.Length > maxLength)
        {
            throw new CaptureException("VALIDATION_FAILED", $"{field} 超出允许长度。", false, 400);
        }
    }

    private static string ComputePayloadHash(CaptureRequest request)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            request.SchemaVersion,
            request.EntryId,
            request.SourceType,
            request.Content,
            request.OccurredAt,
            request.SourceContext
        }, JsonOptions);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
