using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RimLiaison.Git;
using RimLiaison.Observability;
using RimLiaison.Results;

namespace RimLiaison.Provenance;

public static class ValidationEvidenceSchema
{
    public const string Current = "rimliaison-validation-evidence/v1";
}

public static class ValidationEvidenceKinds
{
    public const string Static = "static";
    public const string Runtime = "runtime";
    public const string Dependency = "dependency";
}

public static class ValidationDecisionReasonCodes
{
    public const string NoRelevantChange = "NO_RELEVANT_SOURCE_CHANGE";
    public const string GeneratedStateIgnored = "GENERATED_STATE_IGNORED";
    public const string DocumentationOnly = "DOCUMENTATION_ONLY_NO_RUNTIME";
    public const string DataOnlyStatic = "DATA_ONLY_STATIC_VALIDATION";
    public const string RuntimeChange = "RUNTIME_CHANGE_REQUIRES_QUICKTEST";
    public const string DependencyChange = "DEPENDENCY_CHANGE_INVALIDATES_DEPENDENTS";
    public const string UnknownChange = "CHANGE_IMPACT_UNKNOWN";
    public const string EvidenceRecorded = "EVIDENCE_RECORDED_AFTER_RUN";
    public const string EvidenceValid = "EVIDENCE_VALID_IDENTICAL_INPUTS";
    public const string EvidenceMissing = "EVIDENCE_MISSING";
    public const string EvidenceInputMismatch = "EVIDENCE_INPUT_MISMATCH";
    public const string EvidenceResultNotPass = "EVIDENCE_RESULT_NOT_PASS";
    public const string EvidenceDeploymentMismatch = "EVIDENCE_DEPLOYMENT_MISMATCH";
    public const string EvidenceRuntimeGenerationMissing = "EVIDENCE_RUNTIME_GENERATION_MISSING";
    public const string EvidenceEnvironmentMismatch = "EVIDENCE_ENVIRONMENT_MISMATCH";
    public const string EvidenceTestIdentityMismatch = "EVIDENCE_TEST_IDENTITY_MISMATCH";
    public const string InfrastructureFailureRetryable = "INFRASTRUCTURE_FAILURE_RETRYABLE";
    public const string PublicationEvidenceReused = "PUBLICATION_REUSED_VALID_EVIDENCE";
    public const string PublicationValidationRequired = "PUBLICATION_VALIDATION_REQUIRED";
}

public sealed record ValidationEvidenceIdentity
{
    [JsonPropertyName("repository")]
    public required string Repository { get; init; }

    [JsonPropertyName("commitSha")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CommitSha { get; init; }

    [JsonPropertyName("contentFingerprint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContentFingerprint { get; init; }

    [JsonPropertyName("selectedSourceInputs")]
    public IReadOnlyList<string> SelectedSourceInputs { get; init; } = [];

    [JsonPropertyName("dependencyFingerprints")]
    public IReadOnlyDictionary<string, string> DependencyFingerprints { get; init; } =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal));

    [JsonPropertyName("buildArtifactSha256")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BuildArtifactSha256 { get; init; }

    [JsonPropertyName("deploymentArtifactSha256")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeploymentArtifactSha256 { get; init; }

    [JsonPropertyName("validationKind")]
    public required string ValidationKind { get; init; }

    [JsonPropertyName("coveredKinds")]
    public IReadOnlyList<string> CoveredKinds { get; init; } = [];

    [JsonPropertyName("suiteId")]
    public required string SuiteId { get; init; }

    [JsonPropertyName("testIds")]
    public IReadOnlyList<string> TestIds { get; init; } = [];

    [JsonPropertyName("rimWorldVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RimWorldVersion { get; init; }

    [JsonPropertyName("toolVersions")]
    public IReadOnlyDictionary<string, string> ToolVersions { get; init; } =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal));

    [JsonPropertyName("configuration")]
    public IReadOnlyDictionary<string, string> Configuration { get; init; } =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal));

    [JsonPropertyName("environmentFingerprint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EnvironmentFingerprint { get; init; }

    [JsonPropertyName("runtimeGeneration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RuntimeGeneration { get; init; }

    [JsonPropertyName("requiresRuntimeGeneration")]
    public bool RequiresRuntimeGeneration { get; init; }

    [JsonPropertyName("deploymentCorrespondence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeploymentCorrespondence { get; init; }

    [JsonIgnore]
    public bool HasSourceIdentity =>
        !string.IsNullOrWhiteSpace(ContentFingerprint) ||
        !string.IsNullOrWhiteSpace(CommitSha);

    [JsonIgnore]
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(Repository) &&
        !string.IsNullOrWhiteSpace(ValidationKind) &&
        !string.IsNullOrWhiteSpace(SuiteId) &&
        TestIds.Count > 0 &&
        HasSourceIdentity &&
        !string.IsNullOrWhiteSpace(EnvironmentFingerprint) &&
        (!RequiresRuntimeGeneration ||
            RuntimeGeneration.HasValue &&
            !string.IsNullOrWhiteSpace(BuildArtifactSha256) &&
            !string.IsNullOrWhiteSpace(DeploymentArtifactSha256) &&
            IsCorrespondingDeployment(DeploymentCorrespondence));

    public string ComputeFingerprint()
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, ValidationEvidenceSchema.Current);
        Append(hash, Repository);
        // A content fingerprint is stronger than a commit identity and makes
        // metadata-only commits reusable when the relevant inputs are byte
        // for byte unchanged.
        Append(hash, ContentFingerprint ?? CommitSha);
        Append(hash, ValidationKind);
        Append(hash, CoveredKinds);
        Append(hash, SuiteId);
        Append(hash, TestIds);
        Append(hash, SelectedSourceInputs);
        Append(hash, DependencyFingerprints);
        Append(hash, BuildArtifactSha256);
        Append(hash, DeploymentArtifactSha256);
        Append(hash, RimWorldVersion);
        Append(hash, ToolVersions);
        Append(hash, Configuration);
        Append(hash, EnvironmentFingerprint);
        Append(hash, RequiresRuntimeGeneration ? "runtime-required" : "runtime-not-required");
        Append(hash, RuntimeGeneration?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(hash, DeploymentCorrespondence);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public ValidationEvidenceIdentity Normalize()
    {
        return this with
        {
            Repository = Bound(Repository, 512) ?? "unknown",
            CommitSha = Bound(CommitSha, 128),
            ContentFingerprint = Bound(ContentFingerprint, 128),
            SelectedSourceInputs = NormalizeValues(SelectedSourceInputs, 256, 256),
            DependencyFingerprints = NormalizeMap(DependencyFingerprints, 64, 256, 256),
            BuildArtifactSha256 = Bound(BuildArtifactSha256, 128),
            DeploymentArtifactSha256 = Bound(DeploymentArtifactSha256, 128),
            ValidationKind = Bound(ValidationKind, 64) ?? ValidationEvidenceKinds.Static,
            CoveredKinds = NormalizeValues(CoveredKinds, 64, 64),
            SuiteId = Bound(SuiteId, 256) ?? "unknown",
            TestIds = NormalizeValues(TestIds, 128, 256),
            RimWorldVersion = Bound(RimWorldVersion, 128),
            ToolVersions = NormalizeMap(ToolVersions, 64, 128, 256),
            Configuration = NormalizeMap(Configuration, 64, 256, 2048),
            EnvironmentFingerprint = Bound(EnvironmentFingerprint, 128),
            DeploymentCorrespondence = Bound(DeploymentCorrespondence, 64)
        };
    }

    private static bool IsCorrespondingDeployment(string? correspondence) =>
        correspondence is not null &&
        correspondence is "synchronized" or "corresponds" or "generation-matches";

    private static IReadOnlyList<string> NormalizeValues(
        IEnumerable<string>? values,
        int maximumCount,
        int maximumLength) =>
        (values ?? [])
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Bound(value, maximumLength)!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .Take(maximumCount)
            .ToArray();

    private static IReadOnlyDictionary<string, string> NormalizeMap(
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

        return new ReadOnlyDictionary<string, string>(result);
    }

    private static void Append(IncrementalHash hash, string? value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        hash.AppendData(bytes);
        hash.AppendData([0]);
    }

    private static void Append(IncrementalHash hash, IEnumerable<string> values)
    {
        foreach (string value in values)
        {
            Append(hash, value);
        }

        Append(hash, "<end-list>");
    }

    private static void Append(IncrementalHash hash, IReadOnlyDictionary<string, string> values)
    {
        foreach ((string key, string value) in values.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
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

public sealed record ValidationEvidenceRecord
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = ValidationEvidenceSchema.Current;

    [JsonPropertyName("evidenceId")]
    public required string EvidenceId { get; init; }

    [JsonPropertyName("identity")]
    public required ValidationEvidenceIdentity Identity { get; init; }

    [JsonPropertyName("result")]
    public required string Result { get; init; }

    [JsonPropertyName("recordedAtUtc")]
    public DateTimeOffset RecordedAtUtc { get; init; }

    [JsonPropertyName("reusable")]
    public bool Reusable { get; init; }

    [JsonPropertyName("sourceProof")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceProof { get; init; }

    [JsonPropertyName("transactionId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TransactionId { get; init; }

    [JsonIgnore]
    public bool IsSelfConsistent =>
        SchemaVersion == ValidationEvidenceSchema.Current &&
        Identity is not null &&
        EvidenceId == CreateEvidenceId(Identity) &&
        Result is "pass" or "fail" or "infrastructure" or "cancelled";

    public static ValidationEvidenceRecord Create(
        ValidationEvidenceIdentity identity,
        string result,
        DateTimeOffset recordedAtUtc,
        string? sourceProof = null,
        string? transactionId = null)
    {
        ValidationEvidenceIdentity normalized = identity.Normalize();
        string normalizedResult = NormalizeResult(result);
        return new ValidationEvidenceRecord
        {
            EvidenceId = CreateEvidenceId(normalized),
            Identity = normalized,
            Result = normalizedResult,
            RecordedAtUtc = recordedAtUtc.ToUniversalTime(),
            Reusable = normalizedResult == "pass" && normalized.IsComplete,
            SourceProof = Bound(sourceProof, 256),
            TransactionId = Bound(transactionId, 256)
        };
    }

    public static string CreateEvidenceId(ValidationEvidenceIdentity identity) =>
        "ve-" + identity.Normalize().ComputeFingerprint();

    private static string NormalizeResult(string result) =>
        result.Trim().ToLowerInvariant() switch
        {
            "passed" or "success" => "pass",
            "failed" or "failure" => "fail",
            "infra" => "infrastructure",
            "pass" or "fail" or "infrastructure" or "cancelled" => result.Trim().ToLowerInvariant(),
            _ => "infrastructure"
        };

    private static string? Bound(string? value, int maximum) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim().Length <= maximum
                ? value.Trim()
                : value.Trim()[..maximum];
}

public static class ValidationEvidenceFactory
{
    public static string RepositoryIdentity(string rootPath)
    {
        string fullPath = Path.GetFullPath(rootPath)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/')
            .ToLowerInvariant();
        return "git:" + fullPath;
    }

    public static IReadOnlyDictionary<string, string> DefaultToolVersions() =>
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["rimliaison"] = typeof(ValidationEvidenceFactory).Assembly
                .GetName().Version?.ToString() ?? "unknown",
            [".NET"] = Environment.Version.ToString()
        });

    public static string DefaultEnvironmentFingerprint()
    {
        string input = string.Join(
            "\n",
            RuntimeInformation.OSDescription,
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.ProcessArchitecture,
            RuntimeInformation.OSArchitecture);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)))
            .ToLowerInvariant();
    }

    public static ValidationEvidenceRecord FromSuiteResult(
        string repository,
        string? commitSha,
        string? contentFingerprint,
        IReadOnlyList<string>? selectedSourceInputs,
        string suiteId,
        IReadOnlyList<string> testIds,
        RimTestSuiteResult result,
        DateTimeOffset recordedAtUtc,
        IReadOnlyDictionary<string, string>? dependencyFingerprints = null,
        IReadOnlyDictionary<string, string>? toolVersions = null,
        IReadOnlyDictionary<string, string>? configuration = null,
        string? environmentFingerprint = null,
        string? rimWorldVersion = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        RimTestArtifactFreshness? freshness = result.ArtifactFreshness;
        bool runtime = freshness is not null ||
            string.Equals(result.Orchestration?.RuntimeValidation, "PASS", StringComparison.OrdinalIgnoreCase);
        string kind = runtime ? ValidationEvidenceKinds.Runtime : ValidationEvidenceKinds.Static;
        string[] coveredKinds = runtime
            ? [ValidationEvidenceKinds.Static, ValidationEvidenceKinds.Runtime]
            : [ValidationEvidenceKinds.Static];
        string? deploymentCorrespondence = freshness is null
            ? null
            : string.Equals(freshness.EvaluationStatus, "FRESH", StringComparison.OrdinalIgnoreCase)
                ? "synchronized"
                : freshness.EvaluationStatus?.ToLowerInvariant();
        var identity = new ValidationEvidenceIdentity
        {
            Repository = repository,
            CommitSha = commitSha,
            ContentFingerprint = contentFingerprint ?? freshness?.SourceFingerprint,
            SelectedSourceInputs = selectedSourceInputs ?? [],
            DependencyFingerprints = dependencyFingerprints ?? new Dictionary<string, string>(StringComparer.Ordinal),
            BuildArtifactSha256 = freshness?.BuiltArtifactSha256,
            DeploymentArtifactSha256 = freshness?.DeployedArtifactSha256,
            ValidationKind = kind,
            CoveredKinds = coveredKinds,
            SuiteId = suiteId,
            TestIds = testIds,
            RimWorldVersion = rimWorldVersion,
            ToolVersions = toolVersions ?? new Dictionary<string, string>(StringComparer.Ordinal),
            Configuration = configuration ?? new Dictionary<string, string>(StringComparer.Ordinal),
            EnvironmentFingerprint = environmentFingerprint,
            RuntimeGeneration = freshness?.Generation,
            RequiresRuntimeGeneration = runtime,
            DeploymentCorrespondence = deploymentCorrespondence
        };
        return ValidationEvidenceRecord.Create(
            identity,
            result.Status,
            recordedAtUtc,
            freshness?.Proof,
            freshness?.TransactionId);
    }

    public static ValidationEvidenceIdentity CurrentIdentity(
        GitRepositoryStateSnapshot repository,
        IReadOnlyList<string> selectedSourceInputs,
        IReadOnlyList<string> requiredKinds,
        IReadOnlyList<string>? testIds = null,
        IReadOnlyDictionary<string, string>? dependencyFingerprints = null,
        string? contentFingerprint = null,
        IReadOnlyDictionary<string, string>? toolVersions = null,
        IReadOnlyDictionary<string, string>? configuration = null,
        string? environmentFingerprint = null,
        string? deploymentCorrespondence = null,
        int? runtimeGeneration = null,
        string? buildArtifactSha256 = null,
        string? deploymentArtifactSha256 = null)
    {
        bool runtime = requiredKinds.Contains(ValidationEvidenceKinds.Runtime, StringComparer.Ordinal);
        return new ValidationEvidenceIdentity
        {
            Repository = repository.Identity,
            CommitSha = repository.HeadSha,
            ContentFingerprint = contentFingerprint ?? repository.SourceFingerprint,
            SelectedSourceInputs = selectedSourceInputs,
            DependencyFingerprints = dependencyFingerprints ?? new Dictionary<string, string>(StringComparer.Ordinal),
            ValidationKind = runtime ? ValidationEvidenceKinds.Runtime : ValidationEvidenceKinds.Static,
            CoveredKinds = runtime
                ? [ValidationEvidenceKinds.Static, ValidationEvidenceKinds.Runtime]
                : [ValidationEvidenceKinds.Static],
            SuiteId = "publication",
            TestIds = testIds ?? requiredKinds,
            ToolVersions = toolVersions ?? new Dictionary<string, string>(StringComparer.Ordinal),
            Configuration = configuration ?? new Dictionary<string, string>(StringComparer.Ordinal),
            EnvironmentFingerprint = environmentFingerprint,
            DeploymentCorrespondence = deploymentCorrespondence,
            RuntimeGeneration = runtimeGeneration,
            RequiresRuntimeGeneration = runtime,
            BuildArtifactSha256 = buildArtifactSha256,
            DeploymentArtifactSha256 = deploymentArtifactSha256
        };
    }
}

public static class ValidationEvidenceParser
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static bool TryParse(AgentEvent record, out ValidationEvidenceRecord? evidence)
    {
        evidence = null;
        if (record.Data is not JsonElement data ||
            data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("validationEvidence", out JsonElement value) ||
            value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        try
        {
            ValidationEvidenceRecord? parsed = value.Deserialize<ValidationEvidenceRecord>(Options);
            if (parsed is null || !parsed.IsSelfConsistent)
            {
                return false;
            }

            evidence = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

public sealed record ValidationChangeAnalysis(
    string Category,
    IReadOnlyList<string> MeaningfulPaths,
    IReadOnlyList<string> GeneratedPaths,
    IReadOnlyList<string> RequiredKinds,
    string ReasonCode,
    bool RequiresBuild,
    bool RequiresRuntime)
{
    public bool HasMeaningfulChanges => MeaningfulPaths.Count > 0;
}

public static class ValidationChangeAnalyzer
{
    public static ValidationChangeAnalysis Analyze(IEnumerable<GitRepositoryChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        GitRepositoryChange[] all = changes
            .Where(static change => change is not null)
            .ToArray();
        string[] generated = all
            .Where(static change => change.Generated || IsGeneratedPath(change.Path))
            .Select(static change => Normalize(change.Path))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        string[] meaningful = all
            .Where(static change => !change.Generated && !IsGeneratedPath(change.Path))
            .Select(static change => Normalize(change.Path))
            .Where(static path => path.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();

        if (meaningful.Length == 0)
        {
            return new ValidationChangeAnalysis(
                generated.Length == 0 ? "none" : "generated",
                meaningful,
                generated,
                [],
                generated.Length == 0
                    ? ValidationDecisionReasonCodes.NoRelevantChange
                    : ValidationDecisionReasonCodes.GeneratedStateIgnored,
                false,
                false);
        }

        if (meaningful.All(IsDocumentation))
        {
            return new ValidationChangeAnalysis(
                "documentation",
                meaningful,
                generated,
                [],
                ValidationDecisionReasonCodes.DocumentationOnly,
                false,
                false);
        }

        if (meaningful.Any(IsDependency))
        {
            return new ValidationChangeAnalysis(
                "dependency",
                meaningful,
                generated,
                [ValidationEvidenceKinds.Static, ValidationEvidenceKinds.Runtime],
                ValidationDecisionReasonCodes.DependencyChange,
                true,
                true);
        }

        if (meaningful.All(IsData))
        {
            return new ValidationChangeAnalysis(
                "data",
                meaningful,
                generated,
                [ValidationEvidenceKinds.Static],
                ValidationDecisionReasonCodes.DataOnlyStatic,
                false,
                false);
        }

        if (meaningful.Any(IsRuntime))
        {
            return new ValidationChangeAnalysis(
                "runtime",
                meaningful,
                generated,
                [ValidationEvidenceKinds.Static, ValidationEvidenceKinds.Runtime],
                ValidationDecisionReasonCodes.RuntimeChange,
                true,
                true);
        }

        return new ValidationChangeAnalysis(
            "unknown",
            meaningful,
            generated,
            [ValidationEvidenceKinds.Static, ValidationEvidenceKinds.Runtime],
            ValidationDecisionReasonCodes.UnknownChange,
            true,
            true);
    }

    private static bool IsDocumentation(string path) =>
        path.StartsWith("docs/", StringComparison.OrdinalIgnoreCase) ||
        path.Equals("readme.md", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).ToLowerInvariant() is ".md" or ".txt" or ".rst" or ".adoc";

    private static bool IsData(string path) =>
        Path.GetExtension(path).Equals(".xml", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("defs/", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("/defs/", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("about/", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("/about/", StringComparison.OrdinalIgnoreCase);

    private static bool IsDependency(string path) =>
        Path.GetFileName(path).Equals("directory.packages.props", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(path).Equals("packages.lock.json", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).ToLowerInvariant() is ".lock" or ".csproj" or ".fsproj" or ".vbproj" or ".props" or ".targets" or ".sln" or ".slnx";

    private static bool IsRuntime(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".cs" or ".fs" or ".vb" ||
        path.StartsWith("source/", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("/source/", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("/assemblies/", StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    public static bool IsGeneratedPath(string path)
    {
        string normalized = Normalize(path);
        string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 2 &&
            string.Equals(segments[0], ".rimdev", StringComparison.OrdinalIgnoreCase) &&
            segments[1] is "observability" or "profiles" or "validation-proofs")
        {
            return true;
        }

        return segments.Any(segment => segment.Equals(".rimctx", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals(".rimerror", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("artifacts", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("coverage", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("testresults", StringComparison.OrdinalIgnoreCase));
    }
}

public sealed record ValidationPublicationDecision(
    string Action,
    string ReasonCode,
    string? ValidationKind,
    string? EvidenceId,
    string Explanation);

public sealed record ValidationPublicationResult(
    string Status,
    bool SafeToPublish,
    string PublicationAction,
    IReadOnlyList<ValidationPublicationDecision> Decisions,
    IReadOnlyList<string> ReusedEvidence,
    IReadOnlyList<string> InvalidatedEvidence,
    IReadOnlyList<string> RequiredValidation,
    string? NextAction,
    int ReusedEvidenceCount,
    int InvalidatedEvidenceCount,
    int NewValidationCount)
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; init; } = "rimliaison-publication-check/v1";
}

public static class ValidationPublicationGate
{
    public static ValidationPublicationResult Evaluate(
        ValidationChangeAnalysis analysis,
        ValidationEvidenceIdentity currentIdentity,
        IReadOnlyList<ValidationEvidenceRecord> evidence,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(currentIdentity);
        ArgumentNullException.ThrowIfNull(evidence);
        var decisions = new List<ValidationPublicationDecision>();
        var reused = new HashSet<string>(StringComparer.Ordinal);
        var invalidated = new HashSet<string>(StringComparer.Ordinal);
        string[] required = analysis.RequiredKinds
            .Where(static kind => !string.IsNullOrWhiteSpace(kind))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (!analysis.HasMeaningfulChanges)
        {
            string reason = analysis.ReasonCode;
            decisions.Add(new ValidationPublicationDecision(
                global::RimContext.Core.Context.RimContextDecisionActions.Skip,
                reason,
                null,
                null,
                analysis.Category == "generated"
                    ? "Generated owner state does not require source validation."
                    : "No meaningful source input requires validation for publication."));
            return Complete("pass", true, "skip", decisions, reused, invalidated, required, null);
        }

        if (analysis.Category == "documentation")
        {
            decisions.Add(new ValidationPublicationDecision(
                global::RimContext.Core.Context.RimContextDecisionActions.Skip,
                analysis.ReasonCode,
                null,
                null,
                "Documentation-only changes do not require RimWorld or mod runtime validation."));
            return Complete("pass", true, "skip", decisions, reused, invalidated, required, null);
        }

        ValidationEvidenceIdentity normalizedCurrent = currentIdentity.Normalize();
        foreach (string kind in required)
        {
            if (kind == ValidationEvidenceKinds.Runtime &&
                !normalizedCurrent.RuntimeGeneration.HasValue)
            {
                decisions.Add(new ValidationPublicationDecision(
                    global::RimContext.Core.Context.RimContextDecisionActions.Block,
                    ValidationDecisionReasonCodes.EvidenceRuntimeGenerationMissing,
                    kind,
                    null,
                    "Runtime evidence cannot be reused until the current DevBridge generation is known."));
                continue;
            }

            ValidationEvidenceRecord[] allForKind = evidence
                .Where(record => record.Identity.CoveredKinds.Contains(kind, StringComparer.Ordinal))
                .OrderByDescending(static record => record.RecordedAtUtc)
                .ThenBy(static record => record.EvidenceId, StringComparer.Ordinal)
                .ToArray();
            ValidationEvidenceRecord? latest = allForKind.FirstOrDefault();
            ValidationEvidenceRecord[] candidates = allForKind
                .Where(static record => record.Reusable && record.Result == "pass")
                .ToArray();
            ValidationEvidenceRecord? matching = candidates.FirstOrDefault(record =>
                MatchesForKind(record.Identity, normalizedCurrent, kind, out _));

            if (matching is not null)
            {
                reused.Add(matching.EvidenceId);
                decisions.Add(new ValidationPublicationDecision(
                    global::RimContext.Core.Context.RimContextDecisionActions.Reuse,
                    ValidationDecisionReasonCodes.EvidenceValid,
                    kind,
                    matching.EvidenceId,
                    "A passing immutable evidence record matches the relevant source, tool, configuration, and artifact inputs."));
                continue;
            }

            if (latest is not null)
            {
                string reason = latest.Result != "pass" || !latest.Reusable
                    ? ValidationDecisionReasonCodes.EvidenceResultNotPass
                    : InvalidationReason(latest.Identity, normalizedCurrent, kind);
                invalidated.Add(latest.EvidenceId);
                decisions.Add(new ValidationPublicationDecision(
                    global::RimContext.Core.Context.RimContextDecisionActions.Invalidate,
                    reason,
                    kind,
                    latest.EvidenceId,
                    "Previous evidence exists but cannot be reused for the current publication inputs."));
            }
            else
            {
                decisions.Add(new ValidationPublicationDecision(
                    global::RimContext.Core.Context.RimContextDecisionActions.Block,
                    ValidationDecisionReasonCodes.EvidenceMissing,
                    kind,
                    null,
                    "Required passing evidence is absent; publication must not silently rerun or bypass validation."));
            }
        }

        bool safe = decisions.All(static decision =>
            decision.Action is global::RimContext.Core.Context.RimContextDecisionActions.Reuse or
                global::RimContext.Core.Context.RimContextDecisionActions.Skip);
        string? nextAction = safe ? null : "rimliaison affected --run --json";
        if (safe)
        {
            decisions.Add(new ValidationPublicationDecision(
                global::RimContext.Core.Context.RimContextDecisionActions.Reuse,
                ValidationDecisionReasonCodes.PublicationEvidenceReused,
                null,
                null,
                "Publication can rely on the matching evidence without rerunning the same expensive validation."));
        }
        else
        {
            decisions.Add(new ValidationPublicationDecision(
                global::RimContext.Core.Context.RimContextDecisionActions.Block,
                ValidationDecisionReasonCodes.PublicationValidationRequired,
                null,
                null,
                "Publication is blocked until the required validation is run and produces reusable evidence."));
        }

        return Complete(
            safe ? "pass" : "blocked",
            safe,
            safe ? "reuse" : "block",
            decisions,
            reused,
            invalidated,
            required,
            nextAction);
    }

    private static bool MatchesForKind(
        ValidationEvidenceIdentity evidence,
        ValidationEvidenceIdentity current,
        string kind,
        out string reason)
    {
        reason = ValidationDecisionReasonCodes.EvidenceInputMismatch;
        if (!string.Equals(evidence.Repository, current.Repository, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(evidence.ValidationKind, current.ValidationKind, StringComparison.Ordinal) &&
            !evidence.CoveredKinds.Contains(kind, StringComparer.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(current.ContentFingerprint))
        {
            if (!string.Equals(evidence.ContentFingerprint, current.ContentFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        else if (!string.Equals(evidence.CommitSha, current.CommitSha, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!SequenceEqual(evidence.SelectedSourceInputs, current.SelectedSourceInputs) ||
            !MapEqual(evidence.DependencyFingerprints, current.DependencyFingerprints) ||
            !MapEqual(evidence.ToolVersions, current.ToolVersions) ||
            !MapEqual(evidence.Configuration, current.Configuration))
        {
            return false;
        }

        if (current.TestIds.Count > 0 &&
            !evidence.TestIds.All(testId => current.TestIds.Contains(testId, StringComparer.Ordinal)))
        {
            reason = ValidationDecisionReasonCodes.EvidenceTestIdentityMismatch;
            return false;
        }

        if (!string.IsNullOrWhiteSpace(current.EnvironmentFingerprint) &&
            !string.Equals(evidence.EnvironmentFingerprint, current.EnvironmentFingerprint, StringComparison.Ordinal))
        {
            reason = ValidationDecisionReasonCodes.EvidenceEnvironmentMismatch;
            return false;
        }

        if (kind == ValidationEvidenceKinds.Runtime)
        {
            if (!evidence.RequiresRuntimeGeneration ||
                !evidence.RuntimeGeneration.HasValue)
            {
                reason = ValidationDecisionReasonCodes.EvidenceRuntimeGenerationMissing;
                return false;
            }

            if (current.RuntimeGeneration.HasValue &&
                evidence.RuntimeGeneration != current.RuntimeGeneration)
            {
                reason = ValidationDecisionReasonCodes.EvidenceDeploymentMismatch;
                return false;
            }

            if (current.BuildArtifactSha256 is not null &&
                !string.Equals(evidence.BuildArtifactSha256, current.BuildArtifactSha256, StringComparison.OrdinalIgnoreCase))
            {
                reason = ValidationDecisionReasonCodes.EvidenceDeploymentMismatch;
                return false;
            }

            if (current.DeploymentArtifactSha256 is not null &&
                !string.Equals(evidence.DeploymentArtifactSha256, current.DeploymentArtifactSha256, StringComparison.OrdinalIgnoreCase))
            {
                reason = ValidationDecisionReasonCodes.EvidenceDeploymentMismatch;
                return false;
            }

            if (!IsCorrespondingDeployment(evidence.DeploymentCorrespondence) ||
                current.DeploymentCorrespondence is not null &&
                !IsCorrespondingDeployment(current.DeploymentCorrespondence))
            {
                reason = ValidationDecisionReasonCodes.EvidenceDeploymentMismatch;
                return false;
            }
        }

        return true;
    }

    private static string InvalidationReason(
        ValidationEvidenceIdentity evidence,
        ValidationEvidenceIdentity current,
        string kind)
    {
        if (kind == ValidationEvidenceKinds.Runtime &&
            (!IsCorrespondingDeployment(evidence.DeploymentCorrespondence) ||
             current.DeploymentCorrespondence is not null &&
             !IsCorrespondingDeployment(current.DeploymentCorrespondence)))
        {
            return ValidationDecisionReasonCodes.EvidenceDeploymentMismatch;
        }

        if (kind == ValidationEvidenceKinds.Runtime &&
            (current.RuntimeGeneration.HasValue && evidence.RuntimeGeneration != current.RuntimeGeneration ||
             current.BuildArtifactSha256 is not null &&
             !string.Equals(evidence.BuildArtifactSha256, current.BuildArtifactSha256, StringComparison.OrdinalIgnoreCase) ||
             current.DeploymentArtifactSha256 is not null &&
             !string.Equals(evidence.DeploymentArtifactSha256, current.DeploymentArtifactSha256, StringComparison.OrdinalIgnoreCase)))
        {
            return ValidationDecisionReasonCodes.EvidenceDeploymentMismatch;
        }

        if (current.TestIds.Count > 0 &&
            !evidence.TestIds.All(testId => current.TestIds.Contains(testId, StringComparer.Ordinal)))
        {
            return ValidationDecisionReasonCodes.EvidenceTestIdentityMismatch;
        }

        return ValidationDecisionReasonCodes.EvidenceInputMismatch;
    }

    private static bool IsCorrespondingDeployment(string? correspondence) =>
        correspondence is "synchronized" or "corresponds" or "generation-matches";

    private static bool SequenceEqual(IReadOnlyList<string> left, IReadOnlyList<string> right) =>
        left.OrderBy(static value => value, StringComparer.Ordinal)
            .SequenceEqual(right.OrderBy(static value => value, StringComparer.Ordinal), StringComparer.Ordinal);

    private static bool MapEqual(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right) =>
        left.Count == right.Count &&
        left.All(pair => right.TryGetValue(pair.Key, out string? value) &&
            string.Equals(pair.Value, value, StringComparison.Ordinal));

    private static ValidationPublicationResult Complete(
        string status,
        bool safe,
        string action,
        IReadOnlyList<ValidationPublicationDecision> decisions,
        IReadOnlySet<string> reused,
        IReadOnlySet<string> invalidated,
        IReadOnlyList<string> required,
        string? nextAction) =>
        new(
            status,
            safe,
            action,
            decisions,
            reused.OrderBy(static value => value, StringComparer.Ordinal).ToArray(),
            invalidated.OrderBy(static value => value, StringComparer.Ordinal).ToArray(),
            required,
            nextAction,
            reused.Count,
            invalidated.Count,
            decisions.Count(decision =>
                decision.ValidationKind is not null &&
                (decision.Action == global::RimContext.Core.Context.RimContextDecisionActions.Block ||
                 decision.Action == global::RimContext.Core.Context.RimContextDecisionActions.Invalidate)));
}
