namespace RimError.Core;

public sealed record DiagnosticIngestionResult
{
    public DiagnosticRecord[] Diagnostics { get; init; } = [];

    public long InputBytes { get; init; }

    public long LinesRead { get; init; }

    public long RawOccurrenceCount { get; init; }

    public long MalformedLineCount { get; init; }

    public long TruncatedLineCount { get; init; }

    public long DroppedDiagnosticCount { get; init; }

    public int SourceCount { get; init; }

    public int? FingerprintSchemaVersion { get; init; }

    public string? RimWorldVersion { get; init; }

    public string? ModProfile { get; init; }

    public DiagnosticIntegrationState? Integration { get; init; }

    public TimeSpan Elapsed { get; init; }

    public int UniqueDiagnosticCount => Diagnostics.Length;

    public DiagnosticStoreSnapshot ToSnapshot(DateTimeOffset? capturedAt = null) =>
        new()
        {
            CapturedAt = capturedAt,
            FingerprintSchemaVersion = FingerprintSchemaVersion,
            RimWorldVersion = RimWorldVersion,
            ModProfile = ModProfile,
            InputBytes = InputBytes,
            RawOccurrenceCount = RawOccurrenceCount,
            LinesRead = LinesRead,
            SourceCount = SourceCount,
            MalformedLineCount = MalformedLineCount,
            TruncatedLineCount = TruncatedLineCount,
            DroppedDiagnosticCount = DroppedDiagnosticCount,
            Integration = Integration,
            Items = Diagnostics
        };
}
