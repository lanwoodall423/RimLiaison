namespace RimError.Core;

internal static class DiagnosticRecordFactory
{
    public static DiagnosticRecord Create(
        RawDiagnosticEvent raw,
        DiagnosticIngestionOptions options,
        DateTimeOffset fallbackTime)
    {
        var message = DiagnosticNormalizer.NormalizeStableValue(raw.Message);
        if (message.Length == 0)
        {
            message = "Unrecognized diagnostic";
        }

        var normalizedMessage = DiagnosticNormalizer.NormalizeMessage(raw.Message);
        var exceptionType = DiagnosticNormalizer.NormalizeStableValue(raw.ExceptionType);
        var frames = raw.StackFrames?
            .Select(DiagnosticNormalizer.NormalizeMessage)
            .Where(frame => frame.Length > 0)
            .Select(frame => TrimToLength(frame, options.MaxFrameLength))
            .Take(options.MaxStackDepth)
            .ToArray();
        var origin = frames is { Length: > 0 }
            ? DiagnosticNormalizer.ExtractOrigin(frames[0])
            : (null, null);
        var def = DiagnosticNormalizer.ExtractDefIdentity(raw.Message);
        var classification = RimWorldDiagnosticClassifier.Classify(raw);
        var inner = DiagnosticNormalizer.ExtractInnerException(
            $"{raw.Message}\n{raw.Sample}");
        var sample = TrimToLength(raw.Sample, options.MaxRawSampleLength);
        var first = raw.Timestamp ?? fallbackTime;

        var record = new DiagnosticRecord
        {
            Id = string.Empty,
            Severity = raw.Severity,
            Category = classification?.Category ?? raw.Category ?? (raw.IsPartial ? "Partial" : null),
            Message = TrimToLength(message, options.MaxMessageLength),
            NormalizedMessage = normalizedMessage == message ? null :
                TrimToLength(normalizedMessage, options.MaxMessageLength),
            RepresentativeSample = sample.Length == 0 || sample == message ? null : sample,
            ExceptionType = exceptionType.Length == 0 ? null : exceptionType,
            StackFrames = frames is { Length: > 0 } ? frames : null,
            OriginatingType = origin.Type,
            OriginatingMethod = origin.Method,
            OriginatingAssembly = classification?.OriginatingAssembly,
            TargetType = classification?.TargetType,
            TargetMethod = classification?.TargetMethod,
            DefType = classification?.DefType ?? def.DefType,
            DefName = classification?.DefName ?? def.DefName,
            MissingMember = classification?.MissingMember ??
                DiagnosticNormalizer.ExtractMissingMember(raw.Message, exceptionType),
            Asset = classification?.Asset,
            PackageId = classification?.PackageId,
            Dependency = classification?.Dependency,
            BuildCode = classification?.BuildCode,
            Source = raw.Source,
            InnerExceptionType = inner.Type,
            InnerExceptionMessage = inner.Message is null
                ? null
                : TrimToLength(inner.Message, options.MaxMessageLength),
            FirstOccurrence = first,
            LastOccurrence = first,
            OccurrenceCount = 1,
            RunId = raw.Metadata?.RunId,
            WorkflowId = raw.Metadata?.WorkflowId,
            TestId = raw.Metadata?.TestId,
            OperationId = raw.Metadata?.OperationId,
            OperationName = raw.Metadata?.OperationName,
            CorrelationConfidence = !string.IsNullOrWhiteSpace(raw.Metadata?.OperationId) ||
                !string.IsNullOrWhiteSpace(raw.Metadata?.OperationName)
                ? "high"
                : null,
            CorrelationSignals = !string.IsNullOrWhiteSpace(raw.Metadata?.OperationId) ||
                !string.IsNullOrWhiteSpace(raw.Metadata?.OperationName)
                ? ["explicit-ingest-metadata"]
                : null,
            SourceAttribution = raw.Metadata?.SourceAttribution
        };

        return record with { Id = DiagnosticFingerprint.Compute(record) };
    }

    private static string TrimToLength(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
