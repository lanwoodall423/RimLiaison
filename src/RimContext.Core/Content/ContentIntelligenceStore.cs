using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RimContext.Core.Model;

namespace RimContext.Core.Content;

public static class ContentIntelligenceStorage
{
    public const string StoreEnvironmentVariable = "RIMCONTEXT_CONTENT_STORE";

    public static string ResolveDefaultPath(string? rootPath = null)
    {
        string? configured = Environment.GetEnvironmentVariable(StoreEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured) && Path.IsPathFullyQualified(configured.Trim()))
        {
            return Path.GetFullPath(configured.Trim());
        }

        string? canonicalWorkspace = FindWorkspaceRoot(rootPath);
        if (canonicalWorkspace is not null)
        {
            return Path.Combine(canonicalWorkspace, ".rimdev", "content-intelligence.jsonl");
        }

        string applicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(applicationData))
        {
            applicationData = Path.GetTempPath();
        }

        return Path.Combine(applicationData, "RimLiaison", "content-intelligence.jsonl");
    }

    private static string? FindWorkspaceRoot(string? rootPath)
    {
        DirectoryInfo? current = null;
        try
        {
            current = new DirectoryInfo(System.IO.Path.GetFullPath(
                rootPath ?? Directory.GetCurrentDirectory()));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        string? outermost = null;
        while (current is not null)
        {
            if (Directory.Exists(System.IO.Path.Combine(current.FullName, ".rimdev")))
            {
                outermost = current.FullName;
            }

            current = current.Parent;
        }

        return outermost;
    }
}

/// <summary>
/// Append-only shared content knowledge. Blueprints and evidence are kept as separate records;
/// the latest record for an identity wins, so a failed attempt never overwrites intent.
/// </summary>
public sealed class ContentIntelligenceStore
{
    private const string BlueprintRecord = "blueprint";
    private const string EvidenceRecord = "evidence";
    private const string PolicyRecord = "policy";
    private const string ArchetypeRecord = "archetype";
    private const string UsageRecord = "archetype_usage";
    private readonly object gate = new();
    private readonly string? path;

    public ContentIntelligenceStore(string? path = null)
    {
        this.path = string.IsNullOrWhiteSpace(path) ? null : System.IO.Path.GetFullPath(path);
        if (this.path is not null)
        {
            string? directory = System.IO.Path.GetDirectoryName(this.path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }
    }

    public string? Path => path;

    public void SaveBlueprint(ContentBlueprint blueprint)
    {
        ContentIntelligenceJson.ValidateBlueprint(blueprint);
        Append(BlueprintRecord, blueprint);
    }

    public void SaveEvidence(ContentEvidence evidence)
    {
        ContentIntelligenceJson.ValidateEvidence(evidence);
        Append(EvidenceRecord, evidence);
    }

    public void SetPolicy(ContentPrecedentPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (string.IsNullOrWhiteSpace(policy.PrecedentId))
        {
            throw new ArgumentException("A precedent policy requires a precedent ID.", nameof(policy));
        }

        Append(PolicyRecord, policy);
    }

    public void SaveArchetype(ContentArchetype archetype)
    {
        ContentPhase2Json.ValidateArchetype(archetype);
        Append(ArchetypeRecord, archetype);
    }

    public void SaveUsage(ContentArchetypeUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        Append(UsageRecord, usage);
    }

    public ContentIntelligenceSnapshot Snapshot()
    {
        lock (gate)
        {
            LoadedRecords records = Load();
            return new ContentIntelligenceSnapshot(
                records.Blueprints,
                records.Evidences,
                records.Policies,
                records.Archetypes,
                records.Usages);
        }
    }

    public ContentBlueprint? GetBlueprint(string blueprintId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blueprintId);
        lock (gate)
        {
            return Load().Blueprints.FirstOrDefault(item => item.BlueprintId == blueprintId);
        }
    }

    public ContentEvidence? GetEvidence(string evidenceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceId);
        lock (gate)
        {
            return Load().Evidences.FirstOrDefault(item => item.EvidenceId == evidenceId);
        }
    }

    public ContentQueryResult Query(ContentQueryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        int limit = Math.Clamp(request.Limit, 1, 100);
        int maxBytes = Math.Clamp(request.MaxBytes, 256, 1_048_576);
        lock (gate)
        {
            LoadedRecords records = Load();
            var evidenceByBlueprint = records.Evidences
                .GroupBy(item => item.BlueprintId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
            var policies = records.Policies
                .GroupBy(item => PolicyKey(item.Project, item.PrecedentId), StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);

            IEnumerable<(ContentBlueprint Blueprint, ContentEvidence? Evidence, int Trust, int ProjectRank)> candidates =
                records.Blueprints
                    .Select(blueprint =>
                    {
                        evidenceByBlueprint.TryGetValue(blueprint.BlueprintId, out ContentEvidence? evidence);
                        if (evidence is not null &&
                            (blueprint.Metadata.SourceIdentity is not { } expected ||
                             !ContentIntelligenceService.SourceIdentityMatches(expected, evidence.SourceIdentity)))
                        {
                            evidence = null;
                        }

                        int trust = ContentReuseSources.TrustRank(blueprint.Intent.ReuseSource);
                        int projectRank = ProjectRank(blueprint, request.Project);
                        return (Blueprint: blueprint, Evidence: evidence, Trust: trust, ProjectRank: projectRank);
                    })
                    .Where(item => Matches(item.Blueprint, request))
                    .Where(item => request.IncludeFailures || IsProven(item.Evidence, item.Trust))
                    .Where(item => !IsExcluded(item.Blueprint, request.Project, policies));

            var summaries = candidates
                .OrderBy(item => item.Trust)
                .ThenByDescending(item => item.ProjectRank)
                .ThenBy(item => item.Evidence?.CapturedAtUtc ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(item => item.Blueprint.BlueprintId, StringComparer.Ordinal)
                .Take(limit)
                .Select(item => ToSummary(item.Blueprint, item.Evidence, item.Trust, request.Project, policies))
                .ToList();

            bool truncated = candidates.Skip(limit).Any();
            while (summaries.Count > 0 && SerializedBytes(new ContentQueryResult(
                       ContentIntelligenceSchemas.Query,
                       summaries,
                       truncated,
                       limit,
                       maxBytes)) > maxBytes)
            {
                summaries.RemoveAt(summaries.Count - 1);
                truncated = true;
            }

            return new ContentQueryResult(
                ContentIntelligenceSchemas.Query,
                summaries,
                truncated,
                limit,
                maxBytes);
        }
    }

    private void Append(string recordType, object value)
    {
        if (path is null)
        {
            return;
        }

        string json = ContentIntelligenceJson.Serialize(new StoreEnvelope(
            ContentIntelligenceSchemas.Store,
            recordType,
            value));
        if (Encoding.UTF8.GetByteCount(json) > 131_072)
        {
            throw new InvalidOperationException("Content intelligence records must remain compact.");
        }
        lock (gate)
        {
            File.AppendAllText(path, json + Environment.NewLine, Encoding.UTF8);
        }
    }

    private LoadedRecords Load()
    {
        var blueprints = new Dictionary<string, ContentBlueprint>(StringComparer.Ordinal);
        var evidences = new Dictionary<string, ContentEvidence>(StringComparer.Ordinal);
        var policies = new Dictionary<string, ContentPrecedentPolicy>(StringComparer.Ordinal);
        var archetypes = new Dictionary<string, ContentArchetype>(StringComparer.Ordinal);
        var usages = new Dictionary<string, ContentArchetypeUsage>(StringComparer.Ordinal);
        if (path is null || !File.Exists(path))
        {
            return new LoadedRecords([], [], [], [], []);
        }

        foreach (string line in File.ReadLines(path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;
                if (!root.TryGetProperty("schemaVersion", out JsonElement schema) ||
                    schema.ValueKind != JsonValueKind.String ||
                    !string.Equals(
                        schema.GetString(),
                        ContentIntelligenceSchemas.Store,
                        StringComparison.Ordinal) ||
                    !root.TryGetProperty("recordType", out JsonElement type) ||
                    type.ValueKind != JsonValueKind.String ||
                    !root.TryGetProperty("value", out JsonElement value))
                {
                    continue;
                }
                switch (type.GetString())
                {
                    case BlueprintRecord:
                        ContentBlueprint blueprint = value.Deserialize<ContentBlueprint>(ContentIntelligenceJson.Options)
                            ?? throw new JsonException();
                        ContentIntelligenceJson.ValidateBlueprint(blueprint);
                        blueprints[blueprint.BlueprintId] = blueprint;
                        break;
                    case EvidenceRecord:
                        ContentEvidence evidence = value.Deserialize<ContentEvidence>(ContentIntelligenceJson.Options)
                            ?? throw new JsonException();
                        ContentIntelligenceJson.ValidateEvidence(evidence);
                        evidences[evidence.EvidenceId] = evidence;
                        break;
                    case PolicyRecord:
                        ContentPrecedentPolicy policy = value.Deserialize<ContentPrecedentPolicy>(ContentIntelligenceJson.Options)
                            ?? throw new JsonException();
                        policies[PolicyKey(policy.Project, policy.PrecedentId)] = policy;
                        break;
                    case ArchetypeRecord:
                        ContentArchetype archetype = value.Deserialize<ContentArchetype>(ContentIntelligenceJson.Options)
                            ?? throw new JsonException();
                        ContentPhase2Json.ValidateArchetype(archetype);
                        archetypes[ArchetypeKey(archetype)] = archetype;
                        break;
                    case UsageRecord:
                        ContentArchetypeUsage usage = value.Deserialize<ContentArchetypeUsage>(ContentIntelligenceJson.Options)
                            ?? throw new JsonException();
                        usages[usage.UsageId] = usage;
                        break;
                }
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException)
            {
                // A torn, malformed, or future record must not make existing knowledge unavailable.
            }
        }

        return new LoadedRecords(
            blueprints.Values.OrderBy(item => item.BlueprintId, StringComparer.Ordinal).ToArray(),
            evidences.Values.OrderBy(item => item.EvidenceId, StringComparer.Ordinal).ToArray(),
            policies.Values.OrderBy(item => PolicyKey(item.Project, item.PrecedentId), StringComparer.Ordinal).ToArray(),
            archetypes.Values.OrderBy(item => item.ArchetypeId, StringComparer.Ordinal).ToArray(),
            usages.Values.OrderBy(item => item.UsageId, StringComparer.Ordinal).ToArray());
    }

    private static bool Matches(ContentBlueprint blueprint, ContentQueryRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.ContentKind) &&
            !string.Equals(blueprint.Intent.ContentKind, request.ContentKind, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(request.GameplayRole) &&
            !string.Equals(blueprint.Intent.GameplayRole, request.GameplayRole, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            string query = request.Query.Trim();
            string haystack = string.Join('\n',
                blueprint.Intent.ContentKind,
                blueprint.Intent.GameplayRole,
                blueprint.Intent.ReuseSource,
                blueprint.Intent.DesignParameters is null
                    ? null
                    : string.Join(' ', blueprint.Intent.DesignParameters.Select(pair => pair.Key + " " + pair.Value)),
                blueprint.Intent.VanillaComparables is null ? null : string.Join(' ', blueprint.Intent.VanillaComparables),
                blueprint.Intent.FrameworkRequirements is null ? null : string.Join(' ', blueprint.Intent.FrameworkRequirements),
                blueprint.Intent.ProjectConstraints is null ? null : string.Join(' ', blueprint.Intent.ProjectConstraints),
                blueprint.Intent.ValidationExpectations is null ? null : string.Join(' ', blueprint.Intent.ValidationExpectations),
                blueprint.Metadata.Dependencies is null ? null : string.Join(' ', blueprint.Metadata.Dependencies),
                blueprint.Metadata.FrameworkDependencies is null ? null : string.Join(' ', blueprint.Metadata.FrameworkDependencies),
                blueprint.Metadata.EntityIdentifiers is null ? null : string.Join(' ', blueprint.Metadata.EntityIdentifiers),
                blueprint.Metadata.SourceFiles is null ? null : string.Join(' ', blueprint.Metadata.SourceFiles));
            if (haystack.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsProven(ContentEvidence? evidence, int trust) =>
        evidence is not null && string.Equals(evidence.Outcome.Final, "PASS", StringComparison.OrdinalIgnoreCase) ||
        evidence is not null && string.Equals(evidence.Outcome.Final, "SUCCESS", StringComparison.OrdinalIgnoreCase) ||
        trust == ContentReuseSources.TrustRank(ContentReuseSources.VanillaReference);

    private static int ProjectRank(ContentBlueprint blueprint, string? project) =>
        !string.IsNullOrWhiteSpace(project) &&
        (string.Equals(blueprint.Metadata.Project, project, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(blueprint.ProjectOverride, project, StringComparison.OrdinalIgnoreCase))
            ? 1
            : 0;

    private static bool IsExcluded(
        ContentBlueprint blueprint,
        string? project,
        IReadOnlyDictionary<string, ContentPrecedentPolicy> policies)
    {
        if (blueprint.ExcludedFromGlobalReuse == true)
        {
            return string.IsNullOrWhiteSpace(project) ||
                !string.Equals(blueprint.Metadata.Project, project, StringComparison.OrdinalIgnoreCase);
        }

        if (project is not null && policies.TryGetValue(PolicyKey(project, blueprint.BlueprintId), out ContentPrecedentPolicy? projectPolicy))
        {
            return projectPolicy.Excluded;
        }

        return policies.TryGetValue(PolicyKey(null, blueprint.BlueprintId), out ContentPrecedentPolicy? globalPolicy) &&
            globalPolicy.Excluded;
    }

    private static ContentPrecedentSummary ToSummary(
        ContentBlueprint blueprint,
        ContentEvidence? evidence,
        int trust,
        string? project,
        IReadOnlyDictionary<string, ContentPrecedentPolicy> policies)
    {
        IReadOnlyList<string>? constraints = blueprint.Intent.ProjectConstraints;
        ContentPrecedentPolicy? policy = project is not null &&
            policies.TryGetValue(PolicyKey(project, blueprint.BlueprintId), out ContentPrecedentPolicy? projectPolicy)
                ? projectPolicy
                : policies.GetValueOrDefault(PolicyKey(null, blueprint.BlueprintId));
        if (policy?.Constraints is { Count: > 0 })
        {
            constraints = (constraints ?? [])
                .Concat(policy.Constraints)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
        }

        return new ContentPrecedentSummary(
            blueprint.BlueprintId,
            blueprint.Intent.ContentKind,
            blueprint.Intent.GameplayRole,
            blueprint.Intent.ReuseSource,
            blueprint.Metadata.Project,
            blueprint.Metadata.SourceIdentity?.SourceFingerprint,
            blueprint.Metadata.SourceIdentity?.Commit,
            blueprint.Metadata.SourceFiles,
            blueprint.Metadata.EntityIdentifiers,
            blueprint.Metadata.Dependencies,
            constraints,
            evidence?.Outcome.Final,
            evidence?.EvidenceId,
            trust,
            blueprint.Intent.DesignParameters,
            blueprint.Intent.VanillaComparables,
            blueprint.Intent.FrameworkRequirements,
            blueprint.Intent.ValidationExpectations,
            evidence?.Errors,
            evidence?.Warnings,
            blueprint.Metadata.Repository,
            blueprint.ProjectOverride,
            blueprint.ExcludedFromGlobalReuse,
            blueprint.Metadata.FrameworkDependencies);
    }

    private static int SerializedBytes(ContentQueryResult result) =>
        Encoding.UTF8.GetByteCount(ContentIntelligenceJson.Serialize(result));
    private static string PolicyKey(string? project, string precedentId) =>
        (project ?? string.Empty) + "\0" + precedentId;

    private static string ArchetypeKey(ContentArchetype archetype) =>
        archetype.ArchetypeId + "\0" + archetype.Version.ToString(CultureInfo.InvariantCulture);

    private sealed record StoreEnvelope(string SchemaVersion, string RecordType, object Value);

    private sealed record LoadedRecords(
        IReadOnlyList<ContentBlueprint> Blueprints,
        IReadOnlyList<ContentEvidence> Evidences,
        IReadOnlyList<ContentPrecedentPolicy> Policies,
        IReadOnlyList<ContentArchetype> Archetypes,
        IReadOnlyList<ContentArchetypeUsage> Usages);
}
