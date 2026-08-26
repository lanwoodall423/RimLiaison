using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimContext.Core.Impact;

public sealed class ImpactLearningStore
{
    private readonly string relationshipPath;
    private readonly string overridePath;
    private readonly object gate = new();

    public ImpactLearningStore(string? path = null)
    {
        relationshipPath = path ?? ResolveDefaultPath();
        overridePath = relationshipPath + ".overrides";
    }

    public static string ResolveDefaultPath(string? rootPath = null)
    {
        string? configured = Environment.GetEnvironmentVariable("RIMCONTEXT_IMPACT_LEARNING_STORE");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        string root = rootPath ?? Directory.GetCurrentDirectory();
        return Path.Combine(root, ".rimdev", "impact-learning.jsonl");
    }

    public IReadOnlyList<LearnedImpactRelationship> Read(
        string? project = null,
        string? frameworkVersion = null,
        string? rimWorldVersion = null)
    {
        lock (gate)
        {
            var records = ReadRelationships()
                .Where(record => Applies(record, project, frameworkVersion, rimWorldVersion))
                .ToArray();
            var overrides = ReadOverrides()
                .Where(item => Applies(item, project))
                .ToArray();
            var localKeys = records
                .Where(record => record.Scope == "project" && string.Equals(record.Project, project, StringComparison.Ordinal))
                .Select(Key)
                .ToHashSet(StringComparer.Ordinal);
            return records
                .Where(record => !overrides.Any(item => item.Excluded && Matches(item, record)))
                .Where(record => record.Scope == "project" && string.Equals(record.Project, project, StringComparison.Ordinal) ||
                    record.Scope == "global" && !localKeys.Contains(Key(record)))
                .GroupBy(Key, StringComparer.Ordinal)
                .Select(Aggregate)
                .OrderByDescending(record => record.Status == "proven")
                .ThenByDescending(record => record.SupportCount)
                .ThenBy(record => record.FromIdentity, StringComparer.Ordinal)
                .ThenBy(record => record.ToIdentity, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public void Append(LearnedImpactRelationship relationship)
    {
        ArgumentNullException.ThrowIfNull(relationship);
        lock (gate)
        {
            AppendLine(relationshipPath, relationship);
        }
    }

    public void AppendOverride(ImpactLearningOverride learningOverride)
    {
        ArgumentNullException.ThrowIfNull(learningOverride);
        lock (gate)
        {
            AppendLine(overridePath, learningOverride);
        }
    }
    public IReadOnlyList<global::RimDev.Contracts.RemediationPrecedent> ReadRemediationPrecedents(
        string failureFamily,
        global::RimDev.Contracts.EntityReference? subject = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureFamily);
        lock (gate)
        {
            return ReadLines<global::RimDev.Contracts.RemediationPrecedent>(relationshipPath)
                .Where(precedent =>
                    precedent.SchemaVersion == global::RimDev.Contracts.FailureContractSchemas.RemediationPrecedent &&
                    precedent.FailureFamily.Equals(failureFamily, StringComparison.Ordinal) &&
                    (subject is null ||
                        precedent.Subject is not null &&
                        precedent.Subject.Kind == subject.Kind &&
                        precedent.Subject.Id == subject.Id))
                .GroupBy(precedent => precedent.PrecedentId, StringComparer.Ordinal)
                .Select(group => group.Last().Normalize())
                .Where(precedent => precedent.Status.Equals("proven", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(precedent => precedent.SupportCount)
                .ThenBy(precedent => precedent.PrecedentId, StringComparer.Ordinal)
                .Take(16)
                .ToArray();
        }
    }

    public bool RecordValidatedRemediation(
        global::RimDev.Contracts.RemediationPrecedent precedent)
    {
        ArgumentNullException.ThrowIfNull(precedent);
        global::RimDev.Contracts.RemediationPrecedent normalized = precedent.Normalize();
        global::RimDev.Contracts.ExecutionIdentity identity = normalized.SuccessfulValidationIdentity;
        if (!normalized.Status.Equals("proven", StringComparison.OrdinalIgnoreCase) ||
            normalized.Evidence.Count == 0 ||
            string.IsNullOrWhiteSpace(identity.RepositoryId) ||
            string.IsNullOrWhiteSpace(identity.SourceFingerprint ?? identity.SourceRevision))
        {
            return false;
        }

        lock (gate)
        {
            AppendLine(relationshipPath, normalized);
        }
        return true;
    }
    public void SetRemediationEligibility(
        global::RimDev.Contracts.RemediationPrecedent precedent,
        bool eligible,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(precedent);
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("An administrative remediation decision requires a reason.", nameof(reason));
        }

        lock (gate)
        {
            AppendLine(
                relationshipPath,
                precedent.Normalize() with
                {
                    Status = eligible ? "proven" : "deprecated",
                    Applicability = precedent.Applicability
                        .Concat(["administrative decision: " + reason.Trim()])
                        .Take(16)
                        .ToArray()
                });
        }
    }

    private IReadOnlyList<LearnedImpactRelationship> ReadRelationships() =>
        ReadLines<LearnedImpactRelationship>(relationshipPath)
            .Where(item => string.Equals(item.SchemaVersion, ValidationPlanSchemas.LearningCurrent, StringComparison.Ordinal))
            .ToArray();

    private IReadOnlyList<ImpactLearningOverride> ReadOverrides() =>
        ReadLines<ImpactLearningOverride>(overridePath).ToArray();

    private static IEnumerable<T> ReadLines<T>(string path)
    {
        if (!File.Exists(path))
        {
            yield break;
        }

        foreach (string line in File.ReadLines(path).Take(4096))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            T? value;
            try
            {
                value = JsonSerializer.Deserialize<T>(line, Options);
            }
            catch (JsonException)
            {
                value = default;
            }
            if (value is not null)
            {
                yield return value;
            }
        }
    }

    private static void AppendLine<T>(string path, T value)
    {
        string? parent = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream);
        writer.WriteLine(JsonSerializer.Serialize(value, Options));
    }

    private static LearnedImpactRelationship Aggregate(
        IGrouping<string, LearnedImpactRelationship> group)
    {
        LearnedImpactRelationship latest = group.Last();
        return latest with
        {
            SupportCount = group.Sum(item => Math.Max(1, item.SupportCount)),
            IndependentObservations = group.Max(item => Math.Max(1, item.IndependentObservations)),
            Status = group.Any(item => item.Status == "proven") ? "proven" : "tentative",
            EvidenceIds = group.SelectMany(item => item.EvidenceIds ?? [])
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .Take(128)
                .ToArray()
        };
    }

    private static bool Applies(
        LearnedImpactRelationship item,
        string? project,
        string? frameworkVersion,
        string? rimWorldVersion) =>
        (item.Scope == "global" || item.Scope == "project" && item.Project == project) &&
        (item.FrameworkVersion is null || item.FrameworkVersion == frameworkVersion) &&
        (item.RimWorldVersion is null || item.RimWorldVersion == rimWorldVersion);

    private static bool Applies(ImpactLearningOverride item, string? project) =>
        item.Project is null || item.Project == project;

    private static string Key(LearnedImpactRelationship item) =>
        string.Join("\0", item.FromIdentity, item.ToIdentity, item.RelationshipKind,
            item.Project, item.FrameworkVersion, item.RimWorldVersion);

    private static bool Matches(
        ImpactLearningOverride learningOverride,
        LearnedImpactRelationship relationship) =>
        string.Equals(learningOverride.FromIdentity, relationship.FromIdentity, StringComparison.Ordinal) &&
        string.Equals(learningOverride.ToIdentity, relationship.ToIdentity, StringComparison.Ordinal) &&
        string.Equals(learningOverride.RelationshipKind, relationship.RelationshipKind, StringComparison.Ordinal) &&
        (learningOverride.Project is null ||
            string.Equals(learningOverride.Project, relationship.Project, StringComparison.Ordinal));

    private static string Key(ImpactLearningOverride item) =>
        string.Join("\0", item.FromIdentity, item.ToIdentity, item.RelationshipKind,
            item.Project, null, null);

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };
}

public sealed class ImpactLearningService
{
    private readonly ImpactLearningStore store;

    public ImpactLearningService(ImpactLearningStore? store = null)
    {
        this.store = store ?? new ImpactLearningStore();
    }

    public ImpactLearningResult Observe(ImpactLearningObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        bool strong = observation.CausalAttribution &&
            (observation.RevertedChange || observation.TargetedReproduction ||
             observation.DeterministicRelationship || observation.RimErrorAttribution ||
             observation.IndependentObservations >= 2);
        if (!observation.CausalAttribution)
        {
            return new ImpactLearningResult(false, false, null, "causal attribution is absent");
        }
        if (!strong)
        {
            return new ImpactLearningResult(false, false, null, "one weak or coincident observation is insufficient");
        }

        bool promoteGlobal = observation.GlobalCandidate &&
            (observation.RevertedChange || observation.TargetedReproduction ||
             observation.DeterministicRelationship || observation.IndependentObservations >= 2);
        var relationship = new LearnedImpactRelationship(
            ValidationPlanSchemas.LearningCurrent,
            observation.FromIdentity,
            observation.ToIdentity,
            observation.RelationshipKind,
            observation.ImpactClass,
            observation.Provenance with
            {
                Source = observation.Provenance.Source,
                EvidenceClass = observation.RimErrorAttribution
                    ? ImpactEvidenceClasses.Learned
                    : observation.Provenance.EvidenceClass
            },
            promoteGlobal ? "global" : "project",
            observation.Project,
            observation.SourceIdentity.FrameworkVersion,
            observation.SourceIdentity.RimWorldVersion,
            observation.SourceIdentity.SourceRevision,
            1,
            Math.Max(1, observation.IndependentObservations),
            promoteGlobal ? "proven" : "tentative",
            [observation.EvidenceId]);
        store.Append(relationship);
        return new ImpactLearningResult(true, promoteGlobal, relationship);
    }

    public IReadOnlyList<LearnedImpactRelationship> Applicable(
        ValidationSourceIdentity identity) =>
        store.Read(identity.Project, identity.FrameworkVersion, identity.RimWorldVersion);

    public void ExcludeForProject(
        ImpactLearningOverride learningOverride) =>
        store.AppendOverride(learningOverride);
}
