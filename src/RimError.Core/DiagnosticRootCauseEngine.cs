namespace RimError.Core;

public static class DiagnosticRootCauseEngine
{
    private static readonly TimeSpan MaximumCausalGap = TimeSpan.FromMinutes(10);

    public static DiagnosticCausalAnalysis Analyze(DiagnosticStoreSnapshot snapshot) =>
        Analyze(snapshot?.Items ?? []);

    public static DiagnosticCausalAnalysis Analyze(
        IEnumerable<DiagnosticRecord> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        var records = diagnostics.ToArray();
        var byId = records.ToDictionary(
            diagnostic => diagnostic.Id,
            StringComparer.Ordinal);
        var contextIndex = BuildContextIndex(records);
        var exceptionIndex = BuildExceptionIndex(records);
        var proposals = new Dictionary<(string Parent, string Child), CandidateLink>();

        AddExplicitParentLinks(records, byId, proposals);
        AddInnerExceptionLinks(records, exceptionIndex, proposals);
        AddInitializationLinks(records, contextIndex, proposals);
        AddKnownWrapperLinks(records, contextIndex, proposals);

        var links = SelectLinks(proposals.Values)
            .OrderBy(link => link.ParentId, StringComparer.Ordinal)
            .ThenBy(link => link.ChildId, StringComparer.Ordinal)
            .Select(link => new DiagnosticCausalLink
            {
                ParentId = link.ParentId,
                ChildId = link.ChildId,
                Confidence = link.Confidence,
                Signals = link.Signals
                    .OrderBy(signal => signal, StringComparer.Ordinal)
                    .ToArray()
            })
            .ToArray();

        var groups = BuildGroups(records, links)
            .OrderBy(group => RootSortKey(group.RootId, records, group))
            .ThenBy(group => group.RootId, StringComparer.Ordinal)
            .ToArray();

        return new DiagnosticCausalAnalysis
        {
            Groups = groups,
            Links = links
        };
    }

    public static DiagnosticStoreSnapshot Apply(DiagnosticStoreSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var analysis = Analyze(snapshot);
        return snapshot with
        {
            Items = snapshot.Items
                .Select(diagnostic => ApplyToRecord(diagnostic, analysis))
                .ToArray(),
            CausalAnalysis = analysis
        };
    }

    public static DiagnosticRecord EnrichForShow(
        DiagnosticStoreSnapshot snapshot,
        DiagnosticRecord diagnostic)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(diagnostic);
        return ApplyToRecord(diagnostic, snapshot.CausalAnalysis ?? Analyze(snapshot));
    }

    public static DiagnosticRootCauseSummary Summarize(
        DiagnosticRecord diagnostic,
        DiagnosticRootCauseGroup group)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        ArgumentNullException.ThrowIfNull(group);
        return new DiagnosticRootCauseSummary
        {
            Id = diagnostic.Id,
            Type = ShortName(diagnostic.ExceptionType),
            Category = diagnostic.ExceptionType is null ? diagnostic.Category : null,
            Method = ComposeMethod(diagnostic),
            Symbol = diagnostic.SourceSymbol,
            Def = ComposeDef(diagnostic),
            Member = diagnostic.MissingMember,
            Asset = diagnostic.Asset,
            Code = diagnostic.BuildCode,
            Source = ChooseSource(diagnostic),
            Line = ChooseLine(diagnostic),
            Confidence = group.Confidence,
            Operation = MaterialCorrelation(diagnostic)
                ? diagnostic.OperationName ?? diagnostic.OperationId
                : null,
            Test = MaterialCorrelation(diagnostic) ? diagnostic.TestId : null,
            Count = diagnostic.OccurrenceCount
        };
    }

    public static IReadOnlyList<DiagnosticRecord> OrderRootCauses(
        IEnumerable<DiagnosticRecord> diagnostics,
        DiagnosticCausalAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(analysis);
        var records = diagnostics.ToDictionary(
            diagnostic => diagnostic.Id,
            StringComparer.Ordinal);
        return analysis.Groups
            .Where(group => records.ContainsKey(group.RootId))
            .Select(group => records[group.RootId])
            .OrderByDescending(diagnostic => RootPriority(diagnostic, analysis))
            .ThenBy(diagnostic => diagnostic.FirstOccurrence ?? DateTimeOffset.MaxValue)
            .ThenByDescending(diagnostic => diagnostic.OccurrenceCount)
            .ThenBy(diagnostic => diagnostic.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static void AddExplicitParentLinks(
        IReadOnlyList<DiagnosticRecord> records,
        IReadOnlyDictionary<string, DiagnosticRecord> byId,
        IDictionary<(string Parent, string Child), CandidateLink> proposals)
    {
        foreach (var child in records)
        {
            if (string.IsNullOrWhiteSpace(child.ParentId) ||
                !byId.ContainsKey(child.ParentId) ||
                child.ParentId.Equals(child.Id, StringComparison.Ordinal))
            {
                continue;
            }

            AddProposal(
                proposals,
                child.ParentId,
                child.Id,
                "high",
                ["explicit_parent"],
                isExplicit: true);
        }
    }

    private static void AddInnerExceptionLinks(
        IReadOnlyList<DiagnosticRecord> records,
        IReadOnlyDictionary<string, DiagnosticRecord[]> exceptionIndex,
        IDictionary<(string Parent, string Child), CandidateLink> proposals)
    {
        foreach (var wrapper in records)
        {
            var inner = GetInnerException(wrapper);
            if (inner.Type is null)
            {
                continue;
            }

            var candidatePool = GetExceptionCandidates(inner, exceptionIndex);
            var candidates = candidatePool
                .Where(candidate => candidate.Id != wrapper.Id)
                .Where(candidate => SameExceptionType(candidate.ExceptionType, inner.Type))
                .Where(candidate => inner.Message is null ||
                    MessageMatches(candidate, inner.Message))
                .OrderBy(candidate => candidate.Id, StringComparer.Ordinal)
                .ToArray();
            if (candidates.Length != 1)
            {
                continue;
            }

            var signals = SharedSignals(candidates[0], wrapper);
            signals.Add("inner_exception");
            if (HasTemporalOrdering(candidates[0], wrapper))
            {
                signals.Add("temporal_order");
            }

            AddProposal(
                proposals,
                candidates[0].Id,
                wrapper.Id,
                "high",
                signals,
                isExplicit: true);
        }
    }

    private static void AddInitializationLinks(
        IReadOnlyList<DiagnosticRecord> records,
        IReadOnlyDictionary<string, DiagnosticRecord[]> contextIndex,
        IDictionary<(string Parent, string Child), CandidateLink> proposals)
    {
        foreach (var initialization in records.Where(IsInitializationFailure))
        {
            foreach (var downstream in ContextCandidates(initialization, contextIndex)
                         .Where(candidate => candidate.Id != initialization.Id)
                         .Where(IsInitializationDownstream)
                         .Where(candidate => HasTemporalOrdering(initialization, candidate))
                         .Where(candidate => ContextSupportsLink(initialization, candidate))
                         .Where(candidate => !HasIntermediateSpecificFailure(
                             initialization,
                             candidate,
                             contextIndex)))
            {
                var signals = SharedSignals(initialization, downstream);
                if (!HasStrongContext(signals))
                {
                    continue;
                }

                signals.Add("initialization_before_downstream");
                AddProposal(
                    proposals,
                    initialization.Id,
                    downstream.Id,
                    InitializationConfidence(initialization, signals),
                    signals);
            }
        }
    }

    private static bool HasIntermediateSpecificFailure(
        DiagnosticRecord initialization,
        DiagnosticRecord downstream,
        IReadOnlyDictionary<string, DiagnosticRecord[]> contextIndex) =>
        ContextCandidates(initialization, contextIndex)
            .Where(IsSpecificFailure)
            .Where(candidate => candidate.Id != initialization.Id &&
                candidate.Id != downstream.Id)
            .Any(candidate =>
                HasTemporalOrdering(initialization, candidate) &&
                HasTemporalOrdering(candidate, downstream) &&
                ContextSupportsLink(initialization, candidate) &&
                ContextSupportsLink(candidate, downstream));

    private static void AddKnownWrapperLinks(
        IReadOnlyList<DiagnosticRecord> records,
        IReadOnlyDictionary<string, DiagnosticRecord[]> contextIndex,
        IDictionary<(string Parent, string Child), CandidateLink> proposals)
    {
        foreach (var wrapper in records.Where(IsKnownWrapper))
        {
            var candidates = ContextCandidates(wrapper, contextIndex)
                .Where(candidate => candidate.Id != wrapper.Id)
                .Where(IsSpecificFailure)
                .Where(candidate => HasTemporalOrdering(candidate, wrapper))
                .Where(candidate => ContextSupportsLink(candidate, wrapper))
                .Select(candidate => (Record: candidate, Signals: SharedSignals(candidate, wrapper)))
                .Where(candidate => HasStrongContext(candidate.Signals))
                .OrderByDescending(candidate => candidate.Signals.Count)
                .ThenBy(candidate => candidate.Record.Id, StringComparer.Ordinal)
                .ToArray();
            if (candidates.Length != 1)
            {
                continue;
            }

            candidates[0].Signals.Add("known_wrapper");
            AddProposal(
                proposals,
                candidates[0].Record.Id,
                wrapper.Id,
                "medium",
                candidates[0].Signals);
        }
    }

    private static CandidateLink[] SelectLinks(IEnumerable<CandidateLink> candidates)
    {
        var selected = new List<CandidateLink>();
        foreach (var group in candidates
                     .GroupBy(candidate => candidate.ChildId, StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var ordered = group
                .OrderByDescending(candidate => ConfidenceRank(candidate.Confidence))
                .ThenByDescending(candidate => candidate.IsExplicit)
                .ThenByDescending(candidate => candidate.Signals.Count)
                .ThenBy(candidate => candidate.ParentId, StringComparer.Ordinal)
                .ToArray();
            if (ordered.Length == 0)
            {
                continue;
            }

            var best = ordered[0];
            var tied = ordered.Skip(1).Any(candidate =>
                ConfidenceRank(candidate.Confidence) == ConfidenceRank(best.Confidence) &&
                candidate.IsExplicit == best.IsExplicit &&
                candidate.Signals.Count == best.Signals.Count);
            if (tied)
            {
                // A symptom with two equally supported parents is safer as an
                // independent root than as an arbitrary child of one parent.
                continue;
            }

            selected.Add(best);
        }

        return selected
            .OrderBy(candidate => candidate.ParentId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.ChildId, StringComparer.Ordinal)
            .ToArray();
    }

    private static DiagnosticRootCauseGroup[] BuildGroups(
        IReadOnlyList<DiagnosticRecord> records,
        IReadOnlyList<DiagnosticCausalLink> links)
    {
        var incoming = links
            .GroupBy(link => link.ChildId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(link => link.ParentId).ToArray(),
                StringComparer.Ordinal);
        var outgoing = links
            .GroupBy(link => link.ParentId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray(),
                StringComparer.Ordinal);
        var roots = records
            .Where(record => !incoming.ContainsKey(record.Id))
            .Where(record => DiagnosticLatestReportBuilder.IsActionableError(record) ||
                outgoing.ContainsKey(record.Id))
            .ToArray();
        if (roots.Length == 0)
        {
            roots = records
                .Where(DiagnosticLatestReportBuilder.IsActionableError)
                .ToArray();
        }

        return roots
            .Select(root =>
            {
                var childIds = Descendants(root.Id, outgoing);
                var rootLinks = outgoing.TryGetValue(root.Id, out var direct)
                    ? direct
                    : [];
                var signals = rootLinks
                    .SelectMany(link => link.Signals)
                    .Append(childIds.Length == 0 ? "independent" : "root_cause")
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(signal => signal, StringComparer.Ordinal)
                    .ToArray();
                return new DiagnosticRootCauseGroup
                {
                    RootId = root.Id,
                    ChildIds = childIds,
                    Confidence = RootConfidence(root, rootLinks),
                    Signals = signals
                };
            })
            .ToArray();
    }

    private static string[] Descendants(
        string rootId,
        IReadOnlyDictionary<string, DiagnosticCausalLink[]> outgoing)
    {
        var result = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { rootId };
        var queue = new Queue<string>();
        queue.Enqueue(rootId);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!outgoing.TryGetValue(current, out var children))
            {
                continue;
            }

            foreach (var child in children
                         .OrderBy(link => link.ChildId, StringComparer.Ordinal))
            {
                if (!visited.Add(child.ChildId))
                {
                    continue;
                }

                result.Add(child.ChildId);
                queue.Enqueue(child.ChildId);
            }
        }

        return result.ToArray();
    }

    private static DiagnosticRecord ApplyToRecord(
        DiagnosticRecord diagnostic,
        DiagnosticCausalAnalysis analysis)
    {
        var incoming = analysis.Links
            .Where(link => link.ChildId.Equals(diagnostic.Id, StringComparison.Ordinal))
            .OrderByDescending(link => ConfidenceRank(link.Confidence))
            .ThenBy(link => link.ParentId, StringComparer.Ordinal)
            .ToArray();
        var outgoing = analysis.Links
            .Where(link => link.ParentId.Equals(diagnostic.Id, StringComparison.Ordinal))
            .OrderBy(link => link.ChildId, StringComparer.Ordinal)
            .ToArray();
        var signals = incoming
            .Concat(outgoing)
            .SelectMany(link => link.Signals)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(signal => signal, StringComparer.Ordinal)
            .ToArray();
        var group = analysis.Groups.FirstOrDefault(candidate =>
            candidate.RootId.Equals(diagnostic.Id, StringComparison.Ordinal));
        var confidence = incoming.Length > 0
            ? incoming[0].Confidence
            : group?.Confidence ?? diagnostic.CausalConfidence;
        var role = incoming.Length > 0
            ? "downstream"
            : group is not null && outgoing.Length > 0
                ? "root"
                : diagnostic.CausalRole;

        return diagnostic with
        {
            ParentId = incoming.FirstOrDefault()?.ParentId ?? diagnostic.ParentId,
            CausalChildren = outgoing.Length == 0
                ? diagnostic.CausalChildren
                : outgoing.Select(link => link.ChildId).ToArray(),
            CausalConfidence = confidence,
            CausalSignals = signals.Length == 0
                ? diagnostic.CausalSignals
                : signals,
            CausalRole = role
        };
    }

    private static void AddProposal(
        IDictionary<(string Parent, string Child), CandidateLink> proposals,
        string parentId,
        string childId,
        string confidence,
        IEnumerable<string> signals,
        bool isExplicit = false)
    {
        if (parentId.Equals(childId, StringComparison.Ordinal))
        {
            return;
        }

        var key = (parentId, childId);
        var incomingSignals = signals
            .Where(signal => !string.IsNullOrWhiteSpace(signal))
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        if (!proposals.TryGetValue(key, out var existing))
        {
            proposals[key] = new CandidateLink
            {
                ParentId = parentId,
                ChildId = childId,
                Confidence = confidence,
                Signals = incomingSignals,
                IsExplicit = isExplicit
            };
            return;
        }

        existing.Confidence = ConfidenceRank(confidence) >
            ConfidenceRank(existing.Confidence)
            ? confidence
            : existing.Confidence;
        existing.IsExplicit |= isExplicit;
        existing.Signals.UnionWith(incomingSignals);
    }

    private static IReadOnlyDictionary<string, DiagnosticRecord[]> BuildContextIndex(
        IReadOnlyList<DiagnosticRecord> records)
    {
        var index = new Dictionary<string, List<DiagnosticRecord>>(
            StringComparer.Ordinal);
        foreach (var record in records)
        {
            foreach (var key in ContextKeys(record))
            {
                if (!index.TryGetValue(key, out var bucket))
                {
                    bucket = [];
                    index[key] = bucket;
                }

                bucket.Add(record);
            }
        }

        return index.ToDictionary(
            pair => pair.Key,
            pair => pair.Value
                .OrderBy(record => record.Id, StringComparer.Ordinal)
                .ToArray(),
            StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, DiagnosticRecord[]> BuildExceptionIndex(
        IReadOnlyList<DiagnosticRecord> records)
    {
        var index = new Dictionary<string, List<DiagnosticRecord>>(
            StringComparer.Ordinal);
        foreach (var record in records.Where(record => record.ExceptionType is not null))
        {
            var typeKey = ExceptionTypeKey(record.ExceptionType!);
            AddIndexValue(index, typeKey, record);
            var message = DiagnosticNormalizer.NormalizeMessage(
                record.NormalizedMessage ?? record.Message);
            if (message.Length > 0)
            {
                AddIndexValue(index, $"{typeKey}|message:{message}", record);
            }
        }

        return index.ToDictionary(
            pair => pair.Key,
            pair => pair.Value
                .OrderBy(record => record.Id, StringComparer.Ordinal)
                .ToArray(),
            StringComparer.Ordinal);
    }

    private static void AddIndexValue(
        IDictionary<string, List<DiagnosticRecord>> index,
        string key,
        DiagnosticRecord record)
    {
        if (!index.TryGetValue(key, out var bucket))
        {
            bucket = [];
            index[key] = bucket;
        }

        bucket.Add(record);
    }

    private static DiagnosticRecord[] GetExceptionCandidates(
        (string? Type, string? Message) inner,
        IReadOnlyDictionary<string, DiagnosticRecord[]> index)
    {
        if (inner.Type is null)
        {
            return [];
        }

        var typeKey = ExceptionTypeKey(inner.Type);
        if (inner.Message is not null &&
            index.TryGetValue(
                $"{typeKey}|message:{DiagnosticNormalizer.NormalizeMessage(inner.Message)}",
                out var exact))
        {
            return exact;
        }

        return index.TryGetValue(typeKey, out var candidates) ? candidates : [];
    }

    private static IEnumerable<DiagnosticRecord> ContextCandidates(
        DiagnosticRecord record,
        IReadOnlyDictionary<string, DiagnosticRecord[]> index)
    {
        var candidates = new Dictionary<string, DiagnosticRecord>(StringComparer.Ordinal);
        foreach (var key in ContextKeys(record))
        {
            if (!index.TryGetValue(key, out var bucket))
            {
                continue;
            }

            foreach (var candidate in bucket)
            {
                candidates.TryAdd(candidate.Id, candidate);
            }
        }

        return candidates.Values.OrderBy(candidate => candidate.Id, StringComparer.Ordinal);
    }

    private static IEnumerable<string> ContextKeys(DiagnosticRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.OriginatingType))
        {
            yield return $"type:{record.OriginatingType}";
        }

        if (!string.IsNullOrWhiteSpace(record.TargetType))
        {
            yield return $"type:{record.TargetType}";
        }

        if (!string.IsNullOrWhiteSpace(record.OriginatingType) &&
            !string.IsNullOrWhiteSpace(record.OriginatingMethod))
        {
            yield return $"method:{record.OriginatingType}.{record.OriginatingMethod}";
        }

        if (!string.IsNullOrWhiteSpace(record.SourceSymbol))
        {
            yield return $"symbol:{record.SourceSymbol}";
        }

        if (record.StackFrames is not null)
        {
            foreach (var frame in record.StackFrames.Take(8))
            {
                if (!string.IsNullOrWhiteSpace(frame))
                {
                    yield return $"stack:{frame}";
                }
            }
        }
    }

    private static string ExceptionTypeKey(string exceptionType) =>
        $"exception:{ShortName(exceptionType)?.ToUpperInvariant()}";

    private static bool ContextSupportsLink(
        DiagnosticRecord parent,
        DiagnosticRecord child)
    {
        var signals = SharedSignals(parent, child);
        return HasStrongContext(signals) &&
               (HasTemporalOrdering(parent, child) || SameCorrelation(parent, child));
    }

    private static bool HasStrongContext(IReadOnlyCollection<string> signals) =>
        signals.Contains("shared_stack") ||
        signals.Contains("shared_method") ||
        signals.Contains("shared_type") ||
        signals.Contains("shared_symbol") ||
        signals.Contains("same_operation_and_type") ||
        signals.Contains("same_run_and_type");

    private static List<string> SharedSignals(
        DiagnosticRecord parent,
        DiagnosticRecord child)
    {
        var signals = new List<string>();
        if (SameValue(parent.OriginatingType, child.OriginatingType) ||
            SameValue(parent.TargetType, child.TargetType))
        {
            signals.Add("shared_type");
        }

        if (SameValue(parent.OriginatingType, child.OriginatingType) &&
            SameValue(parent.OriginatingMethod, child.OriginatingMethod))
        {
            signals.Add("shared_method");
        }

        if (parent.StackFrames is not null && child.StackFrames is not null &&
            parent.StackFrames.Intersect(child.StackFrames, StringComparer.Ordinal).Any())
        {
            signals.Add("shared_stack");
        }

        if (SameValue(parent.OriginatingAssembly, child.OriginatingAssembly))
        {
            signals.Add("shared_assembly");
        }

        if (SameValue(parent.DefName, child.DefName))
        {
            signals.Add("shared_def");
        }

        if (SameValue(parent.SourceSymbol, child.SourceSymbol))
        {
            signals.Add("shared_symbol");
        }

        if (SameValue(parent.SourceFile, child.SourceFile))
        {
            signals.Add("shared_source_file");
        }

        if (SameValue(parent.RunId, child.RunId))
        {
            signals.Add("same_run");
            if (SameValue(parent.OriginatingType, child.OriginatingType))
            {
                signals.Add("same_run_and_type");
            }
        }

        if (SameValue(parent.OperationId, child.OperationId))
        {
            signals.Add("same_operation");
            if (SameValue(parent.OriginatingType, child.OriginatingType))
            {
                signals.Add("same_operation_and_type");
            }
        }

        return signals;
    }

    private static bool SameCorrelation(
        DiagnosticRecord left,
        DiagnosticRecord right) =>
        SameValue(left.OperationId, right.OperationId) ||
        SameValue(left.RunId, right.RunId);

    private static bool HasTemporalOrdering(
        DiagnosticRecord parent,
        DiagnosticRecord child)
    {
        if (parent.LastOccurrence is null || child.FirstOccurrence is null)
        {
            return false;
        }

        var gap = child.FirstOccurrence.Value - parent.LastOccurrence.Value;
        return gap >= TimeSpan.Zero && gap <= MaximumCausalGap;
    }

    private static bool IsInitializationFailure(DiagnosticRecord diagnostic) =>
        diagnostic.Category is "runtime_type_initialization" or
            "runtime_static_initialization" ||
        ContainsAny(diagnostic, "type initializer", "static constructor", "failed to initialize", "could not instantiate");

    private static bool IsInitializationDownstream(DiagnosticRecord diagnostic) =>
        diagnostic.Category is "runtime_ticking" or
            "runtime_drawing" or
            "runtime_long_event" ||
        ContainsAny(diagnostic, "exception ticking", "exception drawing", "LongEventHandler") ||
        diagnostic.OriginatingMethod is "Tick" or "Draw";

    private static bool IsKnownWrapper(DiagnosticRecord diagnostic) =>
        diagnostic.Category is "runtime_ticking" or
            "runtime_drawing" or
            "runtime_long_event" ||
        ContainsAny(diagnostic, "exception ticking", "exception drawing", "LongEventHandler") ||
        ContainsAny(diagnostic, "could not instantiate", "wrapper exception");

    private static bool IsSpecificFailure(DiagnosticRecord diagnostic) =>
        !IsKnownWrapper(diagnostic) &&
        !IsInitializationFailure(diagnostic) &&
        diagnostic.ExceptionType is not null &&
        DiagnosticLatestReportBuilder.IsActionableError(diagnostic);

    private static string InitializationConfidence(
        DiagnosticRecord initialization,
        IReadOnlyCollection<string> signals) =>
        signals.Contains("shared_stack") ||
        signals.Contains("same_operation_and_type") ||
        signals.Contains("same_run_and_type") ||
        initialization.Category == "runtime_type_initialization"
            ? "high"
            : "medium";

    private static string RootConfidence(
        DiagnosticRecord root,
        IReadOnlyCollection<DiagnosticCausalLink> outgoing)
    {
        if (outgoing.Any(link => link.Confidence == "high") ||
            root.Category is "runtime_type_initialization" or
            "runtime_static_initialization")
        {
            return "high";
        }

        if (outgoing.Count > 0 || root.ExceptionType is not null)
        {
            return "medium";
        }

        return "low";
    }

    private static int RootPriority(
        DiagnosticRecord diagnostic,
        DiagnosticCausalAnalysis analysis)
    {
        var group = analysis.Groups.FirstOrDefault(candidate =>
            candidate.RootId.Equals(diagnostic.Id, StringComparison.Ordinal));
        var priority = diagnostic.Category is "runtime_type_initialization" or
            "runtime_static_initialization"
            ? 100
            : group?.Signals.Contains("inner_exception") == true
                ? 90
                : group?.ChildIds.Length > 0
                    ? 80
                    : 50;
        return priority + ConfidenceRank(group?.Confidence);
    }

    private static (int Priority, DateTimeOffset Time, string Id) RootSortKey(
        string rootId,
        IReadOnlyList<DiagnosticRecord> records,
        DiagnosticRootCauseGroup group)
    {
        var record = records.First(candidate => candidate.Id == rootId);
        return (-RootPriority(record, new DiagnosticCausalAnalysis { Groups = [group] }),
            record.FirstOccurrence ?? DateTimeOffset.MaxValue,
            rootId);
    }

    private static (string? Type, string? Message) GetInnerException(
        DiagnosticRecord diagnostic)
    {
        if (!string.IsNullOrWhiteSpace(diagnostic.InnerExceptionType))
        {
            return (diagnostic.InnerExceptionType, diagnostic.InnerExceptionMessage);
        }

        return DiagnosticNormalizer.ExtractInnerException(
            $"{diagnostic.Message}\n{diagnostic.RepresentativeSample}");
    }

    private static bool MessageMatches(
        DiagnosticRecord diagnostic,
        string expected)
    {
        var normalizedExpected = DiagnosticNormalizer.NormalizeMessage(expected);
        var candidates = new[]
        {
            diagnostic.NormalizedMessage,
            DiagnosticNormalizer.NormalizeMessage(diagnostic.Message),
            diagnostic.InnerExceptionMessage
        };
        return candidates.Any(candidate =>
            candidate is not null &&
            (candidate.Equals(normalizedExpected, StringComparison.Ordinal) ||
             candidate.Contains(normalizedExpected, StringComparison.Ordinal)));
    }

    private static bool SameExceptionType(string? left, string right)
    {
        var leftName = ShortName(left);
        var rightName = ShortName(right);
        return leftName is not null &&
            rightName is not null &&
            leftName.Equals(rightName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameValue(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) &&
        left.Equals(right, StringComparison.Ordinal);

    private static bool ContainsAny(
        DiagnosticRecord diagnostic,
        params string[] values)
    {
        var text = $"{diagnostic.Message}\n{diagnostic.Category}";
        return values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ChooseSource(DiagnosticRecord diagnostic) =>
        diagnostic.SourceFile ??
        diagnostic.DefSourceFile ??
        diagnostic.DefReferenceFiles?.FirstOrDefault();

    private static int? ChooseLine(DiagnosticRecord diagnostic) =>
        diagnostic.SourceFile is not null
            ? diagnostic.SourceLine
            : diagnostic.DefSourceFile is not null
                ? diagnostic.DefSourceLine
                : null;

    private static string? ComposeMethod(DiagnosticRecord diagnostic)
    {
        var type = diagnostic.OriginatingType ?? diagnostic.TargetType;
        var method = diagnostic.OriginatingMethod ?? diagnostic.TargetMethod;
        return type is null ? method : method is null ? type : $"{type}.{method}";
    }

    private static string? ComposeDef(DiagnosticRecord diagnostic)
    {
        if (diagnostic.DefType is null)
        {
            return diagnostic.DefName;
        }

        return diagnostic.DefName is null
            ? diagnostic.DefType
            : $"{diagnostic.DefType}:{diagnostic.DefName}";
    }

    private static string? ShortName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var separator = value.LastIndexOf('.');
        return separator >= 0 ? value[(separator + 1)..] : value;
    }

    private static bool MaterialCorrelation(DiagnosticRecord diagnostic) =>
        diagnostic.CorrelationConfidence is "high" or "medium" &&
        (!string.IsNullOrWhiteSpace(diagnostic.OperationName) ||
         !string.IsNullOrWhiteSpace(diagnostic.OperationId));

    private static int ConfidenceRank(string? confidence) =>
        confidence switch
        {
            "high" => 3,
            "medium" => 2,
            "low" => 1,
            _ => 0
        };

    private sealed class CandidateLink
    {
        public required string ParentId { get; init; }

        public required string ChildId { get; init; }

        public required string Confidence { get; set; }

        public required HashSet<string> Signals { get; init; }

        public bool IsExplicit { get; set; }
    }
}
