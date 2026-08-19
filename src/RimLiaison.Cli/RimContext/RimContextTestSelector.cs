using RimLiaison.Catalog;
using RimLiaison.Git;
using RimLiaison.Recovery;

namespace RimLiaison.RimContext;

public sealed class RimContextTestSelector
{
    private const int MaximumExplanationReasons = 128;
    private readonly IRimContextImpactAdapter impactAdapter;

    public RimContextTestSelector(IRimContextImpactAdapter impactAdapter)
    {
        this.impactAdapter = impactAdapter ?? throw new ArgumentNullException(nameof(impactAdapter));
    }

    public async Task<RimTestSelectionResult> SelectAsync(
        CatalogDocument catalog,
        IReadOnlyList<string> changedPaths,
        string? fallbackSuite,
        bool explain,
        CancellationToken cancellationToken = default,
        IReadOnlyList<GitChangedPath>? gitChanges = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(changedPaths);

        RimContextImpactResult context = await impactAdapter
            .AffectedAsync(changedPaths, cancellationToken)
            .ConfigureAwait(false);

        if (context.Status.Outcome == RimContextImpactOutcome.Cancelled)
        {
            return WithRecovery(new RimTestSelectionResult
            {
                Status = "cancelled",
                ReasonCount = 1,
                ErrorCode = context.Status.ErrorCode ?? "RIMTEST_CANCELLED",
                Reasons = explain ?
                    [CreateStatusReason(context.Status.ErrorCode ?? "RIMTEST_CANCELLED", context.Status.Error)] :
                    null
            }, context.Status);
        }

        if (context.Status.Outcome == RimContextImpactOutcome.InvalidInput)
        {
            return WithRecovery(new RimTestSelectionResult
            {
                Status = "invalid",
                ReasonCount = 1,
                ErrorCode = context.Status.ErrorCode ?? "RIMCONTEXT_INPUT_INVALID",
                Reasons = explain ?
                    [CreateStatusReason(context.Status.ErrorCode ?? "RIMCONTEXT_INPUT_INVALID", context.Status.Error)] :
                    null
            }, context.Status);
        }

        if (!context.Status.IsSuccess &&
            IsStaleContext(context.Status.ErrorCode))
        {
            RimTestSelectionResult conservative = Conservative(
                catalog,
                fallbackSuite,
                explain,
                "CONTEXT_STALE",
                context.Status.Error,
                nextAction: "rimliaison affected --run --json");
            return WithRecovery(
                conservative.Tests.Count > 0
                    ? conservative
                    : new RimTestSelectionResult
                    {
                        Status = "blocked",
                        ReasonCount = 1,
                        ErrorCode = "CONTEXT_STALE",
                        NextAction = "rimliaison affected --run --json",
                        Reasons = explain
                            ? conservative.Reasons
                            : null
                    },
                context.Status);
        }

        if (!context.Status.IsSuccess)
        {
            return WithRecovery(
                Conservative(
                    catalog,
                    fallbackSuite,
                    explain,
                    context.Status.ErrorCode ?? "RIMCONTEXT_IMPACT_UNKNOWN",
                    context.Status.Error),
                context.Status);
        }

        return WithRecovery(
            SelectKnown(catalog, context, fallbackSuite, explain, gitChanges),
            context.Status);
    }

    private static RimTestSelectionResult WithRecovery(
        RimTestSelectionResult result,
        RimContextAdapterStatus status)
    {
        if (status.RecoveryState == PrerequisiteRecoveryState.Ready &&
            status.RecoveryAttempts == 0)
        {
            return result;
        }

        return new RimTestSelectionResult
        {
            SchemaVersion = result.SchemaVersion,
            Status = result.Status,
            Tests = result.Tests,
            ReasonCount = result.ReasonCount,
            ErrorCode = result.ErrorCode,
            NextAction = result.NextAction,
            FallbackSuite = result.FallbackSuite,
            Reasons = result.Reasons,
            ReasonsTruncated = result.ReasonsTruncated,
            RecoveryState = status.RecoveryState.ToWireName(),
            RecoveryAttempts = Math.Max(0, status.RecoveryAttempts),
            RecoveryAction = status.RecoveryAction
        };
    }

    private static RimTestSelectionResult SelectKnown(
        CatalogDocument catalog,
        RimContextImpactResult context,
        string? fallbackSuite,
        bool explain,
        IReadOnlyList<GitChangedPath>? gitChanges)
    {
        RimContextImpact[] impacts = context.Impacts
            .OrderBy(ImpactTierOrder)
            .ThenBy(static impact => impact.Kind, StringComparer.Ordinal)
            .ThenBy(static impact => impact.Name ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static impact => impact.Id, StringComparer.Ordinal)
            .ToArray();

        if (context.Truncated)
        {
            return Conservative(
                catalog,
                fallbackSuite,
                explain,
                "RIMCONTEXT_RESULT_TRUNCATED",
                "RimContext did not prove that the affected result was complete.");
        }

        if (gitChanges?.Any(static change =>
                change.IsDeleted || change.IsRenamed) == true)
        {
            return Conservative(
                catalog,
                fallbackSuite,
                explain,
                "RIMCONTEXT_CHANGE_UNPROVEN",
                "Git reported a deleted or renamed path that RimContext cannot safely prove.",
                impacts);
        }

        if (impacts.Length == 0)
        {
            return Conservative(
                catalog,
                fallbackSuite,
                explain,
                "RIMCONTEXT_NO_TESTS",
                "RimContext returned no affected tests for changed paths.");
        }

        var testsByCoverage = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (CatalogTest? test in catalog.Tests ?? [])
        {
            if (test is null)
            {
                continue;
            }

            foreach (CatalogCoverage? coverage in test.Covers ?? [])
            {
                if (coverage is null)
                {
                    continue;
                }

                string key = CoverageKey(coverage.Kind, coverage.Name);
                if (!testsByCoverage.TryGetValue(key, out List<string>? testIds))
                {
                    testIds = [];
                    testsByCoverage.Add(key, testIds);
                }

                testIds.Add(test.Id);
            }
        }

        var selectedTests = new HashSet<string>(StringComparer.Ordinal);
        var mapped = new List<MappedImpact>();
        foreach (RimContextImpact impact in impacts)
        {
            if (string.IsNullOrWhiteSpace(impact.Name) ||
                !testsByCoverage.TryGetValue(
                    CoverageKey(impact.Kind, impact.Name),
                    out List<string>? matchingTests))
            {
                return Conservative(
                    catalog,
                    fallbackSuite,
                    explain,
                    "RIMCONTEXT_UNCOVERED_IMPACT",
                    "RimContext reported an impact without a registered catalog coverage mapping.",
                    impacts);
            }

            string[] sortedTests = matchingTests
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static id => id, StringComparer.Ordinal)
                .ToArray();
            foreach (string testId in sortedTests)
            {
                selectedTests.Add(testId);
            }

            mapped.Add(new MappedImpact(impact, sortedTests));
        }

        if (selectedTests.Count == 0)
        {
            return Conservative(
                catalog,
                fallbackSuite,
                explain,
                "RIMCONTEXT_NO_TESTS",
                "RimContext returned no affected tests for changed paths.",
                impacts);
        }

        var reasons = mapped
            .GroupBy(
                item => ImpactKey(item.Impact),
                StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(item => ImpactTierOrder(item.Impact))
            .ThenBy(item => item.Impact.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Impact.Name ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(item => item.Impact.Id, StringComparer.Ordinal)
            .ToArray();

        bool reasonsTruncated = false;
        IReadOnlyList<RimTestSelectionReason>? projectedReasons = null;
        if (explain)
        {
            projectedReasons = ProjectReasons(reasons, out reasonsTruncated);
        }

        return new RimTestSelectionResult
        {
            Status = "ok",
            Tests = selectedTests.OrderBy(static id => id, StringComparer.Ordinal).ToArray(),
            ReasonCount = reasons.Length,
            Reasons = projectedReasons,
            ReasonsTruncated = explain && reasonsTruncated ? true : null
        };
    }

    private static RimTestSelectionResult Conservative(
        CatalogDocument catalog,
        string? fallbackSuite,
        bool explain,
        string errorCode,
        string? error,
        IReadOnlyList<RimContextImpact>? impacts = null,
        string? nextAction = null)
    {
        IReadOnlyList<string> fallbackTests = [];
        string? selectedFallback = null;
        string? finalErrorCode = errorCode;
        if (!string.IsNullOrWhiteSpace(fallbackSuite))
        {
            if (CatalogNavigator.FindSuite(catalog, fallbackSuite) is null)
            {
                finalErrorCode = "FALLBACK_SUITE_NOT_FOUND";
            }
            else
            {
                fallbackTests = CatalogNavigator
                    .ResolvedTestIds(catalog, fallbackSuite)
                    .Where(testId =>
                        CatalogNavigator.FindTest(catalog, testId) is not null)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(static id => id, StringComparer.Ordinal)
                    .ToArray();
                if (fallbackTests.Count > 0)
                {
                    selectedFallback = fallbackSuite;
                }
                else
                {
                    finalErrorCode = "FALLBACK_SUITE_EMPTY";
                }
            }
        }

        string? finalNextAction = nextAction;
        if (fallbackTests.Count == 0 &&
            string.IsNullOrWhiteSpace(finalNextAction))
        {
            finalNextAction = NextActionForUnavailableFallback(
                fallbackSuite,
                finalErrorCode);
        }

        var reasons = new List<RimTestSelectionReason>();
        if (explain)
        {
            if (impacts is not null)
            {
                reasons.AddRange(impacts
                    .OrderBy(ImpactTierOrder)
                    .ThenBy(static impact => impact.Kind, StringComparer.Ordinal)
                    .ThenBy(static impact => impact.Name ?? string.Empty, StringComparer.Ordinal)
                    .ThenBy(static impact => impact.Id, StringComparer.Ordinal)
                    .Take(MaximumExplanationReasons)
                    .Select(impact => new RimTestSelectionReason
                    {
                        Tier = impact.Tier,
                        Kind = impact.Kind,
                        Id = impact.Id,
                        Name = impact.Name,
                        File = impact.File,
                        Line = impact.Line,
                        Reason = impact.Reason,
                        Confidence = impact.Confidence,
                        Tests = []
                    }));
            }

            if (reasons.Count == 0)
            {
                reasons.Add(CreateStatusReason(finalErrorCode, error));
            }
        }

        return new RimTestSelectionResult
        {
            Status = "conservative",
            Tests = fallbackTests,
            ReasonCount = Math.Max(1, impacts?.Count ?? 0),
            ErrorCode = finalErrorCode,
            NextAction = finalNextAction,
            FallbackSuite = selectedFallback,
            Reasons = explain ? reasons : null,
            ReasonsTruncated = explain && impacts is not null && impacts.Count > MaximumExplanationReasons
                ? true
                : null
        };
    }

    private static string NextActionForUnavailableFallback(
        string? fallbackSuite,
        string errorCode) => errorCode switch
        {
            "FALLBACK_SUITE_NOT_FOUND" => "rimliaison suites",
            "FALLBACK_SUITE_EMPTY" when !string.IsNullOrWhiteSpace(fallbackSuite) =>
                $"rimliaison suite show {fallbackSuite}",
            _ => "rimliaison affected --run --fallback-suite <suite>"
        };

    private static RimTestSelectionReason CreateStatusReason(
        string errorCode,
        string? error) => new()
        {
            Tier = "selection",
            Kind = "status",
            Reason = string.IsNullOrWhiteSpace(error) ? errorCode : error,
            Tests = []
        };

    private static bool IsStaleContext(string? errorCode) => errorCode is
        "INDEX_NOT_FOUND" or
        "INDEX_INCOMPATIBLE" or
        "ROOT_MISMATCH" or
        "CONTEXT_STALE";

    private static IReadOnlyList<RimTestSelectionReason> ProjectReasons(
        IReadOnlyList<MappedImpact> mapped,
        out bool truncated)
    {
        truncated = mapped.Count > MaximumExplanationReasons;
        return mapped
            .Take(MaximumExplanationReasons)
            .Select(item => new RimTestSelectionReason
            {
                Tier = item.Impact.Tier,
                Kind = item.Impact.Kind,
                Id = item.Impact.Id,
                Name = item.Impact.Name,
                File = item.Impact.File,
                Line = item.Impact.Line,
                Reason = item.Impact.Reason,
                Confidence = item.Impact.Confidence,
                Tests = item.Tests
            })
            .ToArray();
    }

    private static int ImpactTierOrder(RimContextImpact impact) => impact.Tier switch
    {
        "direct" => 0,
        "dependent" => 1,
        "runtimeRisk" => 2,
        _ => 3
    };

    private static string ImpactKey(RimContextImpact impact) => string.Join(
        '\u001f',
        impact.Tier,
        impact.Kind,
        impact.Id,
        impact.Name ?? string.Empty);

    private static string CoverageKey(string kind, string name) => string.Join(
        '\u001f',
        kind,
        name);

    private sealed record MappedImpact(
        RimContextImpact Impact,
        IReadOnlyList<string> Tests);
}
