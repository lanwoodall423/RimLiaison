using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RimLiaison.Git;
using RimLiaison.DevBridge;


namespace RimLiaison.Toolchain;

public static class ToolchainPromotionSchemas
{
    public const string Package = "rimliaison-toolchain-promotion/v2";
    public const string Result = "rimliaison-toolchain-promotion-result/v1";
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
    [JsonPropertyName("devBridgeRuntimeRoot")]
    public string? DevBridgeRuntimeRoot { get; init; }
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
    [JsonPropertyName("compatibilityContract")]
    public string? CompatibilityContract { get; init; }
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

    public static async Task<ToolchainPromotionResult> PromoteAsync(
        string sourceRoot,
        string? packagePath,
        string? qualificationArtifactPath,
        CancellationToken cancellationToken = default,
        string? workflowId = null,
        IPromotionLeaseOrchestrator? promotionLeaseOrchestrator = null)
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
        ProductionToolchainManifest? previous = null;
        ToolchainPromotionPackage? package = null;
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

            GitRepositoryStateResult source = await new SystemGitRepositoryStateProvider()
                .ReadAsync(sourceRoot, cancellationToken)
                .ConfigureAwait(false);
            if (!source.Resolved || source.State?.HeadSha is null ||
                !string.Equals(source.State.HeadSha, package.SourceCommit, StringComparison.OrdinalIgnoreCase))
            {
                return ToolchainPromotionResult.Blocked(
                    "PROMOTION_SOURCE_FINGERPRINT_STALE",
                    "The current source HEAD does not match the qualified source commit.",
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

            if (!string.Equals(package.CompatibilityContract, previous.CompatibilityContract, StringComparison.Ordinal) ||
                !string.Equals(package.DevBridgeRuntimeRoot, previous.DevBridgeRuntimeRoot, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(package.DevBridgePackageSha256, previous.DevBridgePackageSha256, StringComparison.OrdinalIgnoreCase))
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
                    "A unified production input hash differs from the package declaration.",
                    package.SourceCommit,
                    artifactPath,
                    qualificationHash,
                    previous.PromotedFingerprint,
                    "Rebuild the unified promotion package from the qualified artifacts.");
            }

            string runtimeManifestPath = Path.Combine(previous.DevBridgeRuntimeRoot!, ".devbridge-runtime-manifest.json");
            string coordinatorHash = ReadRuntimeFileHash(
                runtimeManifestPath,
                "Coordinator/DevBridge.Coordinator.exe");
            if (!string.Equals(coordinatorHash, package.DevBridgeCoordinatorSha256, StringComparison.OrdinalIgnoreCase))
            {
                return ToolchainPromotionResult.Blocked(
                    "PROMOTION_COORDINATOR_IDENTITY_MISMATCH",
                    "The installed DevBridge Coordinator is not the qualified unified runtime component.",
                    package.SourceCommit,
                    artifactPath,
                    qualificationHash,
                    previous.PromotedFingerprint,
                    "Publish the qualified DevBridge runtime, then rebuild the unified promotion package.");
            }

            string productionCliDirectory = Path.GetDirectoryName(Path.GetFullPath(previous.RimLiaisonExecutablePath!))!;
            string productionParent = Directory.GetParent(productionCliDirectory)!.FullName;
            string promotedRootName = "cli-promoted-" + sourceCommit[..Math.Min(12, sourceCommit.Length)];
            stagedRoot = Path.Combine(productionParent, promotedRootName);
            if (Directory.Exists(stagedRoot))
            {
                return ToolchainPromotionResult.Blocked(
                    "PROMOTION_DESTINATION_EXISTS",
                    "The bounded promotion destination already exists; refusing to reuse it.",
                    package.SourceCommit,
                    artifactPath,
                    qualificationHash,
                    previous.PromotedFingerprint);
            }

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
            File.Copy(consumerSource, consumerRelativePath, overwrite: false);

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
                previous.DevBridgePackageSha256!,
                consumerHash,
                previous.CompatibilityContract!);
            string executionFingerprint = ProductionToolchainBindingResolver.ComputeExecutionFingerprint(
                promotedFingerprint,
                installedExecutableHash,
                installedAssemblyHash,
                coordinatorHash,
                previous.DevBridgePackageSha256!,
                installedConsumerHash,
                previous.CompatibilityContract!);
            var unifiedManifest = new
            {
                schemaVersion = "rimliaison-unified-production-package/v1",
                productFingerprint = promotedFingerprint,
                sourceCommit,
                compatibilityContract = previous.CompatibilityContract,
                rimLiaison = new
                {
                    executablePath = Path.GetRelativePath(stagedRoot, installedExecutable),
                    executableSha256 = installedExecutableHash,
                    assemblyPath = Path.GetRelativePath(stagedRoot, installedAssembly),
                    assemblySha256 = installedAssemblyHash
                },
                devBridge = new
                {
                    runtimeRoot = previous.DevBridgeRuntimeRoot,
                    packageSha256 = previous.DevBridgePackageSha256,
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

            ProductionHealthResult health = await RunProductionHealthAsync(
                installedExecutable,
                previous.DevBridgeRuntimeRoot!,
                workflowId ?? "rimliaison-promotion-" + sourceCommit[..Math.Min(12, sourceCommit.Length)],
                cancellationToken,
                promotionLeaseOrchestrator).ConfigureAwait(false);
            if (!health.Passed)
            {
                WriteFailureHandoff(
                    packagePath,
                    package,
                    previous,
                    "PROMOTION_PRODUCTION_HEALTH_FAILED",
                    health.Error ?? "The promoted production health checks did not pass.",
                    "Repair the production control plane, then retry the supported promotion command.");
                return ToolchainPromotionResult.Blocked(
                    "PROMOTION_PRODUCTION_HEALTH_FAILED",
                    health.Error ?? "The promoted production health checks did not pass.",
                    package.SourceCommit,
                    artifactPath,
                    qualificationHash,
                    previous.PromotedFingerprint,
                    "Repair the production control plane, then retry the supported promotion command.",
                    health.Summary);
            }

            var updated = new ProductionToolchainManifest
            {
                SchemaVersion = previous.SchemaVersion,
                PromotedFingerprint = promotedFingerprint,
                Fingerprint = executionFingerprint,
                RimLiaisonExecutablePath = installedExecutable,
                RimLiaisonExecutableSha256 = installedExecutableHash,
                RimLiaisonAssemblyPath = installedAssembly,
                RimLiaisonAssemblySha256 = installedAssemblyHash,
                DevBridgeRuntimeRoot = previous.DevBridgeRuntimeRoot,
                DevBridgePackageSha256 = previous.DevBridgePackageSha256,
                DevBridgeCoordinatorSha256 = coordinatorHash,
                TransactionConsumerPath = consumerRelativePath,
                TransactionConsumerSha256 = installedConsumerHash,
                UnifiedManifestPath = unifiedManifestPath,
                UnifiedManifestSha256 = unifiedManifestHash,
                CompatibilityContract = previous.CompatibilityContract,
                QualifiedSourceCommit = package.SourceCommit,
                QualificationArtifactPath = artifactPath,
                QualificationArtifactSha256 = qualificationHash
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
                    previous.PromotedFingerprint);
            }

            string doctor = health.Summary;

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
                    ["devBridgePackageSha256"] = previous.DevBridgePackageSha256!,
                    ["devBridgeCoordinatorSha256"] = coordinatorHash,
                    ["transactionConsumerSha256"] = consumerHash
                },
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["rimLiaisonExecutableSha256"] = installedExecutableHash,
                    ["rimLiaisonAssemblySha256"] = installedAssemblyHash,
                    ["devBridgePackageSha256"] = previous.DevBridgePackageSha256!,
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
                PromotionTransactionId = promotionTransactionId
            };
        }
        catch (IOException exception) when (exception.HResult == unchecked((int)0x800700B7))
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
            if (!promotionCommitted && stagedRoot is not null)
            {
                TryDelete(stagedRoot);
            }
            TryDelete(lockPath);
        }

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
    private static ToolchainPromotionPackage? ReadPackage(string path, out string? error)
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
                string.IsNullOrWhiteSpace(package.DevBridgeRuntimeRoot) ||
                string.IsNullOrWhiteSpace(package.DevBridgePackageSha256) ||
                string.IsNullOrWhiteSpace(package.DevBridgeCoordinatorSha256) ||
                string.IsNullOrWhiteSpace(package.TransactionConsumerPath) ||
                string.IsNullOrWhiteSpace(package.TransactionConsumerRelativePath) ||
                string.IsNullOrWhiteSpace(package.TransactionConsumerSha256) ||
                string.IsNullOrWhiteSpace(package.UnifiedManifestRelativePath) ||
                string.IsNullOrWhiteSpace(package.CompatibilityContract))
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
                string.IsNullOrWhiteSpace(manifest.DevBridgeRuntimeRoot) ||
                string.IsNullOrWhiteSpace(manifest.DevBridgePackageSha256) ||
                string.IsNullOrWhiteSpace(manifest.TransactionConsumerPath) ||
                string.IsNullOrWhiteSpace(manifest.TransactionConsumerSha256) ||
                string.IsNullOrWhiteSpace(manifest.CompatibilityContract))
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

    private static bool PromotionProofIsComplete(JsonElement root, string? sourceCommit, out string? error)
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

    private static bool QualifiedHashesMatch(
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

    private static string ComputePromotedFingerprint(
        string sourceCommit,
        string executableHash,
        string assemblyHash,
        string coordinatorHash,
        string devBridgeHash,
        string consumerHash,
        string compatibility)
    {
        string payload = string.Join("\n", [
            ToolchainPromotionSchemas.Package,
            "unified-production-package/v1",
            sourceCommit,
            executableHash,
            assemblyHash,
            coordinatorHash,
            devBridgeHash,
            consumerHash,
            compatibility]);
        return "tc-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private sealed record ProductionHealthResult(bool Passed, string Summary, string? Error);
    private static async Task<ProductionHealthResult> RunProductionHealthAsync(
        string executable,
        string devBridgeRoot,
        string workflowId,
        CancellationToken cancellationToken,
        IPromotionLeaseOrchestrator? promotionLeaseOrchestrator = null)
    {
        var checks = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["rimLiaisonDoctor"] = "not-run",
            ["devBridgeStatus"] = "not-run",
            ["devBridgeDoctor"] = "not-run",
            ["capabilities"] = "not-run",
            ["activeLeases"] = "unknown",
            ["coordinatorCount"] = "status-bound"
        };
        try
        {
            (int exitCode, string output) liaisonDoctor = await RunJsonCommandAsync(
                executable,
                ["doctor", "--devbridge-root", devBridgeRoot, "--json"],
                cancellationToken).ConfigureAwait(false);
            checks["rimLiaisonDoctor"] = IsReady(liaisonDoctor.exitCode, liaisonDoctor.output)
                ? "ready"
                : "failed";

            string devBridgeCommand = Path.Combine(devBridgeRoot, "DevBridge.cmd");
            if (!File.Exists(devBridgeCommand))
            {
                return HealthFailure(checks, "The installed DevBridge command is missing.");
            }

            (int exitCode, string output) status = await RunJsonCommandAsync(
                "cmd.exe",
                ["/d", "/c", devBridgeCommand, "status", "--json"],
                cancellationToken).ConfigureAwait(false);
            checks["devBridgeStatus"] = IsReady(status.exitCode, status.output) ? "ready" : "failed";
            if (!TryParse(status.output, out JsonDocument? statusDocument))
            {
                return HealthFailure(checks, "DevBridge status did not return structured JSON.");
            }
            int? expectedGeneration = null;
            using (statusDocument)
            {
                JsonElement statusRoot = statusDocument!.RootElement;
                if (!IsReady(status.exitCode, statusRoot.GetRawText()))
                {
                    return HealthFailure(checks, "DevBridge status is not READY.");
                }
                int activeTests = statusRoot.TryGetProperty("activeTests", out JsonElement active)
                    && active.TryGetInt32(out int activeValue)
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
                {
                    return HealthFailure(checks, "DevBridge status did not prove a current generation.");
                }
                if (activeTests != 0)
                {
                    return HealthFailure(checks, "DevBridge reports an active test or lease owner.");
                }
            }

            (int exitCode, string output) doctor = await RunJsonCommandAsync(
                "cmd.exe",
                ["/d", "/c", devBridgeCommand, "doctor", "--json"],
                cancellationToken).ConfigureAwait(false);
            checks["devBridgeDoctor"] = IsHealthy(doctor.exitCode, doctor.output) ? "healthy" : "failed";
            if (!TryParse(doctor.output, out JsonDocument? doctorDocument))
            {
                return HealthFailure(checks, "DevBridge doctor did not return structured JSON.");
            }
            using (doctorDocument)
            {
                JsonElement root = doctorDocument!.RootElement;
                if (!IsHealthy(doctor.exitCode, root.GetRawText()) ||
                    !TryFindingLeaseCount(root, out int leaseCount) ||
                    leaseCount != 0)
                {
                    return HealthFailure(checks, "DevBridge doctor did not prove healthy zero-lease state.");
                }
            }

            DevBridgeAdapterOptions bridgeOptions = DevBridgeAdapterOptions.Discover(
                rootPath: devBridgeRoot);
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
                return HealthFailure(
                    checks,
                    live.ErrorCode is null
                        ? live.Error ?? "The promoted executable did not return READY capabilities."
                        : live.ErrorCode + ": " + (live.Error ?? "The promoted executable did not return READY capabilities."));
            }

            (int exitCode, string output) finalStatus = await RunJsonCommandAsync(
                "cmd.exe",
                ["/d", "/c", devBridgeCommand, "status", "--json"],
                cancellationToken).ConfigureAwait(false);
            if (!TryParse(finalStatus.output, out JsonDocument? finalStatusDocument))
            {
                return HealthFailure(checks, "DevBridge final status did not return structured JSON.");
            }
            using (finalStatusDocument)
            {
                JsonElement root = finalStatusDocument!.RootElement;
                int activeTests = root.TryGetProperty("activeTests", out JsonElement active) &&
                    active.TryGetInt32(out int activeValue)
                    ? activeValue
                    : -1;
                checks["activeLeases"] = activeTests == 0 ? "zero" : "nonzero-or-unknown";
                if (!IsReady(finalStatus.exitCode, root.GetRawText()) || activeTests != 0)
                {
                    return HealthFailure(checks, "DevBridge final status did not prove READY zero-lease state.");
                }
            }

            (int exitCode, string output) finalDoctor = await RunJsonCommandAsync(
                "cmd.exe",
                ["/d", "/c", devBridgeCommand, "doctor", "--json"],
                cancellationToken).ConfigureAwait(false);
            if (!TryParse(finalDoctor.output, out JsonDocument? finalDoctorDocument))
            {
                return HealthFailure(checks, "DevBridge final doctor did not return structured JSON.");
            }
            using (finalDoctorDocument)
            {
                JsonElement root = finalDoctorDocument!.RootElement;
                checks["devBridgeDoctor"] = IsHealthy(finalDoctor.exitCode, root.GetRawText())
                    ? "healthy"
                    : "failed";
                if (!IsHealthy(finalDoctor.exitCode, root.GetRawText()) ||
                    !TryFindingLeaseCount(root, out int leaseCount) ||
                    leaseCount != 0)
                {
                    return HealthFailure(checks, "DevBridge final doctor did not prove healthy zero-lease state.");
                }
            }

            return new ProductionHealthResult(true, JsonSerializer.Serialize(checks, WriteOptions), null);
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException or OperationCanceledException)
        {
            return HealthFailure(checks, exception.Message);
        }
    }

    private static ProductionHealthResult HealthFailure(
        IReadOnlyDictionary<string, string> checks,
        string error) =>
        new(false, JsonSerializer.Serialize(checks, WriteOptions), error);

    private static async Task<(int exitCode, string output)> RunJsonCommandAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        string? devBridgeRoot = null,
        string? devBridgeAgent = null)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        if (!string.IsNullOrWhiteSpace(devBridgeRoot))
        {
            process.StartInfo.Environment["RIMTEST_DEVBRIDGE_ROOT"] = devBridgeRoot;
        }
        if (!string.IsNullOrWhiteSpace(devBridgeAgent))
        {
            process.StartInfo.Environment["DEVBRIDGE_AGENT"] = devBridgeAgent;
        }
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        if (!process.Start())
        {
            return (-1, string.Empty);

        }
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
            {
                process.Kill(entireProcessTree: true);
            }
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

internal static class ToolchainFileHash
{
    public static string Sha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
