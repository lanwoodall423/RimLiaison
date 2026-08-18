using System.Diagnostics;

namespace RimError.Core;

internal sealed class DiagnosticIngestionSession
{
    private readonly DiagnosticIngestionOptions _options;
    private readonly DiagnosticAggregator _aggregator;
    private readonly DateTimeOffset _fallbackTime;
    private long _inputBytes;
    private long _linesRead;
    private long _rawOccurrenceCount;
    private long _malformedLineCount;
    private long _truncatedLineCount;
    private int _sourceCount;
    private long _sequence;
    private string? _rimWorldVersion;
    private string? _modProfile;
    private bool _rimWorldVersionConflict;
    private bool _modProfileConflict;
    private DiagnosticIntegrationState? _integration;

    public DiagnosticIngestionSession(DiagnosticIngestionOptions options)
    {
        _options = options;
        _aggregator = new DiagnosticAggregator(options.MaxUniqueDiagnostics);
        _fallbackTime = options.IngestionTime ?? DateTimeOffset.UtcNow;
    }

    public async ValueTask ProcessAsync(
        TextReader reader,
        string source,
        DiagnosticIngestionMetadata? metadata,
        long? knownInputBytes,
        CancellationToken cancellationToken)
    {
        ObserveEnvironment(metadata);
        if (metadata?.Integration is not null)
        {
            _integration = DiagnosticIntegrationAdapter.Combine(
                _integration,
                metadata.Integration);
        }
        var lineReader = new BoundedLineReader(reader, _options.MaxLineLength);
        var framer = new DiagnosticEventFramer(source, metadata, _options);
        long estimatedInputBytes = 0;

        while (true)
        {
            var line = await lineReader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            _linesRead++;
            estimatedInputBytes += line.Value.EstimatedUtf8Bytes;
            if (line.Value.WasTruncated)
            {
                _truncatedLineCount++;
                _malformedLineCount++;
            }

            if (line.Value.Text.Contains('\0'))
            {
                _malformedLineCount++;
            }

            Add(framer.Push(line.Value));
        }

        Add(framer.Complete());
        _sourceCount++;
        _inputBytes += knownInputBytes ?? estimatedInputBytes;
    }

    public DiagnosticIngestionResult Complete(TimeSpan elapsed) => new()
    {
        Diagnostics = _aggregator.ToArray(),
        InputBytes = _inputBytes,
        LinesRead = _linesRead,
        RawOccurrenceCount = _rawOccurrenceCount,
        MalformedLineCount = _malformedLineCount,
        TruncatedLineCount = _truncatedLineCount,
        DroppedDiagnosticCount = _aggregator.DroppedDiagnosticCount,
        SourceCount = _sourceCount,
        FingerprintSchemaVersion = DiagnosticFingerprint.CurrentSchemaVersion,
        RimWorldVersion = _rimWorldVersion,
        ModProfile = _modProfile,
        Integration = _integration,
        Elapsed = elapsed
    };

    private void ObserveEnvironment(DiagnosticIngestionMetadata? metadata)
    {
        if (metadata is null)
        {
            return;
        }

        ObserveValue(
            metadata.RimWorldVersion,
            ref _rimWorldVersion,
            ref _rimWorldVersionConflict);
        ObserveValue(
            metadata.ModProfile,
            ref _modProfile,
            ref _modProfileConflict);
    }

    private static void ObserveValue(
        string? incoming,
        ref string? current,
        ref bool conflict)
    {
        if (conflict || string.IsNullOrWhiteSpace(incoming))
        {
            return;
        }

        var normalized = DiagnosticNormalizer.NormalizeStableValue(incoming);
        if (current is null)
        {
            current = normalized;
        }
        else if (!current.Equals(normalized, StringComparison.Ordinal))
        {
            current = null;
            conflict = true;
        }
    }

    private void Add(RawDiagnosticEvent? raw)
    {
        if (raw is null)
        {
            return;
        }

        _rawOccurrenceCount++;
        var record = DiagnosticRecordFactory.Create(raw, _options, _fallbackTime);
        _aggregator.Add(record, _sequence++);
    }
}
