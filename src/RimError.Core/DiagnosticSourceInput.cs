namespace RimError.Core;

/// <summary>
/// A caller-owned text stream and its source label. The ingestor does not dispose the reader.
/// </summary>
public sealed record DiagnosticSourceInput
{
    public required string Source { get; init; }

    public required TextReader Reader { get; init; }

    public DiagnosticIngestionMetadata? Metadata { get; init; }

    public long? InputBytes { get; init; }
}
