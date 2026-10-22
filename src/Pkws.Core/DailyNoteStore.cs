using System.Security.Cryptography;
using System.Text;

namespace Pkws.Core;

public interface IDailyNoteStore
{
    string GetRelativePath(DateOnly localDate);
    string GetFullPath(string relativePath);
    Task<string> ReadAsync(string relativePath, CancellationToken cancellationToken = default);
    Task<AppendResult> AppendAndVerifyAsync(
        string relativePath,
        DateOnly localDate,
        string entryId,
        string renderedMarkdown,
        CancellationToken cancellationToken = default);
}

public sealed record AppendResult(string RelativePath, string FileHashAfter);

public sealed class DailyNoteStore : IDailyNoteStore
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private readonly PkwsConfig _config;

    public DailyNoteStore(PkwsConfig config) => _config = config;

    public string GetRelativePath(DateOnly localDate)
    {
        var relative = _config.DailyPathPattern
            .Replace("{yyyy-MM-dd}", localDate.ToString("yyyy-MM-dd"), StringComparison.Ordinal)
            .Replace("{yyyy}", localDate.ToString("yyyy"), StringComparison.Ordinal)
            .Replace('/', Path.DirectorySeparatorChar);

        if (Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar).Contains("..", StringComparer.Ordinal))
        {
            throw new CaptureException("PATH_INVALID", "每日笔记路径必须位于文档目录内。", false, 400);
        }

        _ = GetFullPath(relative);
        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    public string GetFullPath(string relativePath)
    {
        EnsureDocumentRootConfigured();
        var root = Path.GetFullPath(_config.DocumentRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRelative = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(root, normalizedRelative));
        var rootPrefix = root + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new CaptureException("PATH_INVALID", "解析后的每日笔记路径超出文档目录。", false, 400);
        }

        return fullPath;
    }

    public async Task<string> ReadAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var fullPath = GetFullPath(relativePath);
        if (!File.Exists(fullPath))
        {
            return string.Empty;
        }

        try
        {
            return await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException exception)
        {
            throw new CaptureException("FILE_BUSY", "每日笔记暂时无法读取，请稍后重试。", true, 503, exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new CaptureException("VAULT_NOT_WRITABLE", "文档目录或每日笔记当前不可读取。", true, 503, exception);
        }
    }

    public async Task<AppendResult> AppendAndVerifyAsync(
        string relativePath,
        DateOnly localDate,
        string entryId,
        string renderedMarkdown,
        CancellationToken cancellationToken = default)
    {
        var fullPath = GetFullPath(relativePath);
        var directory = Path.GetDirectoryName(fullPath) ?? throw new CaptureException("PATH_INVALID", "每日笔记路径缺少目录。", false, 400);
        Directory.CreateDirectory(directory);

        Exception? lastError = null;
        foreach (var delay in new[] { 0, 60, 180, 400 })
        {
            if (delay > 0)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                await AppendOnceAsync(fullPath, localDate, renderedMarkdown, cancellationToken).ConfigureAwait(false);
                var verified = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
                if (!MarkdownEntrySerializer.ContainsCompleteEntry(verified, entryId))
                {
                    throw new CaptureException("WRITE_NOT_VERIFIED", "文件写入后未能按记录 ID 回读验证。", true, 500);
                }

                var fileHash = Convert.ToHexStringLower(SHA256.HashData(Utf8NoBom.GetBytes(verified)));
                return new AppendResult(relativePath.Replace('\\', '/'), fileHash);
            }
            catch (IOException exception)
            {
                lastError = exception;
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new CaptureException("VAULT_NOT_WRITABLE", "文档目录或每日笔记目录不可写。", true, 503, exception);
            }
        }

        throw new CaptureException("FILE_BUSY", "每日笔记暂时被其他程序占用，请稍后重试。", true, 503, lastError);
    }

    private static async Task AppendOnceAsync(
        string fullPath,
        DateOnly localDate,
        string renderedMarkdown,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            fullPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.Read,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);

        stream.Seek(0, SeekOrigin.End);
        var prefix = stream.Length == 0 ? $"# {localDate:yyyy-MM-dd}\n\n" : "\n\n";
        var bytes = Utf8NoBom.GetBytes(prefix + renderedMarkdown);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(true);
    }

    private void EnsureDocumentRootConfigured()
    {
        if (string.IsNullOrWhiteSpace(_config.DocumentRoot))
        {
            throw new CaptureException("VAULT_NOT_CONFIGURED", "文档目录尚未初始化。", false, 409);
        }

        if (!Directory.Exists(_config.DocumentRoot))
        {
            throw new CaptureException("VAULT_UNAVAILABLE", "文档目录不存在或当前不可用。", true, 503);
        }
    }
}
