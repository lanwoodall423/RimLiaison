using System.Collections.ObjectModel;

namespace RimError.Core;

/// <summary>
/// Reviewed, bounded knowledge about recurring development failures. Entries
/// are descriptive guidance only; they never trigger autonomous source or
/// environment changes.
/// </summary>
public sealed record FailureKnowledgeEntry
{
    public required string SignatureCode { get; init; }

    public required string Component { get; init; }

    public required string Classification { get; init; }

    public required string KnownCause { get; init; }

    public IReadOnlyList<string> Conditions { get; init; } = [];

    public required string RecommendedAction { get; init; }

    public IReadOnlyList<string> InappropriateActions { get; init; } = [];

    public bool Retryable { get; init; }

    public required string EvidenceImpact { get; init; }

    public required string ResolutionProvenance { get; init; }

    public string Confidence { get; init; } = "reviewed";

    public string Status { get; init; } = "active";
}

public sealed record FailureKnowledgeMatch(
    FailureKnowledgeEntry Entry,
    string MatchReason);

public static class FailureKnowledgeCatalog
{
    public const string GeneratedStateTransactionFailure =
        "GENERATED_STATE_TRANSACTION_FAILURE";

    private static readonly IReadOnlyList<FailureKnowledgeEntry> Entries =
        new ReadOnlyCollection<FailureKnowledgeEntry>(
        [
            new FailureKnowledgeEntry
            {
                SignatureCode = GeneratedStateTransactionFailure,
                Component = "RimLiaison/DevBridge2",
                Classification = "development-transaction",
                KnownCause = "Generated observability or owner state was treated as a meaningful worktree mutation during a development transaction.",
                Conditions =
                [
                    ".rimdev/observability or equivalent generated state appears in a transaction diff",
                    "the source worktree is otherwise unchanged or its source fingerprint is stable"
                ],
                RecommendedAction = "Use the owning RimLiaison/DevBridge workflow, classify generated state as generated, and retry only after the transaction state is trustworthy.",
                InappropriateActions =
                [
                    "debugging mod source before separating infrastructure state from source changes",
                    "using a stale deployed artifact as proof of the current source"
                ],
                Retryable = true,
                EvidenceImpact = "Does not invalidate source or static-test evidence by itself; runtime freshness must be re-established if the transaction did not prove its generation.",
                ResolutionProvenance = "docs/context-bundle.md; tests/RimLiaison.Tests/ObservabilityIsolationTests.RepresentativeWorkflowLeavesWorktreeClean",
                Confidence = "reviewed",
                Status = "active"
            }
        ]);

    public static IReadOnlyList<FailureKnowledgeEntry> ReviewedEntries => Entries;

    public static FailureKnowledgeMatch? Match(
        string? signatureCode,
        string? summary,
        string? classification,
        IReadOnlyList<string>? relatedFiles = null)
    {
        string code = signatureCode?.Trim() ?? string.Empty;
        string text = string.Join(
            " ",
            new[] { code, summary, classification }
                .Where(static value => !string.IsNullOrWhiteSpace(value)))
            .ToLowerInvariant();
        bool generatedPath = (relatedFiles ?? [])
            .Any(path => path.Contains(".rimdev/observability", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("generated", StringComparison.OrdinalIgnoreCase));
        bool generatedTerms = text.Contains("generated observability", StringComparison.Ordinal) ||
            text.Contains("worktree-change", StringComparison.Ordinal) ||
            text.Contains("worktree change", StringComparison.Ordinal) ||
            text.Contains("transaction failure", StringComparison.Ordinal) &&
            text.Contains("state", StringComparison.Ordinal);

        FailureKnowledgeEntry? entry = Entries.FirstOrDefault(candidate =>
            string.Equals(candidate.SignatureCode, code, StringComparison.OrdinalIgnoreCase));
        if (entry is not null)
        {
            return new FailureKnowledgeMatch(entry, "exact-signature");
        }

        if (generatedPath || generatedTerms)
        {
            entry = Entries.First(candidate =>
                candidate.SignatureCode == GeneratedStateTransactionFailure);
            return new FailureKnowledgeMatch(entry, generatedPath
                ? "generated-state-path"
                : "generated-state-terms");
        }

        return null;
    }
}
