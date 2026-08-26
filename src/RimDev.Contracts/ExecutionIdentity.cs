using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace RimDev.Contracts;

public static class SharedContractSchemas
{
    public const string Identity = "rimdev-execution-identity/v1";
    public const string Evidence = "rimdev-evidence/v1";
    public const string ValidationRequirement = "rimdev-validation-requirement/v1";
    public const string EntityReference = "rimdev-entity-reference/v1";
    public const string ToolEvent = "rimdev-tool-event/v1";
    public const string RuntimeValidationRequest = "rimdev-runtime-validation-request/v1";
}

public sealed record ExecutionIdentity
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = SharedContractSchemas.Identity;

    [JsonPropertyName("repositoryId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RepositoryId { get; init; }

    [JsonPropertyName("projectId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProjectId { get; init; }

    [JsonPropertyName("sourceRevision")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceRevision { get; init; }

    [JsonPropertyName("sourceFingerprint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceFingerprint { get; init; }

    [JsonPropertyName("sourceInputs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? SourceInputs { get; init; }

    [JsonPropertyName("dependencyFingerprints")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? DependencyFingerprints { get; init; }

    [JsonPropertyName("buildIdentity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BuildIdentity { get; init; }

    [JsonPropertyName("artifactHash")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ArtifactHash { get; init; }

    [JsonPropertyName("deploymentIdentity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeploymentIdentity { get; init; }

    [JsonPropertyName("processGeneration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ProcessGeneration { get; init; }

    [JsonPropertyName("runtimeInstanceId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RuntimeInstanceId { get; init; }

    [JsonPropertyName("executionId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExecutionId { get; init; }

    [JsonPropertyName("testIds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? TestIds { get; init; }

    [JsonPropertyName("toolVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolVersion { get; init; }

    [JsonPropertyName("toolVersions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? ToolVersions { get; init; }

    [JsonPropertyName("configuration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, string>? Configuration { get; init; }

    [JsonPropertyName("environmentFingerprint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EnvironmentFingerprint { get; init; }

    public ExecutionIdentity Normalize()
    {
        return this with
        {
            SchemaVersion = Bound(SchemaVersion, 64) ?? SharedContractSchemas.Identity,
            RepositoryId = Bound(RepositoryId, 512),
            ProjectId = Bound(ProjectId, 256),
            SourceRevision = Bound(SourceRevision, 256),
            SourceFingerprint = Bound(SourceFingerprint, 256),
            SourceInputs = NormalizeList(SourceInputs, 256, 512),
            DependencyFingerprints = NormalizeMap(DependencyFingerprints, 128, 256, 256),
            BuildIdentity = Bound(BuildIdentity, 256),
            ArtifactHash = Bound(ArtifactHash, 256),
            DeploymentIdentity = Bound(DeploymentIdentity, 256),
            RuntimeInstanceId = Bound(RuntimeInstanceId, 256),
            ExecutionId = Bound(ExecutionId, 256),
            TestIds = NormalizeList(TestIds, 256, 256),
            ToolVersion = Bound(ToolVersion, 128),
            ToolVersions = NormalizeMap(ToolVersions, 64, 128, 256),
            Configuration = NormalizeMap(Configuration, 128, 256, 2048),
            EnvironmentFingerprint = Bound(EnvironmentFingerprint, 256)
        };
    }

    public string ComputeFingerprint()
    {
        ExecutionIdentity normalized = Normalize();
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, normalized.SchemaVersion);
        Append(hash, normalized.RepositoryId);
        Append(hash, normalized.ProjectId);
        Append(hash, normalized.SourceRevision);
        Append(hash, normalized.SourceFingerprint);
        Append(hash, normalized.SourceInputs);
        Append(hash, normalized.DependencyFingerprints);
        Append(hash, normalized.BuildIdentity);
        Append(hash, normalized.ArtifactHash);
        Append(hash, normalized.DeploymentIdentity);
        Append(hash, normalized.ProcessGeneration?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(hash, normalized.RuntimeInstanceId);
        Append(hash, normalized.ExecutionId);
        Append(hash, normalized.TestIds);
        Append(hash, normalized.ToolVersion);
        Append(hash, normalized.ToolVersions);
        Append(hash, normalized.Configuration);
        Append(hash, normalized.EnvironmentFingerprint);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static IReadOnlyList<string>? NormalizeList(
        IEnumerable<string>? values,
        int maximumCount,
        int maximumLength)
    {
        string[] normalized = (values ?? [])
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Bound(value, maximumLength)!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .Take(maximumCount)
            .ToArray();
        return normalized.Length == 0 ? null : normalized;
    }

    private static IReadOnlyDictionary<string, string>? NormalizeMap(
        IReadOnlyDictionary<string, string>? values,
        int maximumCount,
        int maximumKeyLength,
        int maximumValueLength)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string key, string value) in (values ?? new Dictionary<string, string>())
                     .Where(static pair => !string.IsNullOrWhiteSpace(pair.Key))
                     .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                     .Take(maximumCount))
        {
            result[Bound(key, maximumKeyLength)!] = Bound(value, maximumValueLength) ?? string.Empty;
        }
        return result.Count == 0
            ? null
            : new ReadOnlyDictionary<string, string>(result);
    }

    private static void Append(IncrementalHash hash, string? value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(value ?? string.Empty));
        hash.AppendData([0]);
    }

    private static void Append(IncrementalHash hash, IEnumerable<string>? values)
    {
        foreach (string value in values ?? [])
        {
            Append(hash, value);
        }
        Append(hash, "<end-list>");
    }

    private static void Append(
        IncrementalHash hash,
        IReadOnlyDictionary<string, string>? values)
    {
        foreach ((string key, string value) in values ?? new Dictionary<string, string>())
        {
            Append(hash, key);
            Append(hash, value);
        }
        Append(hash, "<end-map>");
    }

    private static string? Bound(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        string trimmed = value.Trim();
        return trimmed.Length <= maximum ? trimmed : trimmed[..maximum];
    }
}

public enum IdentityMatchKind
{
    Exact,
    Compatible,
    Mismatch,
    Insufficient
}

public sealed record IdentityComparisonRequirements(
    bool RequireRepository = false,
    bool RequireSource = false,
    bool RequireArtifact = false,
    bool RequireDeployment = false,
    bool RequireProcessGeneration = false,
    bool RequireRuntimeInstance = false,
    bool RequireExecution = false)
{
    public static IdentityComparisonRequirements Static { get; } =
        new(RequireRepository: true, RequireSource: true);

    public static IdentityComparisonRequirements Runtime { get; } = new(
        RequireRepository: true,
        RequireSource: true,
        RequireArtifact: true,
        RequireDeployment: true,
        RequireProcessGeneration: true);
}

public sealed record IdentityComparisonResult(
    IdentityMatchKind Kind,
    IReadOnlyList<string> MissingFields,
    IReadOnlyList<string> ExtraFields,
    IReadOnlyList<string> MismatchedFields)
{
    public bool IsExact => Kind == IdentityMatchKind.Exact;
    public bool IsCompatible => Kind == IdentityMatchKind.Compatible;
    public bool IsMismatch => Kind == IdentityMatchKind.Mismatch;
    public bool IsInsufficient => Kind == IdentityMatchKind.Insufficient;

    public bool IsApplicable(IdentityComparisonRequirements requirements)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        return !IsMismatch && !IsInsufficient;
    }
}

public static class ExecutionIdentityComparer
{
    public static IdentityComparisonResult Compare(
        ExecutionIdentity? evidence,
        ExecutionIdentity? current,
        IdentityComparisonRequirements? requirements = null)
    {
        requirements ??= new IdentityComparisonRequirements();
        var missing = new List<string>();
        var extra = new List<string>();
        var mismatched = new List<string>();
        if (evidence is null || current is null)
        {
            return new(
                IdentityMatchKind.Insufficient,
                ["identity"],
                [],
                []);
        }

        ExecutionIdentity left = evidence.Normalize();
        ExecutionIdentity right = current.Normalize();
        if (left.SchemaVersion != SharedContractSchemas.Identity ||
            right.SchemaVersion != SharedContractSchemas.Identity)
        {
            return new(IdentityMatchKind.Insufficient, ["schemaVersion"], [], []);
        }

        CompareValue(left.RepositoryId, right.RepositoryId, "repositoryId", missing, extra, mismatched);
        CompareValue(left.ProjectId, right.ProjectId, "projectId", missing, extra, mismatched);
        CompareValue(left.SourceRevision, right.SourceRevision, "sourceRevision", missing, extra, mismatched);
        CompareValue(left.SourceFingerprint, right.SourceFingerprint, "sourceFingerprint", missing, extra, mismatched);
        CompareList(left.SourceInputs, right.SourceInputs, "sourceInputs", missing, extra, mismatched);
        CompareMap(left.DependencyFingerprints, right.DependencyFingerprints, "dependencyFingerprints", missing, extra, mismatched);
        CompareValue(left.BuildIdentity, right.BuildIdentity, "buildIdentity", missing, extra, mismatched);
        CompareValue(left.ArtifactHash, right.ArtifactHash, "artifactHash", missing, extra, mismatched);
        CompareValue(left.DeploymentIdentity, right.DeploymentIdentity, "deploymentIdentity", missing, extra, mismatched);
        CompareValue(left.ProcessGeneration, right.ProcessGeneration, "processGeneration", missing, extra, mismatched);
        CompareValue(left.RuntimeInstanceId, right.RuntimeInstanceId, "runtimeInstanceId", missing, extra, mismatched);
        CompareValue(left.ExecutionId, right.ExecutionId, "executionId", missing, extra, mismatched);
        CompareList(left.TestIds, right.TestIds, "testIds", missing, extra, mismatched);
        CompareValue(left.ToolVersion, right.ToolVersion, "toolVersion", missing, extra, mismatched);
        CompareMap(left.ToolVersions, right.ToolVersions, "toolVersions", missing, extra, mismatched);
        CompareMap(left.Configuration, right.Configuration, "configuration", missing, extra, mismatched);
        CompareValue(left.EnvironmentFingerprint, right.EnvironmentFingerprint, "environmentFingerprint", missing, extra, mismatched);
        RequirePair(
            left.RepositoryId,
            right.RepositoryId,
            "repositoryId",
            requirements.RequireRepository,
            missing);

        RequirePair(
            left.SourceFingerprint ?? left.SourceRevision,
            right.SourceFingerprint ?? right.SourceRevision,
            "source",
            requirements.RequireSource,
            missing);
        RequirePair(left.ArtifactHash, right.ArtifactHash, "artifactHash", requirements.RequireArtifact, missing);
        RequirePair(left.DeploymentIdentity, right.DeploymentIdentity, "deploymentIdentity", requirements.RequireDeployment, missing);
        RequirePair(left.ProcessGeneration, right.ProcessGeneration, "processGeneration", requirements.RequireProcessGeneration, missing);
        RequirePair(left.RuntimeInstanceId, right.RuntimeInstanceId, "runtimeInstanceId", requirements.RequireRuntimeInstance, missing);
        RequirePair(left.ExecutionId, right.ExecutionId, "executionId", requirements.RequireExecution, missing);

        bool requiredMissing =
            requirements.RequireRepository && missing.Contains("repositoryId", StringComparer.Ordinal) ||
            requirements.RequireSource &&
                missing.Any(static field => field is
                    "source" or "sourceRevision" or "sourceFingerprint") ||
            requirements.RequireArtifact && missing.Contains("artifactHash", StringComparer.Ordinal) ||
            requirements.RequireDeployment && missing.Contains("deploymentIdentity", StringComparer.Ordinal) ||
            requirements.RequireProcessGeneration && missing.Contains("processGeneration", StringComparer.Ordinal) ||
            requirements.RequireRuntimeInstance && missing.Contains("runtimeInstanceId", StringComparer.Ordinal) ||
            requirements.RequireExecution && missing.Contains("executionId", StringComparer.Ordinal);
        IdentityMatchKind kind = mismatched.Count != 0
            ? IdentityMatchKind.Mismatch
            : requiredMissing
                ? IdentityMatchKind.Insufficient
                : missing.Count != 0 || extra.Count != 0
                    ? IdentityMatchKind.Compatible
                    : IdentityMatchKind.Exact;
        return new(kind, Sort(missing), Sort(extra), Sort(mismatched));
    }

    private static void CompareValue<T>(
        T? evidence,
        T? current,
        string field,
        ICollection<string> missing,
        ICollection<string> extra,
        ICollection<string> mismatched)
        where T : struct
    {
        bool evidenceHasValue = evidence.HasValue;
        bool currentHasValue = current.HasValue;
        if (evidenceHasValue && currentHasValue)
        {
            if (!EqualityComparer<T>.Default.Equals(
                    evidence.GetValueOrDefault(),
                    current.GetValueOrDefault()))
            {
                mismatched.Add(field);
            }
        }
        else if (currentHasValue)
        {
            missing.Add(field);
        }
        else if (evidenceHasValue)
        {
            extra.Add(field);
        }
    }

    private static void CompareValue(
        string? evidence,
        string? current,
        string field,
        ICollection<string> missing,
        ICollection<string> extra,
        ICollection<string> mismatched)
    {
        bool evidenceHasValue = !string.IsNullOrWhiteSpace(evidence);
        bool currentHasValue = !string.IsNullOrWhiteSpace(current);
        if (evidenceHasValue && currentHasValue)
        {
            if (!string.Equals(evidence, current, StringComparison.Ordinal))
            {
                mismatched.Add(field);
            }
        }
        else if (currentHasValue)
        {
            missing.Add(field);
        }
        else if (evidenceHasValue)
        {
            extra.Add(field);
        }
    }

    private static void CompareList(
        IReadOnlyList<string>? evidence,
        IReadOnlyList<string>? current,
        string field,
        ICollection<string> missing,
        ICollection<string> extra,
        ICollection<string> mismatched)
    {
        string[] left = evidence?.OrderBy(static value => value, StringComparer.Ordinal).ToArray() ?? [];
        string[] right = current?.OrderBy(static value => value, StringComparer.Ordinal).ToArray() ?? [];
        if (left.Length == 0 && right.Length == 0)
        {
            return;
        }
        if (left.Length == 0)
        {
            missing.Add(field);
        }
        else if (right.Length == 0)
        {
            extra.Add(field);
        }
        else if (!left.SequenceEqual(right, StringComparer.Ordinal))
        {
            mismatched.Add(field);
        }
    }

    private static void CompareMap(
        IReadOnlyDictionary<string, string>? evidence,
        IReadOnlyDictionary<string, string>? current,
        string field,
        ICollection<string> missing,
        ICollection<string> extra,
        ICollection<string> mismatched)
    {
        bool evidenceEmpty = evidence is null || evidence.Count == 0;
        bool currentEmpty = current is null || current.Count == 0;
        if (evidenceEmpty && currentEmpty)
        {
            return;
        }
        if (evidenceEmpty)
        {
            missing.Add(field);
        }
        else if (currentEmpty)
        {
            extra.Add(field);
        }
        else if (evidence!.Count != current!.Count ||
                 evidence.Any(pair => !current.TryGetValue(pair.Key, out string? value) ||
                                      !string.Equals(pair.Value, value, StringComparison.Ordinal)))
        {
            mismatched.Add(field);
        }
    }

    private static void RequirePair<T>(
        T? evidence,
        T? current,
        string field,
        bool required,
        ICollection<string> missing)
        where T : struct
    {
        if (required && (!evidence.HasValue || !current.HasValue))
        {
            missing.Add(field);
        }
    }

    private static void RequirePair(
        string? evidence,
        string? current,
        string field,
        bool required,
        ICollection<string> missing)
    {
        if (required && (string.IsNullOrWhiteSpace(evidence) ||
                         string.IsNullOrWhiteSpace(current)))
        {
            missing.Add(field);
        }
    }

    private static string[] Sort(IEnumerable<string> values) =>
        values.Distinct(StringComparer.Ordinal).OrderBy(static value => value, StringComparer.Ordinal).ToArray();
}
