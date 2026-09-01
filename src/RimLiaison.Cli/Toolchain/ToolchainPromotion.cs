using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RimLiaison.Git;
using RimLiaison.DevBridge;
using RimLiaison.RimDev;
using RimLiaison.Qualification;



namespace RimLiaison.Toolchain;

public static class ToolchainPromotionSchemas
{
    public const string Package = "rimliaison-toolchain-promotion/v2";
    public const string Result = "rimliaison-toolchain-promotion-result/v1";
    public const string OwnerProduct = "RimLiaison";
    public const string RuntimeProtocolContract = "devbridge-mod-development/v1";
    public const string RuntimeSubsystem = "RimLiaison.Runtime";
    public const string RimBridgeServerBoundary = "external-game-side";
}

public sealed class ToolchainPromotionPackage
{
    [JsonPropertyName("schemaVersion")]
    public string? SchemaVersion { get; init; }
    [JsonPropertyName("sourceCommit")]
    public string? SourceCommit { get; init; }
    [JsonPropertyName("qualificationArtifactPath")]
    public string? QualificationArtifactPath { get; init; }
    [JsonPropertyName("qualificationArtifactSha256")]
    public string? QualificationArtifactSha256 { get; init; }
    [JsonPropertyName("artifactRoot")]
    public string? ArtifactRoot { get; init; }
    [JsonPropertyName("rimLiaisonExecutableRelativePath")]
    public string? RimLiaisonExecutableRelativePath { get; init; }
    [JsonPropertyName("rimLiaisonAssemblyRelativePath")]
    public string? RimLiaisonAssemblyRelativePath { get; init; }
    [JsonPropertyName("rimLiaisonExecutableSha256")]
    public string? RimLiaisonExecutableSha256 { get; init; }
    [JsonPropertyName("rimLiaisonAssemblySha256")]
    public string? RimLiaisonAssemblySha256 { get; init; }
    [JsonPropertyName("ownerProduct")]
    public string? OwnerProduct { get; init; }
    [JsonPropertyName("runtimeSubsystem")]
    public string? RuntimeSubsystem { get; init; }
    [JsonPropertyName("devBridgeRuntimeRoot")]
    public string? DevBridgeRuntimeRoot { get; init; }
    [JsonPropertyName("devBridgeRuntimeArtifactRoot")]
    public string? DevBridgeRuntimeArtifactRoot { get; init; }
    [JsonPropertyName("devBridgePackageSha256")]
    public string? DevBridgePackageSha256 { get; init; }
    [JsonPropertyName("devBridgeCoordinatorSha256")]
    public string? DevBridgeCoordinatorSha256 { get; init; }
    [JsonPropertyName("transactionConsumerPath")]
    public string? TransactionConsumerPath { get; init; }
    [JsonPropertyName("transactionConsumerRelativePath")]
    public string? TransactionConsumerRelativePath { get; init; }
    [JsonPropertyName("transactionConsumerSha256")]
    public string? TransactionConsumerSha256 { get; init; }
    [JsonPropertyName("unifiedManifestRelativePath")]
    public string? UnifiedManifestRelativePath { get; init; }
    [JsonPropertyName("runtimeProtocolContract")]
    public string? RuntimeProtocolContract { get; init; }
}

public sealed record ToolchainPromotionResult(
    string SchemaVersion,
    string Status,
    string? ErrorCode,
    string? Error,
    string? SourceCommit,
    string? QualificationArtifactPath,
    string? QualificationArtifactSha256,
    IReadOnlyDictionary<string, string>? QualifiedHashes,
    IReadOnlyDictionary<string, string>? InstalledHashes,
    string? PromotedFingerprint,
    string? PreviousFingerprint,
    IReadOnlyList<string> ChangedComponents,
    string Verification,
    string ProductionDoctor,
    string CampaignConsequence,
    string? InstalledRoot,
    string? NextAction)
{
    [JsonPropertyName("promotionTransactionId")]
    public string? PromotionTransactionId { get; init; }

    [JsonPropertyName("reliabilityCampaignId")]
    public string? ReliabilityCampaignId { get; init; }

    [JsonPropertyName("reliabilityCampaignState")]
    public string? ReliabilityCampaignState { get; init; }

    [JsonPropertyName("candidateHealth")]
    public PromotionCandidateHealthEvidence? CandidateHealth { get; init; }

    [JsonPropertyName("previousProductionHealth")]
    public string? PreviousProductionHealth { get; init; }

    [JsonPropertyName("activeManifestChanged")]
    public bool ActiveManifestChanged { get; init; }

    [JsonPropertyName("rollbackOccurred")]
    public bool RollbackOccurred { get; init; }

    public static ToolchainPromotionResult Blocked(
        string code,
        string error,
        string? sourceCommit = null,
        string? qualificationArtifactPath = null,
        string? qualificationArtifactSha256 = null,
        string? previousFingerprint = null,
        string? nextAction = null,
        string? productionDoctor = null) => new(
        ToolchainPromotionSchemas.Result,
        "blocked",
        code,
        error,
        sourceCommit,
        qualificationArtifactPath,
        qualificationArtifactSha256,
        null,
        null,
        null,
        previousFingerprint,
        [],
        "not-verified",
        productionDoctor ?? "not-run",
        "unchanged",
        null,
        nextAction);
}

public sealed record PromotionCandidateHealthBinding(
    string CandidateExecutable,
    string CandidateRuntimeRoot,
    string CandidateFingerprint,
    string CandidateSourceCommit,
    string DevBridgePackageSha256,
    string DevBridgeCoordinatorSha256,
    string TransactionConsumerSha256,
    string RuntimeProtocolContract,
    string RimWorldExecutable);

public sealed record PromotionCandidateHealthEvidence(
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("candidateFingerprint")] string CandidateFingerprint,
    [property: JsonPropertyName("candidateRuntimeRoot")] string CandidateRuntimeRoot,
    [property: JsonPropertyName("candidateExecutable")] string CandidateExecutable,
    [property: JsonPropertyName("candidateSourceCommit")] string CandidateSourceCommit,
    [property: JsonPropertyName("devBridgePackageSha256")] string DevBridgePackageSha256,
    [property: JsonPropertyName("devBridgeCoordinatorSha256")] string DevBridgeCoordinatorSha256,
    [property: JsonPropertyName("transactionConsumerSha256")] string TransactionConsumerSha256,
    [property: JsonPropertyName("runtimeProtocolContract")] string RuntimeProtocolContract,
    [property: JsonPropertyName("error")] string? Error = null,
    [property: JsonPropertyName("nestedError")] string? NestedError = null,
    [property: JsonPropertyName("previousProductionHealth")] string? PreviousProductionHealth = null,
    [property: JsonPropertyName("rollbackOccurred")] bool RollbackOccurred = false,
    [property: JsonPropertyName("activeManifestChanged")] bool ActiveManifestChanged = false);

public sealed record PromotionCandidateHealthResult(
    bool Passed,
    string Summary,
    string? Error,
    PromotionCandidateHealthEvidence Evidence);

public interface IPromotionCandidateHealthVerifier
{
    Task<PromotionCandidateHealthResult> VerifyAsync(
        PromotionCandidateHealthBinding binding,
        string workflowId,
        CancellationToken cancellationToken);
}

public static class ToolchainPromotionService
{
    private const string ProductionManifestEnvironment = "RIMLIAISON_PRODUCTION_TOOLCHAIN_MANIFEST";
    private const string DefaultProductionManifest = "C:/RimDev/.rimdev/production-toolchain.json";
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };
    public static string WriteQualifiedPromotionPackage(
        QualificationAggregate qualification,
        string qualificationArtifactPath,
        string packagePath,
        ToolchainCandidate candidate)
    {
        if (!qualification.QualificationPassed ||
            !qualification.CandidateComplete ||
            string.IsNullOrWhiteSpace(qualification.SourceCommit) ||
            qualification.QualifiedArtifactHashes is null ||
            !string.Equals(qualification.SourceCommit, candidate.SourceCommit, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "A complete qualification PASS bound to an immutable candidate is required.");
        }
        if (!File.Exists(qualificationArtifactPath))
        {
            throw new FileNotFoundException(
                "The immutable qualification artifact is missing.",
                qualificationArtifactPath);
        }

        string executableHash = candidate.RimLiaisonExecutableSha256;
        string assemblyHash = candidate.RimLiaisonAssemblySha256;
        if (!qualification.QualifiedArtifactHashes.TryGetValue(
                "rimLiaisonExecutableSha256",
                out string? qualifiedExecutableHash) ||
            !qualification.QualifiedArtifactHashes.TryGetValue(
                "rimLiaisonAssemblySha256",
                out string? qualifiedAssemblyHash) ||
            !string.Equals(executableHash, qualifiedExecutableHash, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(assemblyHash, qualifiedAssemblyHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The immutable candidate artifacts do not equal the artifacts captured by qualification.");
        }
        if (!HashMatches(candidate.RimLiaisonExecutablePath, executableHash) ||
            !HashMatches(candidate.RimLiaisonAssemblyPath, assemblyHash) ||
            !HashMatches(candidate.TransactionConsumerPath, candidate.TransactionConsumerSha256))
        {
            throw new InvalidDataException(
                "The immutable candidate file contents do not match their bound hashes.");
        }
        if (!File.Exists(candidate.RimLiaisonExecutablePath) ||
            !File.Exists(candidate.RimLiaisonAssemblyPath) ||
            !File.Exists(candidate.TransactionConsumerPath) ||
            !Directory.Exists(candidate.DevBridgeRuntimeArtifactRoot))
        {
            throw new InvalidDataException("The immutable candidate is incomplete.");
        }

        string fullPackagePath = Path.GetFullPath(packagePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPackagePath)!);
        if (File.Exists(fullPackagePath))
            throw new IOException("The qualified promotion package already exists and is immutable.");

        string payloadRoot = Path.Combine(
            Path.GetDirectoryName(fullPackagePath)!,
            "qualified-toolchain-payload-" + candidate.SourceCommit);
        Directory.CreateDirectory(payloadRoot);
        string payloadExecutable = Path.Combine(payloadRoot, "rimliaison.exe");
        string payloadAssembly = Path.Combine(payloadRoot, "rimliaison.dll");
        string payloadQualification = Path.Combine(payloadRoot, "qualification.json");
        string payloadConsumer = Path.Combine(payloadRoot, "transaction-components", "mod-test.ps1");
        CopyImmutableFile(candidate.RimLiaisonExecutablePath, payloadExecutable);
        CopyImmutableFile(candidate.RimLiaisonAssemblyPath, payloadAssembly);
        CopyImmutableFile(qualificationArtifactPath, payloadQualification);
        CopyImmutableFile(candidate.TransactionConsumerPath, payloadConsumer);
        foreach (string companionName in new[] { "rimliaison.deps.json", "rimliaison.runtimeconfig.json" })
        {
            string sourceCompanion = Path.Combine(candidate.CandidateRoot, companionName);
            if (File.Exists(sourceCompanion))
                CopyImmutableFile(sourceCompanion, Path.Combine(payloadRoot, companionName));
        }

        string runtimeArtifactRoot = Path.Combine(payloadRoot, "runtime");
        CopyImmutableDirectory(candidate.DevBridgeRuntimeArtifactRoot, runtimeArtifactRoot);
        if (!RuntimeSnapshotIsVerified(
                runtimeArtifactRoot,
                candidate.DevBridgePackageSha256,
                candidate.DevBridgeCoordinatorSha256))
        {
            throw new InvalidDataException("The immutable candidate runtime snapshot does not match its bound hashes.");
        }

        string unifiedManifestPath = Path.Combine(payloadRoot, "unified-package.json");
        string promotedFingerprint = ComputePromotedFingerprint(
            candidate.SourceCommit,
            executableHash,
            assemblyHash,
            candidate.DevBridgeCoordinatorSha256,
            candidate.DevBridgePackageSha256,
            candidate.TransactionConsumerSha256,
            candidate.RuntimeProtocolContract,
            ToolchainPromotionSchemas.OwnerProduct,
            ToolchainPromotionSchemas.RuntimeSubsystem);
        var unifiedManifest = new
        {
            schemaVersion = "rimliaison-unified-production-package/v2",
            productFingerprint = promotedFingerprint,
            ownerProduct = ToolchainPromotionSchemas.OwnerProduct,
            runtimeSubsystem = ToolchainPromotionSchemas.RuntimeSubsystem,
            rimBridgeServer = new
            {
                boundary = ToolchainPromotionSchemas.RimBridgeServerBoundary,
                ownership = "RimBridgeServer"
            },
            sourceCommit = candidate.SourceCommit,
            rimLiaison = new
            {
                executablePath = "rimliaison.exe",
                executableSha256 = executableHash,
                assemblyPath = "rimliaison.dll",
                assemblySha256 = assemblyHash
            },
            runtime = new
            {
                packageSha256 = candidate.DevBridgePackageSha256,
                coordinatorSha256 = candidate.DevBridgeCoordinatorSha256
            },
            transactionConsumer = new
            {
                path = "transaction-components/mod-test.ps1",
                sha256 = candidate.TransactionConsumerSha256
            }
        };
        string unifiedJson = JsonSerializer.Serialize(unifiedManifest, WriteOptions);
        if (File.Exists(unifiedManifestPath))
        {
            if (!string.Equals(File.ReadAllText(unifiedManifestPath), unifiedJson, StringComparison.Ordinal))
                throw new InvalidDataException("The immutable candidate unified manifest was substituted.");
        }
        else
        {
            File.WriteAllText(unifiedManifestPath, unifiedJson);
        }

        ToolchainPromotionPackage package = new()
        {
            SchemaVersion = ToolchainPromotionSchemas.Package,
            OwnerProduct = ToolchainPromotionSchemas.OwnerProduct,
            RuntimeSubsystem = ToolchainPromotionSchemas.RuntimeSubsystem,
            SourceCommit = candidate.SourceCommit,
            QualificationArtifactPath = payloadQualification,
            QualificationArtifactSha256 = ToolchainFileHash.Sha256(payloadQualification),
            ArtifactRoot = payloadRoot,
            RimLiaisonExecutableRelativePath = "rimliaison.exe",
            RimLiaisonAssemblyRelativePath = "rimliaison.dll",
            RimLiaisonExecutableSha256 = executableHash,
            RimLiaisonAssemblySha256 = assemblyHash,
            DevBridgeRuntimeRoot = candidate.DevBridgeRuntimeRoot,
            DevBridgeRuntimeArtifactRoot = runtimeArtifactRoot,
            DevBridgePackageSha256 = candidate.DevBridgePackageSha256,
            DevBridgeCoordinatorSha256 = candidate.DevBridgeCoordinatorSha256,
            TransactionConsumerPath = payloadConsumer,
            TransactionConsumerRelativePath = "transaction-components/mod-test.ps1",
            TransactionConsumerSha256 = candidate.TransactionConsumerSha256,
            UnifiedManifestRelativePath = "unified-package.json",
            RuntimeProtocolContract = candidate.RuntimeProtocolContract
        };
        using FileStream stream = new(
            fullPackagePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read);
        JsonSerializer.Serialize(stream, package, WriteOptions);
        return fullPackagePath;
    }


    private static bool DurableRecoveryPayloadIsVerified(
        ToolchainPromotionPackage package,
        out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(package.DevBridgeRuntimeArtifactRoot) ||
            !Directory.Exists(package.DevBridgeRuntimeArtifactRoot))
        {
            error = "The promotion package does not contain a durable DevBridge runtime payload.";
            return false;
        }

        string artifactRoot = Path.GetFullPath(package.ArtifactRoot ?? string.Empty);
        string? executable = SafeArtifactPath(artifactRoot, package.RimLiaisonExecutableRelativePath);
        string? assembly = SafeArtifactPath(artifactRoot, package.RimLiaisonAssemblyRelativePath);
        string? consumer = SafeArtifactPath(artifactRoot, package.TransactionConsumerRelativePath);
        string? unified = SafeArtifactPath(artifactRoot, package.UnifiedManifestRelativePath);
        if (executable is null || assembly is null || consumer is null || unified is null ||
            !HashMatches(executable, package.RimLiaisonExecutableSha256) ||
            !HashMatches(assembly, package.RimLiaisonAssemblySha256) ||
            !HashMatches(consumer, package.TransactionConsumerSha256) ||
            !File.Exists(unified))
        {
            error = "The promotion package durable RimLiaison payload is incomplete or has mismatching hashes.";
            return false;
        }

        string qualification = Path.GetFullPath(package.QualificationArtifactPath!);
        if (!File.Exists(qualification) ||
            !string.Equals(
                ToolchainFileHash.Sha256(qualification),
                package.QualificationArtifactSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            error = "The promotion package durable qualification proof is missing or has a mismatching hash.";
            return false;
        }

        if (!RuntimeSnapshotIsVerified(
                package.DevBridgeRuntimeArtifactRoot,
                package.DevBridgePackageSha256,
                package.DevBridgeCoordinatorSha256))
        {
            error = "The promotion package durable DevBridge runtime payload is missing or has mismatching hashes.";
            return false;
        }
        return true;
    }

    private static bool RuntimeSnapshotIsVerified(
        string runtimeRoot,
        string? expectedPackageHash,
        string? expectedCoordinatorHash)
    {
        string manifestPath = Path.Combine(runtimeRoot, ".devbridge-runtime-manifest.json");
        if (!Directory.Exists(runtimeRoot) ||
            !File.Exists(manifestPath) ||
            !File.Exists(Path.Combine(runtimeRoot, "DevBridge.cmd")))
        {
            return false;
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!document.RootElement.TryGetProperty("files", out JsonElement files) ||
                files.ValueKind != JsonValueKind.Array)
                return false;
            var entries = new List<(string Path, string Sha256)>();
            foreach (JsonElement entry in files.EnumerateArray())
            {
                if (!TryString(entry, "path", out string? relative) ||
                    !TryString(entry, "sha256", out string? expected))
                    return false;
                string? path = SafeArtifactPath(runtimeRoot, relative);
                if (path is null || !File.Exists(path) ||
                    !string.Equals(ToolchainFileHash.Sha256(path), expected, StringComparison.OrdinalIgnoreCase))
                    return false;
                entries.Add((relative!.Replace('\\', '/'), expected!));
            }
            return string.Equals(
                    ReadRuntimePackageHash(manifestPath),
                    expectedPackageHash,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    entries.FirstOrDefault(entry =>
                        string.Equals(entry.Path, "Coordinator/DevBridge.Coordinator.exe", StringComparison.OrdinalIgnoreCase)).Sha256,
                    expectedCoordinatorHash,
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }
    private static bool HashMatches(string path, string? expected) =>
        File.Exists(path) &&
        !string.IsNullOrWhiteSpace(expected) &&
        string.Equals(ToolchainFileHash.Sha256(path), expected, StringComparison.OrdinalIgnoreCase);

    public static async Task<ToolchainPromotionResult> PromoteAsync(
        string sourceRoot,
        string? packagePath,
        string? qualificationArtifactPath,
        CancellationToken cancellationToken = default,
        string? workflowId = null,
        IPromotionLeaseOrchestrator? promotionLeaseOrchestrator = null,
        IPromotionCandidateHealthVerifier? promotionHealthVerifier = null,
        IPromotionCanonicalHealthVerifier? canonicalHealthVerifier = null,
        IGitRepositoryStateProvider? gitRepositoryStateProvider = null)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            return ToolchainPromotionResult.Blocked(
                "PROMOTION_PACKAGE_MISSING",
                "A qualified toolchain promotion package is required.",
                nextAction: "Build a qualified promotion package and retry rimliaison qualification promote --json.");
        }
        string manifestPath = Environment.GetEnvironmentVariable(ProductionManifestEnvironment) ??
            DefaultProductionManifest;
        string lockPath = manifestPath + ".promotion.lock";
        FileStream? promotionLock = null;
        string? stagedRoot = null;
        string? stagedRuntimeRoot = null;
        string? runtimeBackupRoot = null;
        ProductionToolchainManifest? previous = null;
        ToolchainPromotionPackage? package = null;
        bool runtimeCommitted = false;
        bool promotionCommitted = false;
        try
        {
            promotionLock = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            string promotionTransactionId = "promotion-" + Guid.NewGuid().ToString("N");
            package = ReadPackage(packagePath, out string? packageError);
            if (package is null)
            {
                return ToolchainPromotionResult.Blocked("PROMOTION_PACKAGE_INVALID", packageError ?? "The promotion package is invalid.");
            }
            string sourceCommit = package.SourceCommit!;

            string artifactPath = Path.GetFullPath(package.QualificationArtifactPath ?? qualificationArtifactPath ?? string.Empty);
            if (!File.Exists(artifactPath))
            {
                return ToolchainPromotionResult.Blocked(
                    "PROMOTION_QUALIFICATION_MISSING",
                    "The qualification artifact referenced by the promotion package is missing.",
                    package.SourceCommit,
                    artifactPath,
                    package.QualificationArtifactSha256,
                    nextAction: "Run the complete qualification profile and rebuild the promotion package.");
            }

            string qualificationHash = ToolchainFileHash.Sha256(artifactPath);
            if (!string.Equals(qualificationHash, package.QualificationArtifactSha256, StringComparison.OrdinalIgnoreCase))
            {
                return ToolchainPromotionResult.Blocked(
                    "PROMOTION_QUALIFICATION_HASH_MISMATCH",
                    "The qualification artifact hash differs from the package declaration.",
                    package.SourceCommit,
                    artifactPath,
                    qualificationHash,
                    nextAction: "Rebuild the promotion package from the exact qualification artifact.");
            }

            using JsonDocument qualification = JsonDocument.Parse(File.ReadAllText(artifactPath));
            if (!PromotionProofIsComplete(qualification.RootElement, package.SourceCommit, out string? proofError))
            {
                return ToolchainPromotionResult.Blocked(
                    "PROMOTION_QUALIFICATION_NOT_PROVEN",
                    proofError ?? "The qualification artifact is not a complete PASS proof.",
                    package.SourceCommit,
                    artifactPath,
                    qualificationHash,
                    nextAction: "Run the complete burn-in profile for the current source commit.");
            }
            if (!QualifiedHashesMatch(qualification.RootElement, package, out string? artifactHashError))
            {
                return ToolchainPromotionResult.Blocked(
                    "PROMOTION_QUALIFIED_ARTIFACT_MISMATCH",
                    artifactHashError ?? "The promotion artifacts are not the artifacts qualified by the burn-in.",
                    package.SourceCommit,
                    artifactPath,
                    qualificationHash,
                    nextAction: "Publish the exact qualified Release artifacts and rebuild the promotion package.");
            }
            if (!DurableRecoveryPayloadIsVerified(package, out string? recoveryPayloadError))
            {
                return ToolchainPromotionResult.Blocked(
                    "PROMOTION_RECOVERY_PAYLOAD_INVALID",
                    recoveryPayloadError ?? "The immutable promotion recovery payload is invalid.",
                    package.SourceCommit,
                    artifactPath,
                    qualificationHash);
            }

            IGitRepositoryStateProvider sourceStateProvider = gitRepositoryStateProvider ??
                new SystemGitRepositoryStateProvider();
            GitRepositoryStateResult source = await sourceStateProvider
                .ReadAsync(sourceRoot, cancellationToken)
                .ConfigureAwait(false);
            if (!source.Resolved || source.State?.HeadSha is null ||
                source.State.Dirty ||
                !string.Equals(source.State.HeadSha, package.SourceCommit, StringComparison.OrdinalIgnoreCase))
            {
                return ToolchainPromotionResult.Blocked(
                    source.State?.Dirty == true
                        ? "PROMOTION_SOURCE_DIRTY"
                        : "PROMOTION_SOURCE_FINGERPRINT_STALE",
                    source.State?.Dirty == true
                        ? "The current source checkout is dirty after qualification."
                        : "The current source HEAD does not match the qualified source commit.",
                    package.SourceCommit,
                    artifactPath,
                    qualificationHash,
                    nextAction: "Commit or restore the qualified source and rebuild the promotion package.");
            }

            previous = ReadProductionManifest(manifestPath, out string? manifestError);
            if (previous is null)
            {
                return ToolchainPromotionResult.Blocked(
                    "PROMOTION_PRODUCTION_MANIFEST_INVALID",
                    manifestError ?? "The current production manifest could not be read.");
            }
            string previousProductionHealth = DescribePreviousProductionHealth(previous);
            string previousOwnerProduct = previous.OwnerProduct ?? ToolchainPromotionSchemas.OwnerProduct;
            string previousRuntimeSubsystem = previous.RuntimeSubsystem ?? ToolchainPromotionSchemas.RuntimeSubsystem;
            string previousRuntimeProtocolContract = previous.RuntimeProtocolContract ??
                previous.LegacyCompatibilityContract ??
                ToolchainPromotionSchemas.RuntimeProtocolContract;

            if (!string.Equals(package.OwnerProduct, ToolchainPromotionSchemas.OwnerProduct, StringComparison.Ordinal) ||
                !string.Equals(package.RuntimeSubsystem, ToolchainPromotionSchemas.RuntimeSubsystem, StringComparison.Ordinal) ||
                !string.Equals(previousOwnerProduct, ToolchainPromotionSchemas.OwnerProduct, StringComparison.Ordinal) ||
                !string.Equals(previousRuntimeSubsystem, ToolchainPromotionSchemas.RuntimeSubsystem, StringComparison.Ordinal) ||
                !string.Equals(package.RuntimeProtocolContract, previousRuntimeProtocolContract, StringComparison.Ordinal) ||
                !SamePath(package.DevBridgeRuntimeRoot, previous.DevBridgeRuntimeRoot))
            {
                return ToolchainPromotionResult.Blocked(
                    "PROMOTION_RUNTIME_COMPATIBILITY_MISMATCH",
                    "The qualified package does not match the installed DevBridge runtime contract.",
                    package.SourceCommit,
                    artifactPath,
                    qualificationHash,
                    previous.PromotedFingerprint,
                    "Run the pinned cross-stack compatibility gate and rebuild the unified promotion package.");
            }

            string artifactRoot = Path.GetFullPath(package.ArtifactRoot ?? string.Empty);
            string? executableCandidate = SafeArtifactPath(artifactRoot, package.RimLiaisonExecutableRelativePath);
            string? assemblyCandidate = SafeArtifactPath(artifactRoot, package.RimLiaisonAssemblyRelativePath);
            string? consumerSource = string.IsNullOrWhiteSpace(package.TransactionConsumerPath)
                ? null
                : Path.GetFullPath(package.TransactionConsumerPath);
            if (executableCandidate is null || assemblyCandidate is null ||
                consumerSource is null || !File.Exists(executableCandidate) ||
                !File.Exists(assemblyCandidate) || !File.Exists(consumerSource))
            {
                return ToolchainPromotionResult.Blocked(
                    "PROMOTION_ARTIFACT_MISSING",
                    "A unified production input is missing from the promotion package.",
                    package.SourceCommit,
                    artifactPath,
                    qualificationHash,
                    previous.PromotedFingerprint);
            }

            string executableSource = executableCandidate;
            string assemblySource = assemblyCandidate;
            string executableHash = ToolchainFileHash.Sha256(executableSource);
            string assemblyHash = ToolchainFileHash.Sha256(assemblySource);
            string consumerHash = ToolchainFileHash.Sha256(consumerSource);
            if (!string.Equals(executableHash, package.RimLiaisonExecutableSha256, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(assemblyHash, package.RimLiaisonAssemblySha256, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(consumerHash, package.TransactionConsumerSha256, StringComparison.OrdinalIgnoreCase))
            {
                return ToolchainPromotionResult.Blocked(
                    "PROMOTION_ARTIFACT_HASH_MISMATCH",
                    $"A unified production input hash differs from the package declaration (executable={executableHash}/{package.RimLiaisonExecutableSha256}, assembly={assemblyHash}/{package.RimLiaisonAssemblySha256}, transactionConsumer={consumerHash}/{package.TransactionConsumerSha256}).",
                    package.SourceCommit,
                    artifactPath,
                    qualificationHash,
                    previous.PromotedFingerprint,
                    "Rebuild the unified promotion package from the qualified artifacts.");
            }

            string coordinatorHash = package.DevBridgeCoordinatorSha256!;

            string productionCliDirectory = Path.GetDirectoryName(Path.GetFullPath(previous.RimLiaisonExecutablePath!))!;
            string productionParent = Directory.GetParent(productionCliDirectory)!.FullName;
            string promotedRootName = "cli-promoted-" + sourceCommit[..Math.Min(12, sourceCommit.Length)];
            stagedRoot = Path.Combine(productionParent, promotedRootName);
            string runtimeParent = Directory.GetParent(previous.DevBridgeRuntimeRoot!)?.FullName ??
                throw new InvalidDataException("The production runtime destination has no parent directory.");
            string runtimePromotedRootName = "runtime-promoted-" + sourceCommit[..Math.Min(12, sourceCommit.Length)];
            stagedRuntimeRoot = Path.Combine(runtimeParent, runtimePromotedRootName);
            if (Directory.Exists(stagedRoot) || Directory.Exists(stagedRuntimeRoot))
            {
                return ToolchainPromotionResult.Blocked(
                    "PROMOTION_DESTINATION_EXISTS",
                    "The bounded promotion destination already exists; refusing to reuse it.",
                    package.SourceCommit,
                    artifactPath,
                    qualificationHash,
                    previous.PromotedFingerprint);
            }
            CopyDirectory(package.DevBridgeRuntimeArtifactRoot!, stagedRuntimeRoot);
            // Runtime state is mutable execution residue from qualification, not release content.
            // Do not carry its process identity, slot, or generation into the production target.
            TryDelete(Path.Combine(stagedRuntimeRoot, "Runtime"));


            CopyDirectory(artifactRoot, stagedRoot);
            string installedExecutable = Path.Combine(
                stagedRoot,
                Path.GetRelativePath(artifactRoot, executableSource));
            string installedAssembly = Path.Combine(
                stagedRoot,
                Path.GetRelativePath(artifactRoot, assemblySource));
            string? consumerRelativePath = SafeArtifactPath(
                stagedRoot,
                package.TransactionConsumerRelativePath);
            string? unifiedManifestPath = SafeArtifactPath(
                stagedRoot,
                package.UnifiedManifestRelativePath);
            if (consumerRelativePath is null || unifiedManifestPath is null)
            {
                return ToolchainPromotionResult.Blocked(
                    "PROMOTION_PACKAGE_PATH_INVALID",
                    "Unified package component paths must remain relative to the staged package.",
                    package.SourceCommit,
                    artifactPath,
                    qualificationHash,
                    previous.PromotedFingerprint);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(consumerRelativePath)!);
            if (File.Exists(consumerRelativePath))
            {
                if (!string.Equals(
                        ToolchainFileHash.Sha256(consumerRelativePath),
                        consumerHash,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return ToolchainPromotionResult.Blocked(
                        "PROMOTION_INSTALL_HASH_MISMATCH",
                        "The staged transaction consumer does not match the qualified hash.",
                        package.SourceCommit,
                        artifactPath,
                        qualificationHash,
                        previous.PromotedFingerprint);
                }
            }
            else
            {
                File.Copy(consumerSource, consumerRelativePath);
            }

            string installedExecutableHash = ToolchainFileHash.Sha256(installedExecutable);
            string installedAssemblyHash = ToolchainFileHash.Sha256(installedAssembly);
            string installedConsumerHash = ToolchainFileHash.Sha256(consumerRelativePath);
            if (!string.Equals(installedExecutableHash, executableHash, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(installedAssemblyHash, assemblyHash, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(installedConsumerHash, consumerHash, StringComparison.OrdinalIgnoreCase))
            {
                return ToolchainPromotionResult.Blocked(
                    "PROMOTION_INSTALL_HASH_MISMATCH",
                    "Installed staged artifacts do not match the unified qualified hashes.",
                    package.SourceCommit,
                    artifactPath,
                    qualificationHash,
                    previous.PromotedFingerprint);
            }

            string promotedFingerprint = ComputePromotedFingerprint(
                sourceCommit,
                executableHash,
                assemblyHash,
                coordinatorHash,
                package.DevBridgePackageSha256!,
                consumerHash,
                previous.RuntimeProtocolContract!,
                ToolchainPromotionSchemas.OwnerProduct,
                ToolchainPromotionSchemas.RuntimeSubsystem);
            var unifiedManifest = new
            {
                schemaVersion = "rimliaison-unified-production-package/v2",
                productFingerprint = promotedFingerprint,
                ownerProduct = ToolchainPromotionSchemas.OwnerProduct,
                runtimeSubsystem = ToolchainPromotionSchemas.RuntimeSubsystem,
                rimBridgeServer = new
                {
                    boundary = ToolchainPromotionSchemas.RimBridgeServerBoundary,
                    ownership = "RimBridgeServer"
                },
                sourceCommit,
                rimLiaison = new
                {
                    executablePath = Path.GetRelativePath(stagedRoot, installedExecutable),
                    executableSha256 = installedExecutableHash,
                    assemblyPath = Path.GetRelativePath(stagedRoot, installedAssembly),
                    assemblySha256 = installedAssemblyHash
                },
                runtime = new
                {
                    packageSha256 = package.DevBridgePackageSha256,
                    coordinatorSha256 = coordinatorHash
                },
                transactionConsumer = new
                {
                    path = Path.GetRelativePath(stagedRoot, consumerRelativePath),
                    sha256 = installedConsumerHash
                }
            };
            File.WriteAllText(unifiedManifestPath, JsonSerializer.Serialize(unifiedManifest, WriteOptions));
            string unifiedManifestHash = ToolchainFileHash.Sha256(unifiedManifestPath);
            string candidateRimWorldExecutable = ResolveCandidateRimWorldExecutable(sourceRoot) ?? string.Empty;
            var healthBinding = new PromotionCandidateHealthBinding(
                installedExecutable,
                stagedRuntimeRoot!,
                promotedFingerprint,
                sourceCommit,
                package.DevBridgePackageSha256!,
                coordinatorHash,
                consumerHash,
                previousRuntimeProtocolContract,
                candidateRimWorldExecutable);
            PromotionCandidateHealthResult health = promotionHealthVerifier is null
                ? await RunCandidateHealthAsync(
                        healthBinding,
                        workflowId ?? "rimliaison-promotion-" + sourceCommit[..Math.Min(12, sourceCommit.Length)],
                        cancellationToken,
                        promotionLeaseOrchestrator)
                    .ConfigureAwait(false)
                : await promotionHealthVerifier.VerifyAsync(
                        healthBinding,
                        workflowId ?? "rimliaison-promotion-" + sourceCommit[..Math.Min(12, sourceCommit.Length)],
                        cancellationToken)
                    .ConfigureAwait(false);
            if (health.Passed &&
                !CandidateHealthEvidenceMatches(health.Evidence, healthBinding, out string? evidenceError))
            {
                health = new(
                    false,
                    health.Summary,
                    evidenceError,
                    health.Evidence with
                    {
                        Status = "failed",
                        Error = evidenceError,
                        NestedError = health.Error
                    });
            }
            if (!health.Passed)
            {
                WriteFailureHandoff(
                    packagePath,
                    package,
                    previous,
                    "PROMOTION_CANDIDATE_HEALTH_FAILED",
                    health.Error ?? "The candidate health checks did not pass.",
                    "Repair the candidate package or runtime, then retry the supported promotion command.");
                return ToolchainPromotionResult.Blocked(
                    "PROMOTION_CANDIDATE_HEALTH_FAILED",
                    health.Error ?? "The candidate health checks did not pass.",
                    package.SourceCommit,
                    artifactPath,
                    qualificationHash,
                    previous.PromotedFingerprint,
                    "Repair the candidate package or runtime, then retry the supported promotion command.",
                    health.Summary) with
                {
                    CandidateHealth = health.Evidence with
                    {
                        PreviousProductionHealth = previousProductionHealth,
                        RollbackOccurred = false,
                        ActiveManifestChanged = false
                    },
                    PreviousProductionHealth = previousProductionHealth,
                    ActiveManifestChanged = false,
                    RollbackOccurred = false
                };
            }

            CommitRuntimeDirectory(
                stagedRuntimeRoot!,
                previous.DevBridgeRuntimeRoot!,
                out runtimeBackupRoot);
            runtimeCommitted = true;

            var updated = new ProductionToolchainManifest
            {
                SchemaVersion = previous.SchemaVersion,
                OwnerProduct = ToolchainPromotionSchemas.OwnerProduct,
                RuntimeSubsystem = ToolchainPromotionSchemas.RuntimeSubsystem,
                PromotedFingerprint = promotedFingerprint,
                Fingerprint = promotedFingerprint,
                RimLiaisonExecutablePath = installedExecutable,
                RimLiaisonExecutableSha256 = installedExecutableHash,
                RimLiaisonAssemblyPath = installedAssembly,
                RimLiaisonAssemblySha256 = installedAssemblyHash,
                DevBridgeRuntimeRoot = previous.DevBridgeRuntimeRoot,
                DevBridgePackageSha256 = package.DevBridgePackageSha256,
                DevBridgeCoordinatorSha256 = coordinatorHash,
                TransactionConsumerPath = consumerRelativePath,
                TransactionConsumerSha256 = installedConsumerHash,
                UnifiedManifestPath = unifiedManifestPath,
                UnifiedManifestSha256 = unifiedManifestHash,
                QualifiedSourceCommit = package.SourceCommit,
                RuntimeProtocolContract = previousRuntimeProtocolContract,
                QualificationArtifactSha256 = qualificationHash,
                PromotionPackagePath = Path.GetFullPath(packagePath),
                PromotionPackageSha256 = ToolchainFileHash.Sha256(packagePath)
            };
            AtomicReplace(manifestPath, JsonSerializer.Serialize(updated, WriteOptions));

            ProductionToolchainBindingResolution installed = ProductionToolchainBindingResolver.Resolve(
                sourceRoot,
                currentExecutablePath: installedExecutable);
            if (!installed.Succeeded)
            {
                TryRestoreProductionManifest(manifestPath, previous);
                WriteFailureHandoff(
                    packagePath,
                    package,
                    previous,
                    "PROMOTION_INSTALLED_IDENTITY_UNVERIFIED",
                    installed.Failure?.Error ?? "The installed production identity could not be verified.",
                    "Repair the unified package manifest and retry the supported promotion command.");
                return ToolchainPromotionResult.Blocked(
                    "PROMOTION_INSTALLED_IDENTITY_UNVERIFIED",
                    installed.Failure?.Error ?? "The installed production identity could not be verified.",
                    package.SourceCommit,
                    artifactPath,
                    qualificationHash,
                    previous.PromotedFingerprint) with
                {
                    CandidateHealth = health.Evidence with
                    {
                        PreviousProductionHealth = previousProductionHealth,
                        RollbackOccurred = true,
                        ActiveManifestChanged = true
                    },
                    PreviousProductionHealth = previousProductionHealth,
                    ActiveManifestChanged = true,
                    RollbackOccurred = true
                };
            }
            PromotionCanonicalHealthResult postCommitHealth = canonicalHealthVerifier is null
                ? await RunCanonicalProductionHealthAsync(
                        sourceRoot,
                        installedExecutable,
                        promotedFingerprint,
                        cancellationToken)
                    .ConfigureAwait(false)
                : await canonicalHealthVerifier.VerifyAsync(
                        sourceRoot,
                        installedExecutable,
                        promotedFingerprint,
                        cancellationToken)
                    .ConfigureAwait(false);
            if (!postCommitHealth.Passed)
            {
                TryRestoreProductionManifest(manifestPath, previous);
                WriteFailureHandoff(
                    packagePath,
                    package,
                    previous,
                    "PROMOTION_POST_COMMIT_HEALTH_FAILED",
                    postCommitHealth.Error ?? "Canonical post-commit doctor did not report READY.",
                    "Restore the prior production identity or retry the qualified promotion transaction.");
                return ToolchainPromotionResult.Blocked(
                    "PROMOTION_POST_COMMIT_HEALTH_FAILED",
                    postCommitHealth.Error ?? "Canonical post-commit doctor did not report READY.",
                    package.SourceCommit,
                    artifactPath,
                    qualificationHash,
                    previous.PromotedFingerprint,
                    "Restore the prior production identity or retry the qualified promotion transaction.",
                    postCommitHealth.Summary) with
                {
                    CandidateHealth = health.Evidence with
                    {
                        PreviousProductionHealth = previousProductionHealth,
                        RollbackOccurred = true,
                        ActiveManifestChanged = true
                    },
                    PreviousProductionHealth = previousProductionHealth,
                    ActiveManifestChanged = true,
                    RollbackOccurred = true
                };
            }

            string doctor = postCommitHealth.Summary;

            if (runtimeBackupRoot is not null) TryDelete(runtimeBackupRoot);
            runtimeBackupRoot = null;
            runtimeCommitted = false;
            promotionCommitted = true;
            return new(
                ToolchainPromotionSchemas.Result,
                "promoted",
                null,
                null,
                package.SourceCommit,
                artifactPath,
                qualificationHash,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["rimLiaisonExecutableSha256"] = executableHash,
                    ["rimLiaisonAssemblySha256"] = assemblyHash,
                    ["devBridgePackageSha256"] = package.DevBridgePackageSha256!,
                    ["devBridgeCoordinatorSha256"] = coordinatorHash,
                    ["transactionConsumerSha256"] = consumerHash
                },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["rimLiaisonExecutableSha256"] = installedExecutableHash,
                    ["rimLiaisonAssemblySha256"] = installedAssemblyHash,
                    ["devBridgePackageSha256"] = package.DevBridgePackageSha256!,
                    ["devBridgeCoordinatorSha256"] = coordinatorHash,
                    ["transactionConsumerSha256"] = installedConsumerHash
                },
                promotedFingerprint,
                previous.PromotedFingerprint,
                ["RimLiaison"],
                "verified",
                doctor,
                "start-new-reliability-campaign-collecting",
                stagedRoot,
                null)
            {
                PromotionTransactionId = promotionTransactionId,
                CandidateHealth = health.Evidence with
                {
                    PreviousProductionHealth = previousProductionHealth,
                    RollbackOccurred = false,
                    ActiveManifestChanged = true
                },
                PreviousProductionHealth = previousProductionHealth,
                ActiveManifestChanged = true,
                RollbackOccurred = false
            };
        }
        catch (IOException exception) when (
            promotionLock is null &&
            (exception.HResult == unchecked((int)0x800700B7) ||
             exception.HResult == unchecked((int)0x80070050) ||
             File.Exists(lockPath)))
        {
            return ToolchainPromotionResult.Blocked(
                "PROMOTION_LOCKED",
                "Another production promotion is already in progress.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or OperationCanceledException)
        {
            TryRestoreProductionManifest(manifestPath, previous);
            WriteFailureHandoff(
                packagePath,
                package,
                previous,
                "PROMOTION_TRANSACTION_FAILED",
                exception.Message,
                "Repair the unified package inputs or production control plane, then retry rimliaison qualification promote --json.");
            return ToolchainPromotionResult.Blocked("PROMOTION_TRANSACTION_FAILED", exception.Message);
        }
        finally
        {
            promotionLock?.Dispose();
            if (!promotionCommitted && runtimeCommitted && previous?.DevBridgeRuntimeRoot is not null)
            {
                RestoreRuntimeDirectory(previous.DevBridgeRuntimeRoot, runtimeBackupRoot);
            }
            else if (!promotionCommitted && stagedRuntimeRoot is not null)
            {
                TryDelete(stagedRuntimeRoot);
            }
            if (!promotionCommitted && stagedRoot is not null)
            {
                TryDelete(stagedRoot);
            }
            if (promotionLock is not null)
                TryDelete(lockPath);
        }

    }
    private static void CommitRuntimeDirectory(
        string stagedRoot,
        string targetRoot,
        out string? backupRoot)
    {
        backupRoot = null;
        string target = Path.GetFullPath(targetRoot);
        string stage = Path.GetFullPath(stagedRoot);
        if (Directory.Exists(target))
        {
            backupRoot = target + ".backup-" + Guid.NewGuid().ToString("N");
            Directory.Move(target, backupRoot);
        }
        try
        {
            Directory.Move(stage, target);
        }
        catch
        {
            if (backupRoot is not null && Directory.Exists(backupRoot) && !Directory.Exists(target))
                Directory.Move(backupRoot, target);
            backupRoot = null;
            throw;
        }
    }

    private static void RestoreRuntimeDirectory(string targetRoot, string? backupRoot)
    {
        string target = Path.GetFullPath(targetRoot);
        TryDelete(target);
        if (!string.IsNullOrWhiteSpace(backupRoot) && Directory.Exists(backupRoot))
            Directory.Move(backupRoot, target);
    }
    private static void TryRestoreProductionManifest(
        string manifestPath,
        ProductionToolchainManifest? previous)
    {
        if (previous is null)
        {
            return;
        }
        try
        {
            AtomicReplace(manifestPath, JsonSerializer.Serialize(previous, WriteOptions));
        }
        catch (Exception) when (File.Exists(manifestPath))
        {
        }
    }
    internal static bool TryReadPromotionDestination(
        string manifestPath,
        out string? runtimeRoot,
        out string runtimeProtocolContract,
        out string? error)
    {
        runtimeRoot = null;
        runtimeProtocolContract = ToolchainPromotionSchemas.RuntimeProtocolContract;
        ProductionToolchainManifest? manifest = ReadProductionManifest(manifestPath, out error);
        if (manifest is null || string.IsNullOrWhiteSpace(manifest.DevBridgeRuntimeRoot))
        {
            error ??= "The production manifest has no runtime destination.";
            return false;
        }
        runtimeRoot = Path.GetFullPath(manifest.DevBridgeRuntimeRoot);
        runtimeProtocolContract = manifest.RuntimeProtocolContract ??
            manifest.LegacyCompatibilityContract ??
            ToolchainPromotionSchemas.RuntimeProtocolContract;
        return true;
    }
    internal static ToolchainPromotionPackage? ReadPackage(string path, out string? error)
    {
        error = null;
        try
        {

            ToolchainPromotionPackage? package = JsonSerializer.Deserialize<ToolchainPromotionPackage>(
                File.ReadAllText(Path.GetFullPath(path)), ReadOptions);
            if (package is null ||
                package.SchemaVersion != ToolchainPromotionSchemas.Package ||
                string.IsNullOrWhiteSpace(package.SourceCommit) ||
                string.IsNullOrWhiteSpace(package.QualificationArtifactPath) ||
                string.IsNullOrWhiteSpace(package.QualificationArtifactSha256) ||
                string.IsNullOrWhiteSpace(package.ArtifactRoot) ||
                string.IsNullOrWhiteSpace(package.RimLiaisonExecutableRelativePath) ||
                string.IsNullOrWhiteSpace(package.RimLiaisonAssemblyRelativePath) ||
                string.IsNullOrWhiteSpace(package.RimLiaisonExecutableSha256) ||
                string.IsNullOrWhiteSpace(package.RimLiaisonAssemblySha256) ||
                !string.Equals(package.OwnerProduct, ToolchainPromotionSchemas.OwnerProduct, StringComparison.Ordinal) ||
                !string.Equals(package.RuntimeSubsystem, ToolchainPromotionSchemas.RuntimeSubsystem, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(package.DevBridgeRuntimeRoot) ||
                string.IsNullOrWhiteSpace(package.DevBridgePackageSha256) ||
                string.IsNullOrWhiteSpace(package.DevBridgeCoordinatorSha256) ||
                string.IsNullOrWhiteSpace(package.TransactionConsumerPath) ||
                string.IsNullOrWhiteSpace(package.TransactionConsumerRelativePath) ||
                string.IsNullOrWhiteSpace(package.TransactionConsumerSha256) ||
                string.IsNullOrWhiteSpace(package.UnifiedManifestRelativePath) ||
                string.IsNullOrWhiteSpace(package.RuntimeProtocolContract))
            {
                error = "The promotion package is incomplete or uses an unsupported schema.";
                return null;
            }
            return package;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            error = exception.Message;
            return null;
        }
    }

    private static ProductionToolchainManifest? ReadProductionManifest(string path, out string? error)
    {
        error = null;
        try
        {
            ProductionToolchainManifest? manifest = JsonSerializer.Deserialize<ProductionToolchainManifest>(
                File.ReadAllText(path), ReadOptions);
            if (manifest is null ||
                manifest.SchemaVersion != "rimliaison-production-toolchain/v1" ||
                string.IsNullOrWhiteSpace(manifest.PromotedFingerprint) ||
                string.IsNullOrWhiteSpace(manifest.RimLiaisonExecutablePath) ||
                string.IsNullOrWhiteSpace(manifest.RimLiaisonAssemblyPath) ||
                (!string.IsNullOrWhiteSpace(manifest.OwnerProduct) &&
                    !string.Equals(manifest.OwnerProduct, ToolchainPromotionSchemas.OwnerProduct, StringComparison.Ordinal)) ||
                (!string.IsNullOrWhiteSpace(manifest.RuntimeSubsystem) &&
                    !string.Equals(manifest.RuntimeSubsystem, ToolchainPromotionSchemas.RuntimeSubsystem, StringComparison.Ordinal)) ||
                string.IsNullOrWhiteSpace(manifest.DevBridgePackageSha256) ||
                string.IsNullOrWhiteSpace(manifest.TransactionConsumerPath) ||
                string.IsNullOrWhiteSpace(manifest.TransactionConsumerSha256) ||
                string.IsNullOrWhiteSpace(manifest.RuntimeProtocolContract) &&
                    !string.Equals(manifest.LegacyCompatibilityContract, ToolchainPromotionSchemas.RuntimeProtocolContract, StringComparison.Ordinal))
            {
                error = "The production manifest is incomplete or unsupported.";
                return null;
            }
            return manifest;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            error = exception.Message;
            return null;
        }
    }

    internal static bool PromotionProofIsComplete(JsonElement root, string? sourceCommit, out string? error)
    {
        error = null;
        if (!TryString(root, "SourceCommit", out string? artifactCommit) &&
            !TryString(root, "sourceCommit", out artifactCommit))
        {
            error = "The qualification artifact has no source commit provenance.";
            return false;
        }
        if (!string.Equals(artifactCommit, sourceCommit, StringComparison.OrdinalIgnoreCase) ||
            !TryInt(root, "Passes", out int passes) && !TryInt(root, "passes", out passes) ||
            !TryInt(root, "TotalRuns", out int total) && !TryInt(root, "totalRuns", out total) ||
            !TryInt(root, "InfrastructureFailures", out int infra) && !TryInt(root, "infrastructureFailures", out infra) ||
            !TryInt(root, "FixtureFailures", out int fixtures) && !TryInt(root, "fixtureFailures", out fixtures) ||
            passes != total || infra != 0 || fixtures != 0)
        {
            error = "The qualification artifact is stale or is not a complete PASS profile.";
            return false;
        }
        return true;
    }

    internal static bool QualifiedHashesMatch(
        JsonElement root,
        ToolchainPromotionPackage package,
        out string? error)
    {
        error = null;
        if (!TryObject(root, "QualifiedArtifactHashes", out JsonElement hashes) &&
            !TryObject(root, "qualifiedArtifactHashes", out hashes))
        {
            error = "The qualification artifact has no qualified artifact hashes.";
            return false;
        }
        if (!TryString(hashes, "rimLiaisonExecutableSha256", out string? executableHash) ||
            !TryString(hashes, "rimLiaisonAssemblySha256", out string? assemblyHash) ||
            !string.Equals(executableHash, package.RimLiaisonExecutableSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(assemblyHash, package.RimLiaisonAssemblySha256, StringComparison.OrdinalIgnoreCase))
        {
            error = "The promotion package artifact hashes do not match the qualification artifact.";
            return false;
        }
        if ((TryString(hashes, "devBridgePackageSha256", out string? packageHash) &&
             !string.Equals(packageHash, package.DevBridgePackageSha256, StringComparison.OrdinalIgnoreCase)) ||
            (TryString(hashes, "devBridgeCoordinatorSha256", out string? coordinatorHash) &&
             !string.Equals(coordinatorHash, package.DevBridgeCoordinatorSha256, StringComparison.OrdinalIgnoreCase)) ||
            (TryString(hashes, "transactionConsumerSha256", out string? consumerHash) &&
             !string.Equals(consumerHash, package.TransactionConsumerSha256, StringComparison.OrdinalIgnoreCase)))
        {
            error = "The promotion package runtime or transaction-consumer hashes do not match the qualification artifact.";
            return false;
        }
        return true;
    }

    private static bool TryObject(JsonElement root, string name, out JsonElement value)
    {
        if (root.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object)
        {
            return true;
        }
        value = default;
        return false;
    }

    private static bool TryString(JsonElement root, string name, out string? value)
    {
        value = root.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryInt(JsonElement root, string name, out int value)
    {
        value = 0;
        return root.TryGetProperty(name, out JsonElement element) && element.TryGetInt32(out value);
    }

    private static string? SafeArtifactPath(string root, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':'))
        {
            return null;
        }
        string candidate = Path.GetFullPath(Path.Combine(root, relative));
        string boundary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(boundary, StringComparison.OrdinalIgnoreCase) ? candidate : null;
    }
    private static bool SamePath(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) &&
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static string ReadRuntimeFileHash(string runtimeManifestPath, string relativePath)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(runtimeManifestPath));
        if (!document.RootElement.TryGetProperty("files", out JsonElement files) ||
            files.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        foreach (JsonElement file in files.EnumerateArray())
        {
            if (file.TryGetProperty("path", out JsonElement path) &&
                string.Equals(path.GetString(), relativePath, StringComparison.OrdinalIgnoreCase) &&
                file.TryGetProperty("sha256", out JsonElement hash))
            {
                return hash.GetString() ?? string.Empty;
            }
        }
        return string.Empty;
    }

    private static string ReadRuntimePackageHash(string runtimeManifestPath)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(runtimeManifestPath));
        return document.RootElement.TryGetProperty("packageSha256", out JsonElement hash) &&
            hash.ValueKind == JsonValueKind.String
            ? hash.GetString() ?? string.Empty
            : string.Empty;
    }

    internal static string ComputePromotedFingerprint(
        string sourceCommit,
        string executableHash,
        string assemblyHash,
        string coordinatorHash,
        string devBridgeHash,
        string consumerHash,
        string compatibility,
        string ownerProduct,
        string runtimeSubsystem)
    {
        string payload = string.Join("\n", [
            ToolchainPromotionSchemas.Package,
            "unified-production-package/v2",
            ownerProduct,
            runtimeSubsystem,
            sourceCommit,
            executableHash,
            assemblyHash,
            coordinatorHash,
            devBridgeHash,
            consumerHash,
            compatibility]);
        return "tc-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private static async Task<PromotionCandidateHealthResult> RunCandidateHealthAsync(
        PromotionCandidateHealthBinding binding,
        string workflowId,
        CancellationToken cancellationToken,
        IPromotionLeaseOrchestrator? promotionLeaseOrchestrator = null)
    {
        var checks = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["healthStage"] = "candidate-pre-commit",
            ["candidateExecutable"] = binding.CandidateExecutable,
            ["candidateRuntimeRoot"] = binding.CandidateRuntimeRoot,
            ["candidateFingerprint"] = binding.CandidateFingerprint,
            ["candidateSourceCommit"] = binding.CandidateSourceCommit,
            ["devBridgePackageSha256"] = binding.DevBridgePackageSha256,
            ["devBridgeCoordinatorSha256"] = binding.DevBridgeCoordinatorSha256,
            ["transactionConsumerSha256"] = binding.TransactionConsumerSha256,
            ["runtimeProtocolContract"] = binding.RuntimeProtocolContract,
            ["rimWorldExecutable"] = binding.RimWorldExecutable,
            ["rimLiaisonDoctor"] = "not-run",
            ["devBridgeRestart"] = "not-run",
            ["devBridgeStatus"] = "not-run",
            ["capabilities"] = "not-run",
            ["activeLeases"] = "unknown",
            ["coordinatorCount"] = "status-bound"
        };
        try
        {
            if (!File.Exists(binding.CandidateExecutable))
                return CandidateHealthFailure(checks, binding, "The candidate RimLiaison executable is missing.");
            if (!File.Exists(binding.RimWorldExecutable))
                return CandidateHealthFailure(checks, binding, "The candidate RimWorld executable binding is missing.");
            string devBridgeCommand = Path.Combine(binding.CandidateRuntimeRoot, "DevBridge.cmd");
            if (!File.Exists(devBridgeCommand))
                return CandidateHealthFailure(checks, binding, "The candidate DevBridge command is missing.");

            IReadOnlyDictionary<string, string> environment = CandidateHealthEnvironment(binding);
            (int exitCode, string output) liaisonDoctor = await RunJsonCommandAsync(
                binding.CandidateExecutable,
                ["doctor", "--devbridge-root", binding.CandidateRuntimeRoot, "--json"],
                cancellationToken,
                environment).ConfigureAwait(false);
            checks["rimLiaisonDoctor"] = IsReady(liaisonDoctor.exitCode, liaisonDoctor.output)
                ? "ready"
                : "failed";
            if (!IsReady(liaisonDoctor.exitCode, liaisonDoctor.output))
                return CandidateHealthFailure(checks, binding, "Candidate RimLiaison doctor did not report READY.", liaisonDoctor.output);

            (int exitCode, string output) restart = await RunJsonCommandAsync(
                "cmd.exe",
                ["/d", "/c", devBridgeCommand, "restart", "--json"],
                cancellationToken,
                environment).ConfigureAwait(false);
            checks["devBridgeRestart"] = IsReady(restart.exitCode, restart.output)
                ? "ready"
                : "failed";
            if (!TryParse(restart.output, out JsonDocument? restartDocument))
                return CandidateHealthFailure(checks, binding, "Candidate DevBridge restart did not return structured JSON.", restart.output);
            restartDocument!.Dispose();

            (int exitCode, string output) status = await RunJsonCommandAsync(
                "cmd.exe",
                ["/d", "/c", devBridgeCommand, "status", "--json"],
                cancellationToken,
                environment).ConfigureAwait(false);
            checks["devBridgeStatus"] = IsReady(status.exitCode, status.output) ? "ready" : "failed";
            if (!TryParse(status.output, out JsonDocument? statusDocument))
                return CandidateHealthFailure(checks, binding, "Candidate DevBridge status did not return structured JSON.", status.output);
            int? expectedGeneration;
            using (statusDocument)
            {
                JsonElement statusRoot = statusDocument!.RootElement;
                if (!RuntimeIdentityMatches(statusRoot, binding.CandidateRuntimeRoot))
                    return CandidateHealthFailure(checks, binding, "Candidate DevBridge status resolved a different runtime root.", status.output);
                if (!IsReady(status.exitCode, statusRoot.GetRawText()))
                    return CandidateHealthFailure(checks, binding, "Candidate DevBridge status is not READY.", status.output);
                int activeTests = statusRoot.TryGetProperty("activeTests", out JsonElement active) &&
                    active.TryGetInt32(out int activeValue)
                    ? activeValue
                    : -1;
                expectedGeneration = statusRoot.TryGetProperty("generation", out JsonElement generationElement) &&
                    generationElement.TryGetInt32(out int generationValue) &&
                    generationValue > 0
                    ? generationValue
                    : null;
                checks["generation"] = expectedGeneration?.ToString() ?? "unknown";
                checks["activeLeases"] = activeTests == 0 ? "zero" : "nonzero-or-unknown";
                if (expectedGeneration is null)
                    return CandidateHealthFailure(checks, binding, "Candidate DevBridge status did not prove a current generation.", status.output);
                if (activeTests != 0)
                    return CandidateHealthFailure(checks, binding, "Candidate DevBridge reports an active test or lease owner.", status.output);
            }

            (int exitCode, string output) doctor = await RunJsonCommandAsync(
                "cmd.exe",
                ["/d", "/c", devBridgeCommand, "doctor", "--json"],
                cancellationToken,
                environment).ConfigureAwait(false);
            checks["devBridgeDoctor"] = IsHealthy(doctor.exitCode, doctor.output) ? "healthy" : "failed";
            if (!TryParse(doctor.output, out JsonDocument? doctorDocument))
                return CandidateHealthFailure(checks, binding, "Candidate DevBridge doctor did not return structured JSON.", doctor.output);
            using (doctorDocument)
            {
                JsonElement root = doctorDocument!.RootElement;
                if (!RuntimeIdentityMatches(root, binding.CandidateRuntimeRoot))
                    return CandidateHealthFailure(checks, binding, "Candidate DevBridge doctor resolved a different runtime root.", doctor.output);
                if (!IsHealthy(doctor.exitCode, root.GetRawText()) ||
                    !TryFindingLeaseCount(root, out int leaseCount) ||
                    leaseCount != 0)
                {
                    return CandidateHealthFailure(checks, binding, "Candidate DevBridge doctor did not prove healthy zero-lease state.", doctor.output);
                }
            }

            DevBridgeAdapterOptions bridgeOptions = DevBridgeAdapterOptions.Discover(
                rootPath: binding.CandidateRuntimeRoot);
            var transport = new SystemDevBridgeProcessTransport();
            IPromotionLeaseOrchestrator liveOrchestrator = promotionLeaseOrchestrator ??
                new PromotionLeaseOrchestrator(
                    new DevBridgeLeaseAdapter(transport, bridgeOptions),
                    new DevBridgeCapabilityAdapter(transport, bridgeOptions));
            PromotionLiveVerificationResult live = await liveOrchestrator
                .VerifyCapabilitiesAsync(workflowId, expectedGeneration, cancellationToken)
                .ConfigureAwait(false);
            checks["capabilities"] = live.Passed ? "ready" : "failed";
            checks["capabilityLeaseId"] = live.LeaseId ?? "none";
            checks["capabilityLeaseGeneration"] = live.Generation?.ToString() ?? "unknown";
            checks["capabilityLeaseReleased"] = live.LeaseReleased ? "true" : "false";
            checks["capabilityLeaseAttempts"] = live.Attempts.ToString();
            if (!live.Passed)
            {
                string error = live.ErrorCode is null
                    ? live.Error ?? "The candidate executable did not return READY capabilities."
                    : live.ErrorCode + ": " + (live.Error ?? "The candidate executable did not return READY capabilities.");
                return CandidateHealthFailure(checks, binding, error);
            }

            (int exitCode, string output) finalStatus = await RunJsonCommandAsync(
                "cmd.exe",
                ["/d", "/c", devBridgeCommand, "status", "--json"],
                cancellationToken,
                environment).ConfigureAwait(false);
            if (!TryParse(finalStatus.output, out JsonDocument? finalStatusDocument))
                return CandidateHealthFailure(checks, binding, "Candidate DevBridge final status did not return structured JSON.", finalStatus.output);
            using (finalStatusDocument)
            {
                JsonElement root = finalStatusDocument!.RootElement;
                int activeTests = root.TryGetProperty("activeTests", out JsonElement active) &&
                    active.TryGetInt32(out int activeValue)
                    ? activeValue
                    : -1;
                checks["activeLeases"] = activeTests == 0 ? "zero" : "nonzero-or-unknown";
                if (!RuntimeIdentityMatches(root, binding.CandidateRuntimeRoot) ||
                    !IsReady(finalStatus.exitCode, root.GetRawText()) ||
                    activeTests != 0)
                {
                    return CandidateHealthFailure(checks, binding, "Candidate DevBridge final status did not prove READY zero-lease identity.", finalStatus.output);
                }
            }

            (int exitCode, string output) finalDoctor = await RunJsonCommandAsync(
                "cmd.exe",
                ["/d", "/c", devBridgeCommand, "doctor", "--json"],
                cancellationToken,
                environment).ConfigureAwait(false);
            if (!TryParse(finalDoctor.output, out JsonDocument? finalDoctorDocument))
                return CandidateHealthFailure(checks, binding, "Candidate DevBridge final doctor did not return structured JSON.", finalDoctor.output);
            using (finalDoctorDocument)
            {
                JsonElement root = finalDoctorDocument!.RootElement;
                checks["devBridgeDoctor"] = IsHealthy(finalDoctor.exitCode, root.GetRawText())
                    ? "healthy"
                    : "failed";
                if (!RuntimeIdentityMatches(root, binding.CandidateRuntimeRoot) ||
                    !IsHealthy(finalDoctor.exitCode, root.GetRawText()) ||
                    !TryFindingLeaseCount(root, out int leaseCount) ||
                    leaseCount != 0)
                {
                    return CandidateHealthFailure(checks, binding, "Candidate DevBridge final doctor did not prove healthy zero-lease identity.", finalDoctor.output);
                }
            }

            return CandidateHealthSuccess(checks, binding);
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException or OperationCanceledException)
        {
            return CandidateHealthFailure(checks, binding, exception.Message, exception.ToString());
        }
    }

    private static PromotionCandidateHealthResult CandidateHealthSuccess(
        IReadOnlyDictionary<string, string> checks,
        PromotionCandidateHealthBinding binding) =>
        new(
            true,
            JsonSerializer.Serialize(checks, WriteOptions),
            null,
            CandidateHealthEvidence(binding, "passed", null, null));

    private static PromotionCandidateHealthResult CandidateHealthFailure(
        IReadOnlyDictionary<string, string> checks,
        PromotionCandidateHealthBinding binding,
        string error,
        string? nestedError = null) =>
        new(
            false,
            JsonSerializer.Serialize(checks, WriteOptions),
            error,
            CandidateHealthEvidence(binding, "failed", error, nestedError));

    private static PromotionCandidateHealthEvidence CandidateHealthEvidence(
        PromotionCandidateHealthBinding binding,
        string status,
        string? error,
        string? nestedError) =>
        new(
            "candidate-pre-commit",
            status,
            binding.CandidateFingerprint,
            binding.CandidateRuntimeRoot,
            binding.CandidateExecutable,
            binding.CandidateSourceCommit,
            binding.DevBridgePackageSha256,
            binding.DevBridgeCoordinatorSha256,
            binding.TransactionConsumerSha256,
            binding.RuntimeProtocolContract,
            error,
            nestedError);

    private static bool CandidateHealthEvidenceMatches(
        PromotionCandidateHealthEvidence evidence,
        PromotionCandidateHealthBinding binding,
        out string? error)
    {
        error = null;
        if (!string.Equals(evidence.Stage, "candidate-pre-commit", StringComparison.Ordinal) ||
            !string.Equals(evidence.Status, "passed", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(evidence.CandidateFingerprint, binding.CandidateFingerprint, StringComparison.Ordinal) ||
            !SamePath(evidence.CandidateRuntimeRoot, binding.CandidateRuntimeRoot) ||
            !SamePath(evidence.CandidateExecutable, binding.CandidateExecutable) ||
            !string.Equals(evidence.CandidateSourceCommit, binding.CandidateSourceCommit, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(evidence.DevBridgePackageSha256, binding.DevBridgePackageSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(evidence.DevBridgeCoordinatorSha256, binding.DevBridgeCoordinatorSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(evidence.TransactionConsumerSha256, binding.TransactionConsumerSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(evidence.RuntimeProtocolContract, binding.RuntimeProtocolContract, StringComparison.Ordinal))
        {
            error = "Candidate health did not prove the exact staged candidate identity.";
            return false;
        }
        return true;
    }

    private static IReadOnlyDictionary<string, string> CandidateHealthEnvironment(
        PromotionCandidateHealthBinding binding) =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["RIMTEST_DEVBRIDGE_ROOT"] = binding.CandidateRuntimeRoot,
            ["DEVBRIDGE_RUNTIME_ROOT"] = binding.CandidateRuntimeRoot,
            ["DEVBRIDGE_TEST_RIMWORLD_PATH"] = binding.RimWorldExecutable
        };

    private static bool RuntimeIdentityMatches(JsonElement root, string expectedRuntimeRoot)
    {
        JsonElement identity = root.TryGetProperty("runtimeIdentity", out JsonElement nested)
            ? nested
            : root;
        return identity.TryGetProperty("devBridgeRuntimeRoot", out JsonElement runtimeRoot) &&
            runtimeRoot.ValueKind == JsonValueKind.String &&
            SamePath(runtimeRoot.GetString(), expectedRuntimeRoot);
    }

    private static string DescribePreviousProductionHealth(ProductionToolchainManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.DevBridgeRuntimeRoot) ||
            !Directory.Exists(manifest.DevBridgeRuntimeRoot))
            return "missing";
        return File.Exists(Path.Combine(manifest.DevBridgeRuntimeRoot, "DevBridge.cmd"))
            ? "present-unverified"
            : "incomplete";
    }

    private static string? ResolveCandidateRimWorldExecutable(string sourceRoot)
    {
        RimDevWorkspaceDiscovery workspace = RimDevWorkspaceDiscoverer.Discover(null, sourceRoot);
        string? configuredRoot = workspace.Configuration?.RimWorldRoot ??
            Environment.GetEnvironmentVariable("RIMWORLD_ROOT");
        string? configuredExecutable = workspace.Configuration?.RimWorldExecutable ??
            Environment.GetEnvironmentVariable("RIMWORLD_EXECUTABLE");
        if (string.IsNullOrWhiteSpace(configuredRoot) && string.IsNullOrWhiteSpace(configuredExecutable))
            return null;
        string baseRoot = workspace.RootPath;
        string? root = string.IsNullOrWhiteSpace(configuredRoot)
            ? null
            : Path.GetFullPath(Path.IsPathRooted(configuredRoot)
                ? configuredRoot
                : Path.Combine(baseRoot, configuredRoot));
        return string.IsNullOrWhiteSpace(configuredExecutable)
            ? Path.Combine(root!, "RimWorldWin64.exe")
            : Path.GetFullPath(Path.IsPathRooted(configuredExecutable)
                ? configuredExecutable
                : Path.Combine(baseRoot, configuredExecutable));
    }

    private static async Task<PromotionCanonicalHealthResult> RunCanonicalProductionHealthAsync(
        string sourceRoot,
        string executable,
        string expectedFingerprint,
        CancellationToken cancellationToken)
    {
        ProductionToolchainBindingResolution identity = ProductionToolchainBindingResolver.Resolve(
            sourceRoot,
            currentExecutablePath: executable);
        if (!identity.Succeeded ||
            !string.Equals(
                identity.Binding!.PromotedFingerprint,
                expectedFingerprint,
                StringComparison.Ordinal))
        {
            string error = identity.Failure?.Error ??
                "Canonical post-commit binding did not resolve the candidate fingerprint.";
            return new(
                false,
                JsonSerializer.Serialize(identity.Failure?.ToEvidence() ?? new
                {
                    expectedFingerprint,
                    resolvedFingerprint = identity.Binding?.PromotedFingerprint
                }, WriteOptions),
                error);
        }

        (int exitCode, string output) doctor = await RunJsonCommandAsync(
            executable,
            ["doctor", "--json"],
            cancellationToken,
            workingDirectory: sourceRoot).ConfigureAwait(false);
        bool ready = IsReady(doctor.exitCode, doctor.output);
        return new(
            ready,
            doctor.output,
            ready ? null : "Canonical post-commit doctor did not report READY.");
    }


    private static async Task<(int exitCode, string output)> RunJsonCommandAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null,
        string? workingDirectory = null)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        if (environment is not null)
        {
            foreach ((string name, string value) in environment)
                process.StartInfo.Environment[name] = value;
        }
        foreach (string argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start())
            return (-1, string.Empty);

        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
            throw;
        }
        await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
        return (process.ExitCode, outputTask.Result);
    }
    private static bool TryLeaseId(int exitCode, string output, out string? leaseId)
    {
        leaseId = null;
        if (exitCode != 0 || !TryParse(output, out JsonDocument? document))
        {
            return false;
        }
        using (document)
        {
            JsonElement root = document!.RootElement;
            if (!root.TryGetProperty("leaseId", out JsonElement value) ||
                value.ValueKind != JsonValueKind.String)
            {
                return false;
            }
            leaseId = value.GetString();
            return !string.IsNullOrWhiteSpace(leaseId);
        }
    }

    private static void WriteFailureHandoff(
        string packagePath,
        ToolchainPromotionPackage? package,
        ProductionToolchainManifest? previous,
        string errorCode,
        string error,
        string nextAction)
    {
        try
        {
            string root = Path.Combine("C:\\RimDev", ".rimdev", "failure-handoffs");
            Directory.CreateDirectory(root);
            string timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ");
            File.WriteAllText(
                Path.Combine(root, "unified-production-package-" + timestamp + ".json"),
                JsonSerializer.Serialize(
                    new
                    {
                        schemaVersion = "rimliaison-unified-production-failure-handoff/v1",
                        createdUtc = DateTimeOffset.UtcNow,
                        packagePath = Path.GetFullPath(packagePath),
                        errorCode,
                        error,
                        nextAction,
                        package,
                        previousProductionManifest = previous
                    },
                    WriteOptions));
        }
        catch (Exception)
        {
        }
    }

    private static bool TryParse(string output, out JsonDocument? document)
    {
        try
        {
            document = JsonDocument.Parse(output);
            return true;
        }
        catch (JsonException)
        {
            document = null;
            return false;
        }
    }

    private static bool IsReady(int exitCode, string output)
    {
        return exitCode == 0 && TryParse(output, out JsonDocument? document) &&
            IsReady(exitCode, document!);
    }

    private static bool IsReady(int exitCode, JsonDocument document)
    {
        using (document)
        {
            JsonElement root = document.RootElement;
            return exitCode == 0 &&
                ((root.TryGetProperty("status", out JsonElement status) &&
                    string.Equals(status.GetString(), "ready", StringComparison.OrdinalIgnoreCase)) ||
                 (root.TryGetProperty("state", out JsonElement state) &&
                    string.Equals(state.GetString(), "READY", StringComparison.OrdinalIgnoreCase)));
        }
    }

    private static bool IsHealthy(int exitCode, string output)
    {
        return exitCode == 0 && TryParse(output, out JsonDocument? document) &&
            IsHealthy(exitCode, document!);
    }

    private static bool IsHealthy(int exitCode, JsonDocument document)
    {
        using (document)
        {
            JsonElement root = document.RootElement;
            return IsReady(exitCode, root.GetRawText()) &&
                root.TryGetProperty("healthy", out JsonElement healthy) &&
                healthy.ValueKind == JsonValueKind.True;
        }
    }

    private static bool IsResponsive(int exitCode, string output)
    {
        return exitCode == 0 && TryParse(output, out JsonDocument? document) &&
            IsResponsive(exitCode, document!);
    }

    private static bool IsResponsive(int exitCode, JsonDocument document)
    {
        using (document)
        {
            return exitCode == 0 &&
                document.RootElement.TryGetProperty("state", out JsonElement state) &&
                string.Equals(state.GetString(), "Responsive", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool TryFindingLeaseCount(JsonElement root, out int count)
    {
        if (root.TryGetProperty("findings", out JsonElement findings) &&
            findings.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement finding in findings.EnumerateArray())
            {
                if (finding.TryGetProperty("code", out JsonElement code) &&
                    code.GetString() == "LEASES_VALIDATED" &&
                    finding.TryGetProperty("details", out JsonElement details) &&
                    details.TryGetProperty("leaseCount", out JsonElement leaseCount) &&
                    int.TryParse(leaseCount.GetString(), out count))
                {
                    return true;
                }
            }
        }
        count = -1;
        return false;
    }



    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    private static void CopyImmutableDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            CopyImmutableFile(
                file,
                Path.Combine(destination, Path.GetRelativePath(source, file)));
        }
    }
    private static void CopyImmutableFile(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination))
        {
            if (!string.Equals(
                    ToolchainFileHash.Sha256(source),
                    ToolchainFileHash.Sha256(destination),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The immutable qualified recovery payload contains a substituted artifact.");
            }
            return;
        }
        File.Copy(source, destination, overwrite: false);
    }

    private static void AtomicReplace(string path, string content)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, content);
        try
        {
            File.Replace(temporary, path, null);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
public sealed record PromotionCanonicalHealthResult(
    bool Passed,
    string Summary,
    string? Error);

public interface IPromotionCanonicalHealthVerifier
{
    Task<PromotionCanonicalHealthResult> VerifyAsync(
        string sourceRoot,
        string installedExecutable,
        string expectedFingerprint,
        CancellationToken cancellationToken);
}

internal static class ToolchainFileHash
{
    public static string Sha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
