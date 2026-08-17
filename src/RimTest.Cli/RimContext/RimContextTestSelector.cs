using RimTest.Catalog;

namespace RimTest.RimContext;

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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(changedPaths);

        RimContextImpactResult context = await impactAdapter
            .AffectedAsync(changedPaths, cancellationToken)
            .ConfigureAwait(false);

        if (context.Status.Outcome == RimContextImpactOutcome.Cancelled)
        {
            return new RimTestSelectionResult
            {
                Status = "cancelled",
                ReasonCount = 1,
                ErrorCode = context.Status.ErrorCode ?? "RIMTEST_CANCELLED",
                Reasons = explain ?
                    [CreateStatusReason(context.Status.ErrorCode ?? "RIMTEST_CANCELLED", context.Status.Error)] :
                    null
            };
        }

        if (context.Status.Outcome == RimContextImpactOutcome.InvalidInput)
        {
            return new RimTestSelectionResult
            {
                Status = "invalid",
                ReasonCount = 1,
                ErrorCode = context.Status.ErrorCode ?? "RIMCONTEXT_INPUT_INVALID",
                Reasons = explain ?
                    [CreateStatusReason(context.Status.ErrorCode ?? "RIMCONTEXT_INPUT_INVALID", context.Status.Error)] :
                    null
            };
        }

        if (!context.Status.IsSuccess)
        {
            return Conservative(
                catalog,
                fallbackSuite,
                explain,
                context.Status.ErrorCode ?? "RIMCONTEXT_IMPACT_UNKNOWN",
                context.Status.Error);
        }

        return SelectKnown(catalog, context, fallbackSuite, explain);
    }

    private static RimTestSelectionResult SelectKnown(
        CatalogDocument catalog,
        RimContextImpactResult context,
        string? fallbackSuite,
        bool explain)
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

        if (impacts.Length == 0)
        {
            return new RimTestSelectionResult
            {
                Status = "ok",
                ReasonCount = 0,
                Tests = []
            };
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
        IReadOnlyList<RimContextImpact>? impacts = null)
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
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(static id => id, StringComparer.Ordinal)
                    .ToArray();
                selectedFallback = fallbackSuite;
            }
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
            FallbackSuite = selectedFallback,
            Reasons = explain ? reasons : null,
            ReasonsTruncated = explain && impacts is not null && impacts.Count > MaximumExplanationReasons
                ? true
                : null
        };
    }

    private static RimTestSelectionReason CreateStatusReason(
        string errorCode,
        string? error) => new()
        {
            Tier = "selection",
            Kind = "status",
            Reason = string.IsNullOrWhiteSpace(error) ? errorCode : error,
            Tests = []
        };

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
