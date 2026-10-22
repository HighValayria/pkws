namespace Pkws.Agent;

public sealed class AgentValidationException : Exception
{
    public AgentValidationException(string code, string message, Exception? innerException = null)
        : base(message, innerException) => Code = code;

    public string Code { get; }
}

public sealed class AgentRunLease : IDisposable
{
    private readonly FileStream _stream;
    private AgentRunLease(FileStream stream) => _stream = stream;

    public static AgentRunLease Acquire(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            stream.SetLength(0);
            using var writer = new StreamWriter(stream, leaveOpen: true);
            writer.Write($"{Environment.ProcessId}|{DateTimeOffset.UtcNow:O}");
            writer.Flush();
            stream.Position = 0;
            return new AgentRunLease(stream);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException("另一个 PKWS Agent 任务正在运行。", exception);
        }
    }

    public void Dispose() => _stream.Dispose();
}
