namespace RimError.Core;

public static class DiagnosticSourceAttributor
{
    public static DiagnosticStoreSnapshot Enrich(
        DiagnosticStoreSnapshot snapshot,
        ProjectIndex index,
        ProjectIndexOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(index);
        var maxCandidates = options?.MaxAttributionCandidates ??
            new ProjectIndexOptions().MaxAttributionCandidates;
        if (maxCandidates <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        return snapshot with
        {
            Items = snapshot.Items
                .Select(diagnostic => Enrich(diagnostic, index, maxCandidates))
                .ToArray()
        };
    }

    public static DiagnosticRecord Enrich(
        DiagnosticRecord diagnostic,
        ProjectIndex index,
        int maxCandidates = 8)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        ArgumentNullException.ThrowIfNull(index);
        if (maxCandidates <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCandidates));
        }

        var result = diagnostic;
        var methodEvidence = FindMethodEvidence(diagnostic, index, maxCandidates);
        if (methodEvidence is not null)
        {
            result = ApplyMethodEvidence(result, methodEvidence);
        }

        var definitionEvidence = FindDefinitionEvidence(diagnostic, index, maxCandidates);
        if (definitionEvidence is not null)
        {
            result = ApplyDefinitionEvidence(result, definitionEvidence, maxCandidates);
        }

        return result;
    }

    private static DiagnosticRecord ApplyMethodEvidence(
        DiagnosticRecord diagnostic,
        MethodEvidence evidence)
    {
        var sourceFile = evidence.SingleFile;
        var sourceLine = evidence.Candidates.Count == 1
            ? evidence.Candidates[0].Line
            : null;
        var sourceSymbol = evidence.Candidates.Count == 1
            ? evidence.Candidates[0].Name
            : evidence.QuerySymbol;
        var sourceAssembly = evidence.SingleAssembly;
        var candidateValues = evidence.Candidates.Count > 1
            ? evidence.Candidates
                .Select(FormatSymbol)
                .ToArray()
            : null;

        return diagnostic with
        {
            SourceFile = sourceFile,
            SourceLine = sourceLine,
            SourceSymbol = sourceSymbol,
            SourceAssembly = sourceAssembly,
            AttributionConfidence = BetterConfidence(
                diagnostic.AttributionConfidence,
                evidence.Confidence),
            AttributionCandidates = candidateValues ?? diagnostic.AttributionCandidates
        };
    }

    private static DiagnosticRecord ApplyDefinitionEvidence(
        DiagnosticRecord diagnostic,
        DefinitionEvidence evidence,
        int maxCandidates)
    {
        var candidateValues = evidence.Candidates.Count > 1
            ? evidence.Candidates
                .Select(FormatDefinition)
                .Concat(evidence.References.Select(FormatReference))
                .Take(maxCandidates)
                .ToArray()
            : null;
        var referenceFiles = evidence.References
            .Select(reference => reference.File)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Take(maxCandidates)
            .ToArray();

        return diagnostic with
        {
            DefSourceFile = evidence.SingleDefinition?.File,
            DefSourceLine = evidence.Candidates.Count == 1
                ? evidence.SingleDefinition?.Line
                : null,
            DefReferenceFiles = referenceFiles.Length == 0 ? null : referenceFiles,
            AttributionConfidence = BetterConfidence(
                diagnostic.AttributionConfidence,
                evidence.Confidence),
            AttributionCandidates = candidateValues ?? diagnostic.AttributionCandidates
        };
    }

    private static MethodEvidence? FindMethodEvidence(
        DiagnosticRecord diagnostic,
        ProjectIndex index,
        int maxCandidates)
    {
        var queries = new List<(string? Type, string? Method)>();
        AddQuery(queries, diagnostic.OriginatingType, diagnostic.OriginatingMethod);
        AddQuery(queries, diagnostic.TargetType, diagnostic.TargetMethod);
        if (diagnostic.StackFrames is not null)
        {
            foreach (var frame in diagnostic.StackFrames.Take(3))
            {
                var origin = DiagnosticNormalizer.ExtractOrigin(frame);
                AddQuery(queries, origin.Type, origin.Method);
            }
        }

        foreach (var query in queries)
        {
            var candidates = FindSymbols(index, query.Type, query.Method);
            if (candidates.Count == 0 && query.Method is not null)
            {
                candidates = FindSymbols(index, query.Type, method: null);
            }

            if (candidates.Count == 0)
            {
                continue;
            }

            candidates = candidates
                .OrderBy(candidate => candidate.File, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.Line)
                .ThenBy(candidate => candidate.Name, StringComparer.Ordinal)
                .Take(maxCandidates)
                .ToList();
            return new MethodEvidence
            {
                QuerySymbol = ComposeQuerySymbol(query.Type, query.Method),
                Candidates = candidates,
                Confidence = candidates.Count == 1
                    ? "high"
                    : candidates.Select(candidate => candidate.File)
                        .Distinct(StringComparer.Ordinal)
                        .Count() == 1
                        ? "medium"
                        : "low"
            };
        }

        return null;
    }

    private static DefinitionEvidence? FindDefinitionEvidence(
        DiagnosticRecord diagnostic,
        ProjectIndex index,
        int maxCandidates)
    {
        if (string.IsNullOrWhiteSpace(diagnostic.DefName))
        {
            return null;
        }

        var definitions = index.Definitions
            .Where(definition => definition.Name.Equals(
                diagnostic.DefName,
                StringComparison.Ordinal))
            .Where(definition => diagnostic.DefType is null ||
                definition.Type.Equals(diagnostic.DefType, StringComparison.OrdinalIgnoreCase))
            .OrderBy(definition => definition.File, StringComparer.Ordinal)
            .ThenBy(definition => definition.Line)
            .ThenBy(definition => definition.Type, StringComparer.Ordinal)
            .Take(maxCandidates)
            .ToList();
        var references = index.References
            .Where(reference => reference.Name.Equals(
                diagnostic.DefName,
                StringComparison.Ordinal))
            .OrderBy(reference => reference.File, StringComparer.Ordinal)
            .ThenBy(reference => reference.Line)
            .Take(maxCandidates)
            .ToList();

        if (definitions.Count == 0 && references.Count == 0)
        {
            return null;
        }

        return new DefinitionEvidence
        {
            Candidates = definitions,
            References = references,
            SingleDefinition = definitions.Count == 1 ? definitions[0] : null,
            Confidence = definitions.Count == 1
                ? "high"
                : definitions.Count > 1
                    ? "low"
                    : references.Count == 1
                        ? "medium"
                        : "low"
        };
    }

    private static List<ProjectIndexSymbol> FindSymbols(
        ProjectIndex index,
        string? type,
        string? method)
    {
        var normalizedType = NormalizeType(type);
        var normalizedMethod = NormalizeMethod(method);
        var candidates = index.Symbols
            .Where(symbol => symbol.Kind == "method" || method is null && symbol.Kind == "type")
            .Where(symbol => normalizedType is null ||
                TypeMatches(symbol.TypeName ?? symbol.Name, normalizedType))
            .Where(symbol => normalizedMethod is null ||
                string.Equals(symbol.MethodName, normalizedMethod, StringComparison.Ordinal))
            .ToList();
        return candidates;
    }

    private static void AddQuery(
        ICollection<(string? Type, string? Method)> queries,
        string? type,
        string? method)
    {
        if (type is null && method is null)
        {
            return;
        }

        var query = (NormalizeType(type), NormalizeMethod(method));
        if (!queries.Contains(query))
        {
            queries.Add(query);
        }
    }

    private static bool TypeMatches(string candidate, string query)
    {
        var normalizedCandidate = NormalizeType(candidate) ?? string.Empty;
        return normalizedCandidate.Equals(query, StringComparison.Ordinal) ||
               normalizedCandidate.EndsWith($".{query}", StringComparison.Ordinal) ||
               query.EndsWith($".{normalizedCandidate}", StringComparison.Ordinal);
    }

    private static string? NormalizeType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.StartsWith("global::", StringComparison.Ordinal))
        {
            normalized = normalized[8..];
        }

        var assemblySeparator = normalized.IndexOf(',');
        if (assemblySeparator >= 0)
        {
            normalized = normalized[..assemblySeparator];
        }

        return normalized.Replace('+', '.').Trim();
    }

    private static string? NormalizeMethod(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        var parameterIndex = normalized.IndexOf('(');
        return parameterIndex >= 0
            ? normalized[..parameterIndex].Trim()
            : normalized;
    }

    private static string? ComposeQuerySymbol(string? type, string? method) =>
        type is null ? method : method is null ? type : $"{type}.{method}";

    private static string FormatSymbol(ProjectIndexSymbol symbol) =>
        symbol.Line is null
            ? $"{symbol.Name} @ {symbol.File}"
            : $"{symbol.Name} @ {symbol.File}:{symbol.Line}";

    private static string FormatDefinition(ProjectIndexDefinition definition) =>
        definition.Line is null
            ? $"{definition.Type}:{definition.Name} @ {definition.File}"
            : $"{definition.Type}:{definition.Name} @ {definition.File}:{definition.Line}";

    private static string FormatReference(ProjectIndexReference reference) =>
        reference.Line is null
            ? $"{reference.Name} @ {reference.File}"
            : $"{reference.Name} @ {reference.File}:{reference.Line}";

    private static string? BetterConfidence(string? current, string? incoming)
    {
        if (current is null)
        {
            return incoming;
        }

        return ConfidenceRank(incoming) > ConfidenceRank(current)
            ? incoming
            : current;
    }

    private static int ConfidenceRank(string? value) =>
        value switch
        {
            "high" => 3,
            "medium" => 2,
            "low" => 1,
            _ => 0
        };

    private sealed record MethodEvidence
    {
        public required string? QuerySymbol { get; init; }

        public required List<ProjectIndexSymbol> Candidates { get; init; }

        public required string Confidence { get; init; }

        public string? SingleFile => Candidates
            .Select(candidate => candidate.File)
            .Distinct(StringComparer.Ordinal)
            .Count() == 1
            ? Candidates[0].File
            : null;

        public string? SingleAssembly => Candidates
            .Select(candidate => candidate.AssemblyName)
            .Distinct(StringComparer.Ordinal)
            .Count() == 1
            ? Candidates[0].AssemblyName
            : null;
    }

    private sealed record DefinitionEvidence
    {
        public required List<ProjectIndexDefinition> Candidates { get; init; }

        public required List<ProjectIndexReference> References { get; init; }

        public ProjectIndexDefinition? SingleDefinition { get; init; }

        public required string Confidence { get; init; }
    }
}
