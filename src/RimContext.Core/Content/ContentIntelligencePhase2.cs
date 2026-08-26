using System.Globalization;
using System.Text;
using RimContext.Core.Model;

namespace RimContext.Core.Content;

public static class ContentStructuralFingerprinting
{
    public const string Version = "content-structure/v1";

    public static ContentStructuralFingerprint Compute(ContentBlueprint blueprint)
    {
        ArgumentNullException.ThrowIfNull(blueprint);
        var intent = blueprint.Intent;
        var builder = new StringBuilder()
            .Append("kind=").Append(intent.ContentKind ?? "?").Append('\n')
            .Append("role=").Append(intent.GameplayRole ?? "?").Append('\n')
            .Append("no-framework=").Append(intent.DeliberateNoFramework?.ToString() ?? "?").Append('\n');
        AppendMap(builder, "design", intent.DesignParameters);
        AppendValues(builder, "vanilla", intent.VanillaComparables);
        AppendValues(builder, "framework-requirement", intent.FrameworkRequirements);
        AppendValues(builder, "entity-shape", blueprint.Metadata.EntityIdentifiers?.Select(NormalizeEntity));
        AppendValues(builder, "dependency", blueprint.Metadata.Dependencies?.Select(NormalizeDependency));
        AppendValues(builder, "framework", blueprint.Metadata.FrameworkDependencies?.Select(NormalizeDependency));
        AppendValues(builder, "validation", intent.ValidationExpectations);
        string canonical = builder.ToString();
        return new ContentStructuralFingerprint(
            Version,
            StableEntityId.Create("content-shape", Version, canonical),
            canonical,
            intent.ContentKind,
            intent.GameplayRole);
    }

    private static void AppendMap(StringBuilder builder, string name, IReadOnlyDictionary<string, string>? values)
    {
        if (values is null)
        {
            builder.Append(name).Append("=?\n");
            return;
        }

        foreach (var pair in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            builder.Append(name).Append('.').Append(pair.Key).Append('=').Append(pair.Value).Append('\n');
        }
    }

    private static void AppendValues(StringBuilder builder, string name, IEnumerable<string>? values)
    {
        builder.Append(name).Append('=');
        if (values is not null)
        {
            builder.AppendJoin('|', values.Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal));
        }
        else
        {
            builder.Append('?');
        }

        builder.Append('\n');
    }

    private static string NormalizeEntity(string value)
    {
        int separator = value.IndexOf('/');
        return separator > 0 && value[..separator].EndsWith("Def", StringComparison.Ordinal)
            ? value[..separator] + "/<name>"
            : value;
    }

    private static string NormalizeDependency(string value)
    {
        int separator = value.IndexOf(':');
        return separator > 0 ? value[..separator] : value;
    }
}

public static class ContentQualificationEngine
{
    public static ContentQualificationResult Evaluate(
        ContentStructuralFingerprint fingerprint,
        IReadOnlyList<ContentBlueprint> blueprints,
        IReadOnlyList<ContentEvidence> evidences,
        ContentQualificationCriteria? criteria = null)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        ArgumentNullException.ThrowIfNull(blueprints);
        ArgumentNullException.ThrowIfNull(evidences);
        ContentQualificationCriteria selected = criteria ?? new ContentQualificationCriteria();
        var matched = blueprints
            .Where(blueprint => ContentStructuralFingerprinting.Compute(blueprint).Fingerprint == fingerprint.Fingerprint)
            .OrderBy(blueprint => blueprint.BlueprintId, StringComparer.Ordinal)
            .ToArray();
        var matchingIds = matched.Select(blueprint => blueprint.BlueprintId).ToHashSet(StringComparer.Ordinal);
        var relevantEvidence = evidences
            .Where(evidence => matchingIds.Contains(evidence.BlueprintId))
            .OrderBy(evidence => evidence.CapturedAtUtc ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(evidence => evidence.EvidenceId, StringComparer.Ordinal)
            .ToArray();
        int stale = 0;
        var successful = new List<ContentBlueprint>();
        int failures = 0;
        int repairs = 0;
        var attemptsByBlueprint = new Dictionary<string, ContentEvidence>(StringComparer.Ordinal);
        foreach (IGrouping<string, ContentEvidence> group in relevantEvidence.GroupBy(item => item.BlueprintId, StringComparer.Ordinal))
        {
            ContentBlueprint blueprint = matched.First(item => item.BlueprintId == group.Key);
            ContentEvidence[] current = group.ToArray();
            foreach (ContentEvidence evidence in current)
            {
                if (!ContentIntelligenceService.SourceIdentityMatches(
                        blueprint.Metadata.SourceIdentity ?? new ContentSourceIdentity(),
                        evidence.SourceIdentity))
                {
                    stale++;
                }
            }

            ContentEvidence? latest = current.LastOrDefault(evidence =>
                ContentIntelligenceService.SourceIdentityMatches(
                    blueprint.Metadata.SourceIdentity ?? new ContentSourceIdentity(),
                    evidence.SourceIdentity));
            if (latest is null)
            {
                continue;
            }

            attemptsByBlueprint[blueprint.BlueprintId] = latest;
            repairs += latest.Repairs?.Count ?? 0;
            if (!IsSuccessful(blueprint, latest, selected))
            {
                failures++;
                continue;
            }

            successful.Add(blueprint);
        }

        int distinctProjects = successful
            .Select(item => item.Metadata.Project)
            .Where(project => !string.IsNullOrWhiteSpace(project))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        int distinctRuns = successful
            .Select(item => item.Metadata.RunId)
            .Where(run => !string.IsNullOrWhiteSpace(run))
            .Distinct(StringComparer.Ordinal)
            .Count();
        double repairRate = attemptsByBlueprint.Count == 0
            ? 0
            : (double)repairs / attemptsByBlueprint.Count;
        var reasons = new List<string>();
        Require(reasons, successful.Count >= selected.MinimumSuccessfulImplementations,
            "MIN_SUCCESSFUL_IMPLEMENTATIONS");
        Require(reasons, distinctProjects >= selected.MinimumDistinctProjects,
            "MIN_DISTINCT_PROJECTS");
        Require(reasons, !selected.RequireIndependentRuns || distinctRuns >= selected.MinimumDistinctRuns,
            "MIN_DISTINCT_RUNS");
        Require(reasons, repairRate <= selected.MaximumRepairRate,
            "MAX_REPAIR_RATE");
        Require(reasons, !selected.RequireFreshEvidence || stale == 0,
            "STALE_EVIDENCE");
        Require(reasons, !selected.RequireAllApplicableValidation || failures == 0,
            "APPLICABLE_VALIDATION");
        if (relevantEvidence.Length == 0)
        {
            reasons.Add("NO_OBJECTIVE_EVIDENCE");
        }
        if (matched.Any(HasPrivateAssumptions))
        {
            reasons.Add("PROJECT_LOCAL_ASSUMPTIONS");
        }

        return new ContentQualificationResult(
            ContentPhase2Schemas.Qualification,
            reasons.Count == 0,
            selected,
            successful.Count,
            distinctProjects,
            distinctRuns,
            relevantEvidence.Length,
            repairs,
            failures,
            stale,
            repairRate,
            successful.Select(item => item.BlueprintId).OrderBy(item => item, StringComparer.Ordinal).ToArray(),
            reasons);
    }

    private static bool IsSuccessful(
        ContentBlueprint blueprint,
        ContentEvidence evidence,
        ContentQualificationCriteria criteria)
    {
        if (!string.Equals(evidence.Outcome.Final, "PASS", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(evidence.Outcome.Final, "SUCCESS", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!criteria.RequireAllApplicableValidation)
        {
            return true;
        }

        if (!Pass(evidence.Outcome.StaticReferenceValidation) ||
            !Pass(evidence.Outcome.Build) ||
            !Pass(evidence.Outcome.AffectedTests))
        {
            return false;
        }

        IReadOnlyList<string> expectations = blueprint.Intent.ValidationExpectations ?? [];
        if (expects(expectations, "runtime") && !Pass(evidence.Outcome.Runtime))
        {
            return false;
        }

        return !expects(expectations, "serialization") || Pass(evidence.Outcome.Serialization);
    }

    private static bool Pass(string? value) =>
        string.Equals(value, "PASS", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "SUCCESS", StringComparison.OrdinalIgnoreCase);
    private static bool HasPrivateAssumptions(ContentBlueprint blueprint) =>
        blueprint.ProjectOverride is not null ||
        blueprint.ExcludedFromGlobalReuse == true ||
        blueprint.Intent.ProjectConstraints is { Count: > 0 };

    private static bool expects(IReadOnlyList<string> values, string token) =>
        values.Any(value => value.Contains(token, StringComparison.OrdinalIgnoreCase));

    private static void Require(List<string> reasons, bool condition, string code)
    {
        if (!condition)
        {
            reasons.Add(code);
        }
    }
}

public static class ContentHistoricalReplay
{
    public static ContentReplayResult Replay(
        ContentArchetype archetype,
        IReadOnlyList<ContentBlueprint> blueprints)
    {
        ArgumentNullException.ThrowIfNull(archetype);
        ArgumentNullException.ThrowIfNull(blueprints);
        var failures = new List<string>();
        var checks = new List<string>
        {
            "structural-fingerprint",
            "required-intent",
            "data-only-template",
            "generalized-defaults",
            "framework-contract",
            "validation-contract",
            "deterministic-validation"
        };
        HashSet<string> replayedIds = blueprints
            .Select(item => item.BlueprintId)
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> supportingIds = (archetype.SupportingBlueprintIds ?? [])
            .ToHashSet(StringComparer.Ordinal);
        if (!replayedIds.SetEquals(supportingIds))
        {
            failures.Add("SUPPORTING_BLUEPRINT_MISMATCH");
        }
        if (archetype.Templates is null ||
            !archetype.Templates.ContainsKey("contentKind") ||
            !archetype.Templates.ContainsKey("gameplayRole"))
        {
            failures.Add("ARCHETYPE_TEMPLATE_MISSING");
        }

        foreach (ContentBlueprint blueprint in blueprints.OrderBy(item => item.BlueprintId, StringComparer.Ordinal))
        {
            string id = blueprint.BlueprintId;
            ContentStructuralFingerprint actual = ContentStructuralFingerprinting.Compute(blueprint);
            if (actual.Fingerprint != archetype.StructuralFingerprint.Fingerprint)
            {
                failures.Add(id + ":STRUCTURE_MISMATCH");
            }

            if (!string.Equals(blueprint.Intent.ContentKind, archetype.ContentKind, StringComparison.Ordinal) ||
                !string.Equals(blueprint.Intent.GameplayRole, archetype.GameplayRole, StringComparison.Ordinal))
            {
                failures.Add(id + ":REQUIRED_INTENT_MISMATCH");
            }

            if (!MapsMatch(blueprint.Intent.DesignParameters, archetype.Defaults))
            {
                failures.Add(id + ":DEFAULTS_MISMATCH");
            }

            if (!ValuesMatch(blueprint.Intent.FrameworkRequirements, archetype.FrameworkRequirements))
            {
                failures.Add(id + ":FRAMEWORK_MISMATCH");
            }

            if (!ValuesMatch(blueprint.Intent.ValidationExpectations, archetype.ValidationExpectations))
            {
                failures.Add(id + ":VALIDATION_MISMATCH");
            }
        }

        return new ContentReplayResult(
            ContentPhase2Schemas.Replay,
            failures.Count == 0 && blueprints.Count > 0,
            archetype.ArchetypeId,
            blueprints.Select(item => item.BlueprintId).OrderBy(item => item, StringComparer.Ordinal).ToArray(),
            failures,
            checks);
    }

    private static bool MapsMatch(
        IReadOnlyDictionary<string, string>? expected,
        IReadOnlyDictionary<string, string>? actual)
    {
        if (expected is null || expected.Count == 0)
        {
            return actual is null || actual.Count == 0;
        }

        return actual is not null &&
            expected.Count == actual.Count &&
            expected.All(pair => actual.TryGetValue(pair.Key, out string? value) &&
                string.Equals(pair.Value, value, StringComparison.Ordinal));
    }

    private static bool ValuesMatch(
        IReadOnlyList<string>? expected,
        IReadOnlyList<string>? actual)
    {
        if (expected is null || expected.Count == 0)
        {
            return actual is null || actual.Count == 0;
        }

        return actual is not null &&
            expected.OrderBy(value => value, StringComparer.Ordinal)
                .SequenceEqual(
                    actual.OrderBy(value => value, StringComparer.Ordinal),
                    StringComparer.Ordinal);
    }
}

public static class ContentArchetypeFactory
{
    public static ContentArchetype Create(
        ContentPrecedentCandidate candidate,
        IReadOnlyList<ContentBlueprint> blueprints,
        int version = 1,
        string? promotedAtUtc = null)
    {
        string[] supportingIds = candidate.Qualification.SupportingBlueprintIds
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
        ContentBlueprint representative = blueprints
            .OrderBy(item => item.BlueprintId, StringComparer.Ordinal)
            .First(item => supportingIds.Contains(item.BlueprintId, StringComparer.Ordinal));
        var templates = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["contentKind"] = representative.Intent.ContentKind ?? "unknown",
            ["gameplayRole"] = representative.Intent.GameplayRole ?? "unknown"
        };
        if (representative.Intent.DeliberateNoFramework.HasValue)
        {
            templates["deliberateNoFramework"] = representative.Intent.DeliberateNoFramework.Value.ToString();
        }

        return new ContentArchetype(
            ContentPhase2Schemas.Archetype,
            StableEntityId.Create("rimcontent-archetype", ContentPhase2Schemas.Archetype, candidate.StructuralFingerprint.Fingerprint),
            version,
            "active",
            candidate.StructuralFingerprint,
            representative.Intent.ContentKind,
            representative.Intent.GameplayRole,
            templates,
            representative.Intent.DesignParameters,
            representative.Intent.ProjectConstraints is null ? null : ["project constraints remain local"],
            representative.Intent.ValidationExpectations,
            supportingIds,
            promotedAtUtc ?? DateTimeOffset.UtcNow.ToString("O"),
            Examples: supportingIds,
            FrameworkRequirements: representative.Intent.FrameworkRequirements);
    }
}
