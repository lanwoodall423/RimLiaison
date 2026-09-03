using RimContext.Core.Impact;
using RimLiaison.Catalog;

namespace RimLiaison.Validation;

public sealed record ContentValidationCoverageDecision(
    bool Applies,
    bool Sufficient,
    string? ErrorCode = null,
    string? Reason = null);

/// <summary>
/// Prevents infrastructure-only affected recipes from being promoted to proof
/// for content changes when no catalog entry covers the changed definition.
/// </summary>
public static class ContentValidationCoveragePolicy
{
    public const string InsufficientCode = "VALIDATION_COVERAGE_INSUFFICIENT";

    public static ContentValidationCoverageDecision Evaluate(
        string? contentKind,
        IReadOnlyList<string> changedPaths,
        ImpactGraph? graph,
        ActualImpact? actual,
        CatalogDocument catalog,
        IReadOnlyList<string> selectedTestIds)
    {
        ArgumentNullException.ThrowIfNull(changedPaths);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(selectedTestIds);

        if (!string.Equals(contentKind, "ThingDef", StringComparison.OrdinalIgnoreCase) ||
            !changedPaths.Any(IsXmlContentPath))
        {
            return new(false, true);
        }

        HashSet<string> changedNodeIds = (actual?.ChangedNodeIds ?? [])
            .ToHashSet(StringComparer.Ordinal);
        string[] changedDefinitions = (graph?.Nodes ?? [])
            .Where(node => node.Kind == "def" && changedNodeIds.Contains(node.Id))
            .SelectMany(node => new[] { node.Identity, node.DisplayName })
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        bool covered = selectedTestIds
            .Select(testId => catalog.Tests.FirstOrDefault(test => test.Id == testId))
            .Where(test => test is not null)
            .SelectMany(test => test!.Covers ?? [])
            .Any(coverage =>
                string.Equals(coverage.Kind, "def", StringComparison.OrdinalIgnoreCase) &&
                changedDefinitions.Contains(coverage.Name, StringComparer.Ordinal));
        if (covered)
        {
            return new(true, true);
        }

        return new(
            true,
            false,
            InsufficientCode,
            "The selected validation has no content-aware ThingDef coverage. " +
            "quicktest-smoke establishes toolchain readiness only; it does not validate the changed XML content.");
    }

    private static bool IsXmlContentPath(string path)
    {
        string normalized = path.Replace('\\', '/').TrimStart('.', '/');
        return Path.GetExtension(normalized).Equals(".xml", StringComparison.OrdinalIgnoreCase) &&
            (normalized.StartsWith("defs/", StringComparison.OrdinalIgnoreCase) ||
             normalized.Contains("/defs/", StringComparison.OrdinalIgnoreCase));
    }
}
