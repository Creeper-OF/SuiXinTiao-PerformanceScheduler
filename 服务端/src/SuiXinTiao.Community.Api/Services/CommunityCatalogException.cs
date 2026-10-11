namespace SuiXinTiao.Community.Api.Services;

public sealed class CommunityCatalogException : Exception
{
    public CommunityCatalogException(string message, string path, Exception? innerException = null)
        : base(message, innerException)
    {
        Path = path;
    }

    public string Path { get; }
}
