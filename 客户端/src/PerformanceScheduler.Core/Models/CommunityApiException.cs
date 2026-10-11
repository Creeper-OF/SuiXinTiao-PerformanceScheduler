namespace PerformanceScheduler.Core.Models;

public sealed class CommunityApiException : Exception
{
    public int? StatusCode { get; }

    public string? Endpoint { get; }

    public CommunityApiException(string message, string? endpoint = null, int? statusCode = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Endpoint = endpoint;
        StatusCode = statusCode;
    }
}
