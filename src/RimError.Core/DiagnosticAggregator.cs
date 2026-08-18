namespace RimError.Core;

internal sealed class DiagnosticAggregator
{
    private readonly int _maxUniqueDiagnostics;
    private readonly Dictionary<string, Aggregate> _byId = new(StringComparer.Ordinal);
    private readonly List<Aggregate> _ordered = [];

    public DiagnosticAggregator(int maxUniqueDiagnostics)
    {
        _maxUniqueDiagnostics = maxUniqueDiagnostics;
    }

    public long DroppedDiagnosticCount { get; private set; }

    public void Add(DiagnosticRecord diagnostic, long sequence)
    {
        if (_byId.TryGetValue(diagnostic.Id, out var existing))
        {
            existing.Merge(diagnostic);
            return;
        }

        if (_byId.Count >= _maxUniqueDiagnostics)
        {
            DroppedDiagnosticCount++;
            return;
        }

        var aggregate = new Aggregate(diagnostic, sequence);
        _byId.Add(diagnostic.Id, aggregate);
        _ordered.Add(aggregate);
    }

    public DiagnosticRecord[] ToArray() =>
        _ordered
            .OrderBy(aggregate => aggregate.FirstSequence)
            .ThenBy(aggregate => aggregate.Record.Id, StringComparer.Ordinal)
            .Select(aggregate => aggregate.Record)
            .ToArray();

    private sealed class Aggregate
    {
        public Aggregate(DiagnosticRecord record, long firstSequence)
        {
            Record = record;
            FirstSequence = firstSequence;
        }

        public DiagnosticRecord Record { get; private set; }

        public long FirstSequence { get; }

        public void Merge(DiagnosticRecord incoming)
        {
            var first = Earlier(Record.FirstOccurrence, incoming.FirstOccurrence);
            var last = Later(Record.LastOccurrence, incoming.LastOccurrence);
            var count = Record.OccurrenceCount > long.MaxValue - incoming.OccurrenceCount
                ? long.MaxValue
                : Record.OccurrenceCount + incoming.OccurrenceCount;

            Record = Record with
            {
                FirstOccurrence = first,
                LastOccurrence = last,
                OccurrenceCount = count,
                RepresentativeSample = Record.RepresentativeSample ?? incoming.RepresentativeSample,
                StackFrames = Record.StackFrames ?? incoming.StackFrames,
                NormalizedMessage = Record.NormalizedMessage ?? incoming.NormalizedMessage,
                OriginatingType = Record.OriginatingType ?? incoming.OriginatingType,
                OriginatingMethod = Record.OriginatingMethod ?? incoming.OriginatingMethod,
                TargetType = Record.TargetType ?? incoming.TargetType,
                TargetMethod = Record.TargetMethod ?? incoming.TargetMethod,
                DefType = Record.DefType ?? incoming.DefType,
                DefName = Record.DefName ?? incoming.DefName,
                MissingMember = Record.MissingMember ?? incoming.MissingMember,
                Asset = Record.Asset ?? incoming.Asset,
                PackageId = Record.PackageId ?? incoming.PackageId,
                Dependency = Record.Dependency ?? incoming.Dependency,
                BuildCode = Record.BuildCode ?? incoming.BuildCode,
                RunId = Record.RunId ?? incoming.RunId,
                TestId = Record.TestId ?? incoming.TestId,
                OperationId = Record.OperationId ?? incoming.OperationId,
                OperationName = Record.OperationName ?? incoming.OperationName,
                CorrelationConfidence = Record.CorrelationConfidence ?? incoming.CorrelationConfidence,
                CorrelationSignals = Record.CorrelationSignals ?? incoming.CorrelationSignals,
                CorrelationCandidates = Record.CorrelationCandidates ?? incoming.CorrelationCandidates,
                CorrelationOperationStatus = Record.CorrelationOperationStatus ?? incoming.CorrelationOperationStatus,
                CorrelationOperationSuccess = Record.CorrelationOperationSuccess ?? incoming.CorrelationOperationSuccess,
                CorrelationLaunchId = Record.CorrelationLaunchId ?? incoming.CorrelationLaunchId,
                CorrelationGeneration = Record.CorrelationGeneration ?? incoming.CorrelationGeneration,
                CorrelationSessionId = Record.CorrelationSessionId ?? incoming.CorrelationSessionId,
                CorrelationProfileFingerprint = Record.CorrelationProfileFingerprint ?? incoming.CorrelationProfileFingerprint,
                SourceAttribution = Record.SourceAttribution ?? incoming.SourceAttribution,
                InnerExceptionType = Record.InnerExceptionType ?? incoming.InnerExceptionType,
                InnerExceptionMessage = Record.InnerExceptionMessage ?? incoming.InnerExceptionMessage,
                SourceFile = Record.SourceFile ?? incoming.SourceFile,
                SourceLine = Record.SourceLine ?? incoming.SourceLine,
                SourceSymbol = Record.SourceSymbol ?? incoming.SourceSymbol,
                SourceAssembly = Record.SourceAssembly ?? incoming.SourceAssembly,
                DefSourceFile = Record.DefSourceFile ?? incoming.DefSourceFile,
                DefSourceLine = Record.DefSourceLine ?? incoming.DefSourceLine,
                DefReferenceFiles = Record.DefReferenceFiles ?? incoming.DefReferenceFiles,
                AttributionConfidence = Record.AttributionConfidence ?? incoming.AttributionConfidence,
                AttributionCandidates = Record.AttributionCandidates ?? incoming.AttributionCandidates,
                CausalChildren = Record.CausalChildren ?? incoming.CausalChildren,
                CausalConfidence = Record.CausalConfidence ?? incoming.CausalConfidence,
                CausalSignals = Record.CausalSignals ?? incoming.CausalSignals,
                CausalRole = Record.CausalRole ?? incoming.CausalRole
            };
        }

        private static DateTimeOffset? Earlier(DateTimeOffset? left, DateTimeOffset? right) =>
            left is null ? right : right is null ? left : left <= right ? left : right;

        private static DateTimeOffset? Later(DateTimeOffset? left, DateTimeOffset? right) =>
            left is null ? right : right is null ? left : left >= right ? left : right;
    }
}
