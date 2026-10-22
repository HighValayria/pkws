namespace Pkws.Core;

public sealed class CaptureException : Exception
{
    public CaptureException(string code, string message, bool retryable, int httpStatus, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        Retryable = retryable;
        HttpStatus = httpStatus;
    }

    public string Code { get; }
    public bool Retryable { get; }
    public int HttpStatus { get; }
}
