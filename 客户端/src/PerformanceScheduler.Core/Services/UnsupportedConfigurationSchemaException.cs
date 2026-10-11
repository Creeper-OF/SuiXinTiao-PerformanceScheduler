namespace PerformanceScheduler.Core.Services;

public sealed class UnsupportedConfigurationSchemaException : NotSupportedException
{
    public UnsupportedConfigurationSchemaException(
        string documentType,
        int actualVersion,
        int supportedVersion,
        string? source = null)
        : base(BuildMessage(documentType, actualVersion, supportedVersion, source))
    {
        DocumentType = documentType;
        ActualVersion = actualVersion;
        SupportedVersion = supportedVersion;
        SourceDescription = source;
    }

    public string DocumentType { get; }

    public int ActualVersion { get; }

    public int SupportedVersion { get; }

    public string? SourceDescription { get; }

    private static string BuildMessage(
        string documentType,
        int actualVersion,
        int supportedVersion,
        string? source)
    {
        var location = string.IsNullOrWhiteSpace(source) ? string.Empty : $" ({source})";
        return $"Unsupported {documentType} schema version {actualVersion}; this build supports version {supportedVersion}{location}.";
    }
}
