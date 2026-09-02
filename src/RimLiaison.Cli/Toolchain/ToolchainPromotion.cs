using System.Diagnostics;
using System.ComponentModel;
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
    public const string Package = "rimliaison-toolchain-promotion/v3";
    public const string LegacyPackage = "rimliaison-toolchain-promotion/v2";
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
    [JsonPropertyName("rimLiaisonCliDeploymentRootRelativePath")]
    public string? RimLiaisonCliDeploymentRootRelativePath { get; init; }
    [JsonPropertyName("rimLiaisonCliDeploymentManifestRelativePath")]
    public string? RimLiaisonCliDeploymentManifestRelativePath { get; init; }
    [JsonPropertyName("rimLiaisonCliDeploymentManifestSha256")]
    public string? RimLiaisonCliDeploymentManifestSha256 { get; init; }
    [JsonPropertyName("rimLiaisonCliDeploymentPackageSha256")]
    public string? RimLiaisonCliDeploymentPackageSha256 { get; init; }
    [JsonPropertyName("rimLiaisonCliTargetFramework")]
    public string? RimLiaisonCliTargetFramework { get; init; }
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
    [JsonPropertyName("devBridgeModSha256")]
    public string? DevBridgeModSha256 { get; init; }
    [JsonPropertyName("devBridgeRuntimeManifestSha256")]
    public string? DevBridgeRuntimeManifestSha256 { get; init; }
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
    [JsonPropertyName("meaningfulDirtyPaths")]
    public IReadOnlyList<string>? MeaningfulDirtyPaths { get; init; }
    [JsonPropertyName("machinePreflight")]
    public ProductionMachinePreflightResult? MachinePreflight { get; init; }

    public static ToolchainPromotionResult Blocked(
        string code,
        string error,
        string? sourceCommit = null,
        string? qualificationArtifactPath = null,
        string? qualificationArtifactSha256 = null,
        string? previousFingerprint = null,
        string? nextAction = null,
        string? productionDoctor = null,
        IReadOnlyList<string>? meaningfulDirtyPaths = null) => new(
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
        nextAction)
        {
            MeaningfulDirtyPaths = meaningfulDirtyPaths
        };
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
    string RimWorldExecutable)
{
    public string? RimLiaisonExecutableSha256 { get; init; }
    public string? RimLiaisonAssemblySha256 { get; init; }
    public string? DevBridgeModSha256 { get; init; }
    public string? DevBridgeRuntimeManifestSha256 { get; init; }
    public string? RimLiaisonCliDeploymentManifestSha256 { get; init; }
    public string? RimLiaisonCliDeploymentPackageSha256 { get; init; }
}

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
    [property: JsonPropertyName("activeManifestChanged")] bool ActiveManifestChanged = false)
{
    [JsonPropertyName("devBridgeModSha256")]
    public string? DevBridgeModSha256 { get; init; }
    [JsonPropertyName("devBridgeRuntimeManifestSha256")]
    public string? DevBridgeRuntimeManifestSha256 { get; init; }
    [JsonPropertyName("rimLiaisonCliDeploymentManifestSha256")]
    public string? RimLiaisonCliDeploymentManifestSha256 { get; init; }
    [JsonPropertyName("rimLiaisonCliDeploymentPackageSha256")]
    public string? RimLiaisonCliDeploymentPackageSha256 { get; init; }
    [JsonPropertyName("processEvidence")]
    public PromotionChildProcessResult? ProcessEvidence { get; init; }
}

public sealed record PromotionChildProcessResult(
    [property: JsonPropertyName("exitCode")] int ExitCode,
    [property: JsonPropertyName("stdout")] string Stdout,
    [property: JsonPropertyName("stderr")] string Stderr,
    [property: JsonPropertyName("timedOut")] bool TimedOut,
    [property: JsonPropertyName("cancelled")] bool Cancelled,
    [property: JsonPropertyName("executablePath")] string ExecutablePath,
    [property: JsonPropertyName("workingDirectory")] string WorkingDirectory,
    [property: JsonPropertyName("startError")] string? StartError = null);

public sealed record PromotionCandidateHealthResult(
    bool Passed,
    string Summary,
    string? Error,
    PromotionCandidateHealthEvidence Evidence)
{
    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; init; }
}

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
        CliDeploymentManifest? cliManifest;
        string? cliManifestError = null;
        if (string.IsNullOrWhiteSpace(candidate.RimLiaisonCliDeploymentRoot) ||
            string.IsNullOrWhiteSpace(candidate.RimLiaisonCliDeploymentManifestPath) ||
            string.IsNullOrWhiteSpace(candidate.RimLiaisonCliDeploymentManifestSha256) ||
            string.IsNullOrWhiteSpace(candidate.RimLiaisonCliDeploymentPackageSha256) ||
            !CliDeploymentManifestService.Verify(
                candidate.RimLiaisonCliDeploymentRoot,
                candidate.RimLiaisonCliDeploymentManifestPath,
                candidate.RimLiaisonCliDeploymentManifestSha256,
                candidate.RimLiaisonCliDeploymentPackageSha256,
                out cliManifest,
                out cliManifestError))
        {
            throw new InvalidDataException(
                cliManifestError ?? "The complete candidate CLI deployment manifest is required.");
        }
        if (!string.Equals(cliManifest!.SourceCommit, candidate.SourceCommit,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The CLI deployment manifest source commit does not equal the qualified candidate source.");
        }
        if (!qualification.QualifiedArtifactHashes.TryGetValue(
                "rimLiaisonCliDeploymentManifestSha256",
                out string? qualifiedCliManifestHash) ||
            !qualification.QualifiedArtifactHashes.TryGetValue(
                "rimLiaisonCliDeploymentPackageSha256",
                out string? qualifiedCliPackageHash) ||
            !string.Equals(candidate.RimLiaisonCliDeploymentManifestSha256, qualifiedCliManifestHash,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(candidate.RimLiaisonCliDeploymentPackageSha256, qualifiedCliPackageHash,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The complete CLI deployment identity does not equal the identity captured by qualification.");
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
        if (string.IsNullOrWhiteSpace(candidate.DevBridgeModSha256) ||
            string.IsNullOrWhiteSpace(candidate.DevBridgeRuntimeManifestSha256))
        {
            throw new InvalidDataException(
                "The RimLiaison-owned runtime manifest and mod hash are required for a unified package.");
        }

        string fullPackagePath = Path.GetFullPath(packagePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPackagePath)!);
        if (File.Exists(fullPackagePath))
        {
            throw new IOException("The qualified promotion package already exists and is immutable.");
        }

        string qualificationHash = ToolchainFileHash.Sha256(qualificationArtifactPath);
        string payloadIdentity = ComputeQualifiedPayloadIdentity(
            qualificationHash,
            candidate.SourceCommit,
            executableHash,
            assemblyHash,
            candidate.DevBridgePackageSha256,
            candidate.DevBridgeCoordinatorSha256,
            candidate.TransactionConsumerSha256,
            candidate.RuntimeProtocolContract,
            candidate.DevBridgeModSha256,
            candidate.DevBridgeRuntimeManifestSha256,
            candidate.RimLiaisonCliDeploymentManifestSha256,
            candidate.RimLiaisonCliDeploymentPackageSha256);
        string payloadRoot = Path.Combine(
            Path.GetDirectoryName(fullPackagePath)!,
            "qualified-toolchain-payload-" + candidate.SourceCommit + "-" + payloadIdentity);
        string payloadStageRoot = payloadRoot + ".tmp-" + Guid.NewGuid().ToString("N");
        string packageTemporaryPath = fullPackagePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        bool payloadPublishedByThisCall = false;

        try
        {
            Directory.CreateDirectory(payloadStageRoot);
            string payloadCliRoot = Path.Combine(payloadStageRoot, "cli");
            string payloadQualification = Path.Combine(payloadStageRoot, "qualification.json");
            string payloadConsumer = Path.Combine(payloadStageRoot, "transaction-components", "mod-test.ps1");
            CopyImmutableDirectory(candidate.RimLiaisonCliDeploymentRoot!, payloadCliRoot);
            CopyImmutableFile(qualificationArtifactPath, payloadQualification);
            CopyImmutableFile(candidate.TransactionConsumerPath, payloadConsumer);
            if (!CliDeploymentManifestService.Verify(
                    payloadCliRoot,
                    Path.Combine(payloadCliRoot, CliDeploymentManifestService.FileName),
                    candidate.RimLiaisonCliDeploymentManifestSha256,
                    candidate.RimLiaisonCliDeploymentPackageSha256,
                    out _,
                    out string? payloadCliError))
            {
                throw new InvalidDataException(
                    payloadCliError ?? "The immutable qualified CLI deployment is incomplete.");
            }

            string runtimeArtifactRoot = Path.Combine(payloadStageRoot, "runtime");
            CopyImmutableDirectory(candidate.DevBridgeRuntimeArtifactRoot, runtimeArtifactRoot);
            if (!RuntimeSnapshotIsVerified(
                    runtimeArtifactRoot,
                    candidate.DevBridgePackageSha256,
                    candidate.DevBridgeCoordinatorSha256,
                    candidate.DevBridgeModSha256,
                    candidate.DevBridgeRuntimeManifestSha256,
                    candidate.SourceCommit))
            {
                throw new InvalidDataException(
                    "The immutable candidate runtime snapshot does not match its bound hashes.");
            }

            string unifiedManifestPath = Path.Combine(payloadStageRoot, "unified-package.json");
            string promotedFingerprint = ComputePromotedFingerprint(
                candidate.SourceCommit,
                executableHash,
                assemblyHash,
                candidate.DevBridgeCoordinatorSha256,
                candidate.DevBridgePackageSha256,
                candidate.TransactionConsumerSha256,
                candidate.RuntimeProtocolContract,
                ToolchainPromotionSchemas.OwnerProduct,
                ToolchainPromotionSchemas.RuntimeSubsystem,
                candidate.DevBridgeModSha256,
                candidate.DevBridgeRuntimeManifestSha256,
                candidate.RimLiaisonCliDeploymentManifestSha256,
                candidate.RimLiaisonCliDeploymentPackageSha256);
            var unifiedManifest = new
            {
                schemaVersion = "rimliaison-unified-production-package/v3",
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
                    deploymentRoot = "cli",
                    deploymentManifestPath = "cli/" + CliDeploymentManifestService.FileName,
                    deploymentManifestSha256 = candidate.RimLiaisonCliDeploymentManifestSha256,
                    deploymentPackageSha256 = candidate.RimLiaisonCliDeploymentPackageSha256,
                    targetFramework = candidate.RimLiaisonCliTargetFramework,
                    executablePath = "cli/rimliaison.exe",
                    executableSha256 = executableHash,
                    assemblyPath = "cli/rimliaison.dll",
                    assemblySha256 = assemblyHash
                },
                runtimeManifestSha256 = candidate.DevBridgeRuntimeManifestSha256,
                modSha256 = candidate.DevBridgeModSha256,
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
            File.WriteAllText(unifiedManifestPath, unifiedJson);

            ToolchainPromotionPackage package = new()
            {
                SchemaVersion = ToolchainPromotionSchemas.Package,
                OwnerProduct = ToolchainPromotionSchemas.OwnerProduct,
                RuntimeSubsystem = ToolchainPromotionSchemas.RuntimeSubsystem,
                SourceCommit = candidate.SourceCommit,
                QualificationArtifactPath = Path.Combine(payloadRoot, "qualification.json"),
                QualificationArtifactSha256 = qualificationHash,
                ArtifactRoot = payloadRoot,
                RimLiaisonExecutableRelativePath = "cli/rimliaison.exe",
                RimLiaisonAssemblyRelativePath = "cli/rimliaison.dll",
                RimLiaisonCliDeploymentRootRelativePath = "cli",
                RimLiaisonCliDeploymentManifestRelativePath = "cli/" + CliDeploymentManifestService.FileName,
                RimLiaisonCliDeploymentManifestSha256 = candidate.RimLiaisonCliDeploymentManifestSha256,
                RimLiaisonCliDeploymentPackageSha256 = candidate.RimLiaisonCliDeploymentPackageSha256,
                RimLiaisonCliTargetFramework = cliManifest!.TargetFramework,
                DevBridgeRuntimeRoot = candidate.DevBridgeRuntimeRoot,
                DevBridgeRuntimeArtifactRoot = Path.Combine(payloadRoot, "runtime"),
                DevBridgePackageSha256 = candidate.DevBridgePackageSha256,
                DevBridgeCoordinatorSha256 = candidate.DevBridgeCoordinatorSha256,
                TransactionConsumerPath = Path.Combine(payloadRoot, "transaction-components", "mod-test.ps1"),
                TransactionConsumerRelativePath = "transaction-components/mod-test.ps1",
                TransactionConsumerSha256 = candidate.TransactionConsumerSha256,
                UnifiedManifestRelativePath = "unified-package.json",
                DevBridgeModSha256 = candidate.DevBridgeModSha256,
                DevBridgeRuntimeManifestSha256 = candidate.DevBridgeRuntimeManifestSha256,
                RuntimeProtocolContract = candidate.RuntimeProtocolContract,
                RimLiaisonExecutableSha256 = executableHash,
                RimLiaisonAssemblySha256 = assemblyHash
            };
            using (FileStream stream = new(
                       packageTemporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                JsonSerializer.Serialize(stream, package, WriteOptions);
                stream.Flush(flushToDisk: true);
            }

            if (Directory.Exists(payloadRoot))
            {
                if (!PayloadDirectoriesMatch(payloadStageRoot, payloadRoot))
                {
                    throw new InvalidDataException("The immutable qualified recovery payload contains a substituted artifact.");
                }
                TryDelete(payloadStageRoot);
            }
            else
            {
                try
                {
                    Directory.Move(payloadStageRoot, payloadRoot);
                    payloadPublishedByThisCall = true;
                }
                catch (IOException) when (Directory.Exists(payloadRoot))
                {
                    if (!PayloadDirectoriesMatch(payloadStageRoot, payloadRoot))
                    {
                        throw new InvalidDataException("The immutable qualified recovery payload contains a substituted artifact.");
                    }
                    TryDelete(payloadStageRoot);
                }
            }

            File.Move(packageTemporaryPath, fullPackagePath, overwrite: false);
            return fullPackagePath;
        }
        catch
        {
            TryDelete(packageTemporaryPath);
            TryDelete(payloadStageRoot);
            if (payloadPublishedByThisCall && !File.Exists(fullPackagePath))
            {
                TryDelete(payloadRoot);
            }
            throw;
        }
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
        if (string.Equals(package.SchemaVersion, ToolchainPromotionSchemas.Package, StringComparison.Ordinal))
        {
            string? cliRoot = SafeArtifactPath(artifactRoot, package.RimLiaisonCliDeploymentRootRelativePath);
            string? cliManifest = SafeArtifactPath(artifactRoot, package.RimLiaisonCliDeploymentManifestRelativePath);
            string? cliError = null;
            if (cliRoot is null || cliManifest is null ||
                !CliDeploymentManifestService.Verify(
                    cliRoot,
                    cliManifest,
                    package.RimLiaisonCliDeploymentManifestSha256,
                    package.RimLiaisonCliDeploymentPackageSha256,
                    out _,
                    out cliError))
            {
                error = cliError ?? "The promotion package durable CLI deployment is incomplete.";
                return false;
            }
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
                package.DevBridgeCoordinatorSha256,
                package.DevBridgeModSha256,
                package.DevBridgeRuntimeManifestSha256,
                package.SourceCommit))
        {
            error = "The promotion package durable DevBridge runtime payload is missing or has mismatching hashes.";
            return false;
        }
        return true;
    }

    private static bool RuntimeSnapshotIsVerified(
        string runtimeRoot,
        string? expectedPackageHash,
        string? expectedCoordinatorHash,
        string? expectedModHash = null,
        string? expectedManifestHash = null,
        string? expectedSourceCommit = null)
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
            JsonElement root = document.RootElement;
            bool unified = expectedModHash is not null || expectedManifestHash is not null ||
                expectedSourceCommit is not null;
            if (!root.TryGetProperty("files", out JsonElement files) ||
                files.ValueKind != JsonValueKind.Array ||
                unified &&
                (!root.TryGetProperty("ownerProduct", out JsonElement owner) ||
                 !string.Equals(owner.GetString(), ToolchainPromotionSchemas.OwnerProduct,
                     StringComparison.Ordinal)) ||
                expectedSourceCommit is not null &&
                (!root.TryGetProperty("sourceCommit", out JsonElement sourceCommit) ||
                 !string.Equals(sourceCommit.GetString(), expectedSourceCommit,
                     StringComparison.OrdinalIgnoreCase)))
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
            string coordinatorHash = entries.FirstOrDefault(entry =>
                string.Equals(entry.Path, "Coordinator/DevBridge.Coordinator.exe",
                    StringComparison.OrdinalIgnoreCase)).Sha256;
            string modHash = entries.FirstOrDefault(entry =>
                string.Equals(entry.Path, "1.6/Assemblies/DevBridge2.dll",
                    StringComparison.OrdinalIgnoreCase)).Sha256;
            return string.Equals(
                    ReadRuntimePackageHash(manifestPath),
                    expectedPackageHash,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(coordinatorHash, expectedCoordinatorHash,
                    StringComparison.OrdinalIgnoreCase) &&
                (expectedModHash is null ||
                 string.Equals(modHash, expectedModHash, StringComparison.OrdinalIgnoreCase)) &&
                (expectedManifestHash is null ||
                 string.Equals(ToolchainFileHash.Sha256(manifestPath), expectedManifestHash,
                     StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool RuntimeManifestMatches(
        string runtimeRoot,
        string expectedSourceCommit,
        string expectedProtocolContract)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(runtimeRoot, ".devbridge-runtime-manifest.json")));
        JsonElement root = document.RootElement;
        return root.TryGetProperty("schemaVersion", out JsonElement schema) &&
            string.Equals(schema.GetString(), "devbridge-runtime-manifest/v1", StringComparison.Ordinal) &&
            root.TryGetProperty("ownerProduct", out JsonElement owner) &&
            string.Equals(owner.GetString(), ToolchainPromotionSchemas.OwnerProduct, StringComparison.Ordinal) &&
            root.TryGetProperty("componentRole", out JsonElement role) &&
            string.Equals(role.GetString(), "DevBridge runtime", StringComparison.Ordinal) &&
            root.TryGetProperty("project", out JsonElement project) &&
            string.Equals(project.GetString(), ToolchainPromotionSchemas.OwnerProduct, StringComparison.Ordinal) &&
            root.TryGetProperty("packageId", out JsonElement packageId) &&
            string.Equals(packageId.GetString(), "lan.devbridge2", StringComparison.Ordinal) &&
            root.TryGetProperty("productionEligible", out JsonElement eligible) &&
            eligible.ValueKind == JsonValueKind.False &&
            root.TryGetProperty("sourceCommit", out JsonElement source) &&
            string.Equals(source.GetString(), expectedSourceCommit, StringComparison.OrdinalIgnoreCase) &&
            root.TryGetProperty("runtimeProtocolContract", out JsonElement protocol) &&
            string.Equals(protocol.GetString(), expectedProtocolContract, StringComparison.Ordinal);
    }

    private static bool SamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
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
        IGitRepositoryStateProvider? gitRepositoryStateProvider = null,
        IPromotionMachinePreflightVerifier? machinePreflightVerifier = null)
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
            if (string.IsNullOrWhiteSpace(package.DevBridgeModSha256) ||
                string.IsNullOrWhiteSpace(package.DevBridgeRuntimeManifestSha256))
            {
                return ToolchainPromotionResult.Blocked(
                    "PROMOTION_RUNTIME_OWNERSHIP_UNPROVEN",
                    "The promotion package does not bind the complete RimLiaison-owned runtime identity.",
                    package.SourceCommit,
                    nextAction: "Rebuild the promotion package from the unified RimLiaison-owned runtime candidate.");
            }
            if (string.Equals(package.SchemaVersion, ToolchainPromotionSchemas.Package, StringComparison.Ordinal) &&
                (string.IsNullOrWhiteSpace(package.RimLiaisonCliDeploymentRootRelativePath) ||
                 string.IsNullOrWhiteSpace(package.RimLiaisonCliDeploymentManifestRelativePath) ||
                 string.IsNullOrWhiteSpace(package.RimLiaisonCliDeploymentManifestSha256) ||
                 string.IsNullOrWhiteSpace(package.RimLiaisonCliDeploymentPackageSha256)))
            {
                return ToolchainPromotionResult.Blocked(
                    "PROMOTION_CLI_DEPLOYMENT_MANIFEST_MISSING",
                    "The new promotion package does not bind a complete CLI deployment manifest.",
                    package.SourceCommit,
                    artifactPath,
                    qualificationHash,
                    nextAction: "Rebuild the promotion package from the complete published RimLiaison CLI deployment.");
            }

            IGitRepositoryStateProvider sourceStateProvider = gitRepositoryStateProvider ??
                new SystemGitRepositoryStateProvider();
            GitRepositoryStateResult source = await sourceStateProvider
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

            IReadOnlyList<string> meaningfulDirtyPaths =
                RepositoryChangeClassificationPolicy.MeaningfulPaths(source.State.Changes);
            if (meaningfulDirtyPaths.Count > 0)
            {
                return ToolchainPromotionResult.Blocked(
                    "PROMOTION_SOURCE_DIRTY",
                    "Meaningful source or configuration changes remain after qualification.",
                    package.SourceCommit,
                    artifactPath,
                    qualificationHash,
                    nextAction: "Commit or restore the meaningful source changes, then rebuild the promotion package.",
                    meaningfulDirtyPaths: meaningfulDirtyPaths);
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
                    "Run the unified cross-stack compatibility gate and rebuild the unified promotion package.");
            }
            ProductionMachinePreflightResult machinePreflight =
                (machinePreflightVerifier ?? new ProductionMachinePreflightVerifier())
                .Verify(sourceRoot, previous.DevBridgeRuntimeRoot!);
            if (!machinePreflight.Passed)
            {
                return ToolchainPromotionResult.Blocked(
                    machinePreflight.ErrorCode ?? "RIMLIAISON_MACHINE_PREFLIGHT_FAILED",
                    machinePreflight.Error ?? "Production machine preflight did not pass.",
                    package.SourceCommit,
                    artifactPath,
                    qualificationHash,
                    previous.PromotedFingerprint,
                    nextAction: "Repair the production RimWorld installation/profile and retry promotion.",
                    productionDoctor: JsonSerializer.Serialize(machinePreflight, WriteOptions)) with
                {
                    MachinePreflight = machinePreflight
                };
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
            if (string.Equals(package.SchemaVersion, ToolchainPromotionSchemas.Package, StringComparison.Ordinal))
            {
                string? stagedCliRoot = SafeArtifactPath(
                    stagedRoot,
                    package.RimLiaisonCliDeploymentRootRelativePath);
                string? stagedCliManifest = SafeArtifactPath(
                    stagedRoot,
                    package.RimLiaisonCliDeploymentManifestRelativePath);
                string? stagedCliError = null;
                if (stagedCliRoot is null || stagedCliManifest is null ||
                    !CliDeploymentManifestService.Verify(
                        stagedCliRoot,
                        stagedCliManifest,
                        package.RimLiaisonCliDeploymentManifestSha256,
                        package.RimLiaisonCliDeploymentPackageSha256,
                        out _,
                        out stagedCliError))
                {
                    return ToolchainPromotionResult.Blocked(
                        "PROMOTION_CLI_DEPLOYMENT_VERIFY_FAILED",
                        stagedCliError ?? "The staged CLI deployment does not match the qualified closure.",
                        package.SourceCommit,
                        artifactPath,
                        qualificationHash,
                        previous.PromotedFingerprint,
                        "Rebuild the unified package from the complete published CLI deployment.");
                }
            }
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
                ToolchainPromotionSchemas.RuntimeSubsystem,
                package.DevBridgeModSha256,
                package.DevBridgeRuntimeManifestSha256,
                package.RimLiaisonCliDeploymentManifestSha256,
                package.RimLiaisonCliDeploymentPackageSha256);
            var unifiedManifest = new
            {
                schemaVersion = "rimliaison-unified-production-package/v3",
                productFingerprint = promotedFingerprint,
                ownerProduct = ToolchainPromotionSchemas.OwnerProduct,
                runtimeSubsystem = ToolchainPromotionSchemas.RuntimeSubsystem,
                rimBridgeServer = new
                {
                    boundary = ToolchainPromotionSchemas.RimBridgeServerBoundary,
                    ownership = "RimBridgeServer"
                },
                sourceCommit = sourceCommit,
                rimLiaison = new
                {
                    deploymentRoot = Path.GetRelativePath(stagedRoot, Path.GetDirectoryName(installedExecutable)!).Replace('\\', '/'),
                    deploymentManifestPath = Path.GetRelativePath(
                        stagedRoot,
                        Path.Combine(Path.GetDirectoryName(installedExecutable)!,
                            CliDeploymentManifestService.FileName)).Replace('\\', '/'),
                    deploymentManifestSha256 = package.RimLiaisonCliDeploymentManifestSha256,
                    deploymentPackageSha256 = package.RimLiaisonCliDeploymentPackageSha256,
                    targetFramework = package.RimLiaisonCliTargetFramework,
                    executablePath = Path.GetRelativePath(stagedRoot, installedExecutable).Replace('\\', '/'),
                    executableSha256 = installedExecutableHash,
                    assemblyPath = Path.GetRelativePath(stagedRoot, installedAssembly).Replace('\\', '/'),
                    assemblySha256 = installedAssemblyHash
                },
                runtimeManifestSha256 = package.DevBridgeRuntimeManifestSha256,
                modSha256 = package.DevBridgeModSha256,
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
            var healthBinding = new PromotionCandidateHealthBinding(
                installedExecutable,
                stagedRuntimeRoot!,
                promotedFingerprint,
                sourceCommit,
                package.DevBridgePackageSha256!,
                coordinatorHash,
                consumerHash,
                previousRuntimeProtocolContract,
                string.Empty)
            {
                RimLiaisonExecutableSha256 = installedExecutableHash,
                RimLiaisonAssemblySha256 = installedAssemblyHash,
                DevBridgeModSha256 = package.DevBridgeModSha256,
                DevBridgeRuntimeManifestSha256 = package.DevBridgeRuntimeManifestSha256,
                RimLiaisonCliDeploymentManifestSha256 = package.RimLiaisonCliDeploymentManifestSha256,
                RimLiaisonCliDeploymentPackageSha256 = package.RimLiaisonCliDeploymentPackageSha256
            };
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
                health = health with
                {
                    Error = evidenceError,
                    ErrorCode = health.ErrorCode ?? "PROMOTION_CANDIDATE_HEALTH_FAILED",
                    Evidence = health.Evidence with
                    {
                        Status = "failed",
                        Error = evidenceError,
                        NestedError = health.Error
                    }
                };
            }
            if (!health.Passed)
            {
                string healthErrorCode = health.ErrorCode ?? "PROMOTION_CANDIDATE_HEALTH_FAILED";
                WriteFailureHandoff(
                    packagePath,
                    package,
                    previous,
                    healthErrorCode,
                    health.Error ?? "The candidate health checks did not pass.",
                    "Repair the candidate package or runtime, then retry the supported promotion command.");
                return ToolchainPromotionResult.Blocked(
                    healthErrorCode,
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
                RimLiaisonCliDeploymentManifestSha256 = package.RimLiaisonCliDeploymentManifestSha256,
                RimLiaisonCliDeploymentPackageSha256 = package.RimLiaisonCliDeploymentPackageSha256,
                DevBridgeRuntimeRoot = previous.DevBridgeRuntimeRoot,
                DevBridgePackageSha256 = package.DevBridgePackageSha256,
                DevBridgeCoordinatorSha256 = coordinatorHash,
                DevBridgeModSha256 = package.DevBridgeModSha256,
                DevBridgeRuntimeManifestSha256 = package.DevBridgeRuntimeManifestSha256,
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
            var qualifiedHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["rimLiaisonExecutableSha256"] = executableHash,
                ["rimLiaisonAssemblySha256"] = assemblyHash,
                ["devBridgePackageSha256"] = package.DevBridgePackageSha256!,
                ["devBridgeCoordinatorSha256"] = coordinatorHash,
                ["transactionConsumerSha256"] = consumerHash
            };
            var promotedHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["rimLiaisonExecutableSha256"] = installedExecutableHash,
                ["rimLiaisonAssemblySha256"] = installedAssemblyHash,
                ["devBridgePackageSha256"] = package.DevBridgePackageSha256!,
                ["devBridgeCoordinatorSha256"] = coordinatorHash,
                ["transactionConsumerSha256"] = installedConsumerHash
            };
            if (package.RimLiaisonCliDeploymentManifestSha256 is not null &&
                package.RimLiaisonCliDeploymentPackageSha256 is not null)
            {
                qualifiedHashes["rimLiaisonCliDeploymentManifestSha256"] = package.RimLiaisonCliDeploymentManifestSha256;
                qualifiedHashes["rimLiaisonCliDeploymentPackageSha256"] = package.RimLiaisonCliDeploymentPackageSha256;
                promotedHashes["rimLiaisonCliDeploymentManifestSha256"] = package.RimLiaisonCliDeploymentManifestSha256;
                promotedHashes["rimLiaisonCliDeploymentPackageSha256"] = package.RimLiaisonCliDeploymentPackageSha256;
            }
            return new(
                ToolchainPromotionSchemas.Result,
                "promoted",
                null,
                null,
                package.SourceCommit,
                artifactPath,
                qualificationHash,
                qualifiedHashes,
                promotedHashes,
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
                package.SchemaVersion is not (ToolchainPromotionSchemas.Package or ToolchainPromotionSchemas.LegacyPackage) ||
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
        if (!string.IsNullOrWhiteSpace(package.DevBridgeModSha256) &&
            (!TryString(hashes, "devBridgeModSha256", out string? modHash) ||
             !string.Equals(modHash, package.DevBridgeModSha256, StringComparison.OrdinalIgnoreCase)) ||
            !string.IsNullOrWhiteSpace(package.DevBridgeRuntimeManifestSha256) &&
            (!TryString(hashes, "devBridgeRuntimeManifestSha256", out string? manifestHash) ||
             !string.Equals(manifestHash, package.DevBridgeRuntimeManifestSha256, StringComparison.OrdinalIgnoreCase)))
        {
            error = "The promotion package unified runtime hashes do not match the qualification artifact.";
            return false;
        }
        if (string.Equals(package.SchemaVersion, ToolchainPromotionSchemas.Package, StringComparison.Ordinal) &&
            (!TryString(hashes, "rimLiaisonCliDeploymentManifestSha256", out string? cliManifestHash) ||
             !TryString(hashes, "rimLiaisonCliDeploymentPackageSha256", out string? cliPackageHash) ||
             !string.Equals(cliManifestHash, package.RimLiaisonCliDeploymentManifestSha256,
                 StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(cliPackageHash, package.RimLiaisonCliDeploymentPackageSha256,
                 StringComparison.OrdinalIgnoreCase)))
        {
            error = "The complete CLI deployment hashes do not match the qualification artifact.";
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
            return null;
        string candidate = Path.GetFullPath(Path.Combine(root, relative));
        string boundary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(boundary, StringComparison.OrdinalIgnoreCase)
            ? candidate
            : null;
    }

    private static string ReadRuntimeFileHash(string runtimeManifestPath, string relativePath)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(runtimeManifestPath));
        if (!document.RootElement.TryGetProperty("files", out JsonElement files) ||
            files.ValueKind != JsonValueKind.Array)
            return string.Empty;
        foreach (JsonElement file in files.EnumerateArray())
        {
            if (file.TryGetProperty("path", out JsonElement path) &&
                string.Equals(path.GetString(), relativePath, StringComparison.OrdinalIgnoreCase) &&
                file.TryGetProperty("sha256", out JsonElement hash))
                return hash.GetString() ?? string.Empty;
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
    internal static string ComputeQualifiedPayloadIdentity(
        string qualificationArtifactSha256,
        string sourceCommit,
        string executableHash,
        string assemblyHash,
        string devBridgeHash,
        string coordinatorHash,
        string consumerHash,
        string compatibility,
        string? modHash = null,
        string? runtimeManifestHash = null,
        string? cliManifestHash = null,
        string? cliPackageHash = null)
    {
        var canonical = new StringBuilder();
        AppendPayloadIdentityField(canonical, "qualification", qualificationArtifactSha256);
        AppendPayloadIdentityField(canonical, "source", sourceCommit);
        AppendPayloadIdentityField(canonical, "executable", executableHash);
        AppendPayloadIdentityField(canonical, "assembly", assemblyHash);
        AppendPayloadIdentityField(canonical, "devbridge", devBridgeHash);
        AppendPayloadIdentityField(canonical, "coordinator", coordinatorHash);
        AppendPayloadIdentityField(canonical, "consumer", consumerHash);
        AppendPayloadIdentityField(canonical, "compatibility", compatibility);
        if (modHash is not null)
            AppendPayloadIdentityField(canonical, "mod", modHash);
        if (runtimeManifestHash is not null)
            AppendPayloadIdentityField(canonical, "runtimeManifest", runtimeManifestHash);
        if (cliManifestHash is not null)
            AppendPayloadIdentityField(canonical, "cliManifest", cliManifestHash);
        if (cliPackageHash is not null)
            AppendPayloadIdentityField(canonical, "cliPackage", cliPackageHash);
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
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
        string runtimeSubsystem,
        string? modHash = null,
        string? runtimeManifestHash = null,
        string? cliManifestHash = null,
        string? cliPackageHash = null)
    {
        string payload = string.Join("\n", new[]
        {
            ToolchainPromotionSchemas.Package,
            "unified-production-package/v3",
            ownerProduct,
            runtimeSubsystem,
            sourceCommit,
            executableHash,
            assemblyHash,
            coordinatorHash,
            devBridgeHash,
            consumerHash,
            compatibility,
            modHash ?? string.Empty,
            runtimeManifestHash ?? string.Empty,
            cliManifestHash ?? string.Empty,
            cliPackageHash ?? string.Empty
        });
        return "tc-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private static void AppendPayloadIdentityField(
        StringBuilder canonical,
        string name,
        string value)
    {
        canonical.Append(name.Length)
            .Append(':')
            .Append(name)
            .Append(value.Length)
            .Append(':')
            .Append(value)
            .Append('\n');
    }


    internal static async Task<PromotionCandidateHealthResult> RunCandidateHealthAsync(
        PromotionCandidateHealthBinding binding,
        string workflowId,
        CancellationToken cancellationToken,
        IPromotionLeaseOrchestrator? promotionLeaseOrchestrator = null,
        Action? afterCandidateCliProbe = null)
    {
        _ = workflowId;
        var checks = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["healthStage"] = "candidate-pre-commit",
            ["candidateExecutable"] = binding.CandidateExecutable,
            ["candidateRuntimeRoot"] = binding.CandidateRuntimeRoot,
            ["candidateFingerprint"] = binding.CandidateFingerprint,
            ["candidateSourceCommit"] = binding.CandidateSourceCommit,
            ["devBridgePackageSha256"] = binding.DevBridgePackageSha256,
            ["devBridgeCoordinatorSha256"] = binding.DevBridgeCoordinatorSha256,
            ["devBridgeModSha256"] = binding.DevBridgeModSha256 ?? "unbound",
            ["devBridgeRuntimeManifestSha256"] = binding.DevBridgeRuntimeManifestSha256 ?? "unbound",
            ["transactionConsumerSha256"] = binding.TransactionConsumerSha256,
            ["runtimeProtocolContract"] = binding.RuntimeProtocolContract,
            ["rimWorldExecutable"] = "not-used",
            ["devBridgeRestart"] = "not-run",
            ["devBridgeStatus"] = "not-run",
            ["devBridgeDoctor"] = "not-run",
            ["capabilities"] = "not-run",
            ["coordinatorShutdown"] = "not-run",
            ["coordinatorQuiesced"] = "not-run"
        };
        try
        {
            if (!File.Exists(binding.CandidateExecutable))
                return CandidateHealthFailure(checks, binding, "The candidate RimLiaison executable is missing.");
            if (!Directory.Exists(binding.CandidateRuntimeRoot))
                return CandidateHealthFailure(checks, binding, "The candidate RimLiaison runtime directory is missing.");
            if (binding.RimLiaisonExecutableSha256 is not null &&
                !HashMatches(binding.CandidateExecutable, binding.RimLiaisonExecutableSha256))
                return CandidateHealthFailure(checks, binding, "The candidate RimLiaison executable hash does not match its binding.");
            if (binding.RimLiaisonAssemblySha256 is not null &&
                !HashMatches(Path.Combine(Path.GetDirectoryName(binding.CandidateExecutable)!,
                    "rimliaison.dll"), binding.RimLiaisonAssemblySha256))
                return CandidateHealthFailure(checks, binding, "The candidate RimLiaison assembly hash does not match its binding.");

            string cliRoot = Path.GetDirectoryName(binding.CandidateExecutable)!;
            string cliManifestPath = Path.Combine(cliRoot, CliDeploymentManifestService.FileName);
            string? cliError = null;
            CliDeploymentManifest? cliManifest = null;
            bool cliVerified = !string.IsNullOrWhiteSpace(binding.RimLiaisonCliDeploymentManifestSha256) &&
                !string.IsNullOrWhiteSpace(binding.RimLiaisonCliDeploymentPackageSha256) &&
                CliDeploymentManifestService.Verify(
                    cliRoot,
                    cliManifestPath,
                    binding.RimLiaisonCliDeploymentManifestSha256,
                    binding.RimLiaisonCliDeploymentPackageSha256,
                    out cliManifest,
                    out cliError);
            if (!cliVerified ||
                cliManifest is null ||
                !string.Equals(cliManifest.SourceCommit, binding.CandidateSourceCommit,
                    StringComparison.OrdinalIgnoreCase))
            {
                return CandidateHealthFailure(
                    checks,
                    binding,
                    cliError ?? "The complete candidate CLI deployment is not verified.");
            }

            if (!RuntimeSnapshotIsVerified(
                    binding.CandidateRuntimeRoot,
                    binding.DevBridgePackageSha256,
                    binding.DevBridgeCoordinatorSha256,
                    binding.DevBridgeModSha256,
                    binding.DevBridgeRuntimeManifestSha256,
                    binding.CandidateSourceCommit) ||
                !RuntimeManifestMatches(
                    binding.CandidateRuntimeRoot,
                    binding.CandidateSourceCommit,
                    binding.RuntimeProtocolContract))
                return CandidateHealthFailure(checks, binding, "The candidate runtime manifest or immutable file hashes are invalid.");

            string[] consumerCandidates =
            [
                Path.Combine(binding.CandidateRuntimeRoot, "transaction-components", "mod-test.ps1"),
                Path.Combine(cliRoot, "transaction-components", "mod-test.ps1"),
                Path.Combine(cliRoot, "..", "transaction-components", "mod-test.ps1")
            ];
            string consumerPath = consumerCandidates.FirstOrDefault(File.Exists) ?? consumerCandidates[0];
            if (!HashMatches(consumerPath, binding.TransactionConsumerSha256))
                return CandidateHealthFailure(checks, binding, "The candidate transaction consumer is missing or has a mismatching hash.");

            PromotionChildProcessResult probe = await RunJsonCommandAsync(
                    binding.CandidateExecutable,
                    ["self-check", "--json"],
                    cancellationToken,
                    workingDirectory: cliRoot)
                .ConfigureAwait(false);
            checks["candidateCliProbe"] = IsSelfCheckReady(probe.ExitCode, probe.Stdout)
                ? "passed"
                : "failed";
            if (!IsSelfCheckReady(probe.ExitCode, probe.Stdout))
            {
                PromotionCandidateHealthResult failure = CandidateHealthFailure(
                    checks,
                    binding,
                    "The complete candidate CLI failed its executable self-check.",
                    JsonSerializer.Serialize(probe, WriteOptions));
                return failure with
                {
                    ErrorCode = "PROMOTION_CANDIDATE_CLI_START_FAILED",
                    Evidence = failure.Evidence with { ProcessEvidence = probe }
                };
            }

            afterCandidateCliProbe?.Invoke();
            string? postProbeCliError;
            CliDeploymentManifest? postProbeCliManifest;
            bool postProbeCliVerified = CliDeploymentManifestService.Verify(
                cliRoot,
                cliManifestPath,
                binding.RimLiaisonCliDeploymentManifestSha256,
                binding.RimLiaisonCliDeploymentPackageSha256,
                out postProbeCliManifest,
                out postProbeCliError);
            if (!postProbeCliVerified ||
                postProbeCliManifest is null ||
                !string.Equals(postProbeCliManifest.SourceCommit, binding.CandidateSourceCommit,
                    StringComparison.OrdinalIgnoreCase))
            {
                checks["candidateCliClosure"] = "failed";
                PromotionCandidateHealthResult failure = CandidateHealthFailure(
                    checks,
                    binding,
                    postProbeCliError ??
                        "The candidate CLI deployment changed during its executable self-check.",
                    JsonSerializer.Serialize(
                        new
                        {
                            selfCheck = probe,
                            postSelfCheckVerification = postProbeCliError
                        },
                        WriteOptions));
                return failure with
                {
                    ErrorCode = "PROMOTION_CANDIDATE_CLI_MUTATED",
                    Evidence = failure.Evidence with { ProcessEvidence = probe }
                };
            }

            checks["candidateCliClosure"] = "verified";
            checks["runtimeManifest"] = "verified";
            checks["runtimeFiles"] = "verified";
            checks["transactionConsumer"] = "verified";
            checks["candidateArtifacts"] = "verified";
            checks["status"] = "ready";
            return CandidateHealthSuccess(checks, binding);
        }
        catch (Exception exception) when (exception is IOException or JsonException or
            InvalidOperationException or UnauthorizedAccessException)
        {
            return CandidateHealthFailure(checks, binding, exception.Message, exception.ToString());
        }
    }
    private static bool IsSelfCheckReady(int exitCode, string stdout)
    {
        if (exitCode != 0 || !TryParse(stdout, out JsonDocument? document))
            return false;
        using (document)
        {
            JsonElement root = document!.RootElement;
            return root.TryGetProperty("schemaVersion", out JsonElement schema) &&
                string.Equals(schema.GetString(), "rimliaison-self-check/v1", StringComparison.Ordinal) &&
                root.TryGetProperty("status", out JsonElement status) &&
                string.Equals(status.GetString(), "ready", StringComparison.OrdinalIgnoreCase);
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
            nestedError)
        {
            DevBridgeModSha256 = binding.DevBridgeModSha256,
            DevBridgeRuntimeManifestSha256 = binding.DevBridgeRuntimeManifestSha256,
            RimLiaisonCliDeploymentManifestSha256 = binding.RimLiaisonCliDeploymentManifestSha256,
            RimLiaisonCliDeploymentPackageSha256 = binding.RimLiaisonCliDeploymentPackageSha256
        };

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
            !string.Equals(evidence.RuntimeProtocolContract, binding.RuntimeProtocolContract, StringComparison.Ordinal) ||
            !string.Equals(evidence.RimLiaisonCliDeploymentManifestSha256,
                binding.RimLiaisonCliDeploymentManifestSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(evidence.RimLiaisonCliDeploymentPackageSha256,
                binding.RimLiaisonCliDeploymentPackageSha256, StringComparison.OrdinalIgnoreCase) ||
            binding.DevBridgeModSha256 is not null &&
            !string.Equals(evidence.DevBridgeModSha256, binding.DevBridgeModSha256, StringComparison.OrdinalIgnoreCase) ||
            binding.DevBridgeRuntimeManifestSha256 is not null &&
            !string.Equals(evidence.DevBridgeRuntimeManifestSha256, binding.DevBridgeRuntimeManifestSha256, StringComparison.OrdinalIgnoreCase))
        {
            error = "Candidate health did not prove the exact staged candidate identity.";
            return false;
        }
        return true;
    }



    internal static bool IsCoordinatorQuiesced(
        JsonElement probeRoot,
        string expectedRuntimeRoot,
        int probeExitCode)
    {
        return probeExitCode != 0 &&
            probeRoot.TryGetProperty("state", out JsonElement state) &&
            string.Equals(state.GetString(), "Absent", StringComparison.OrdinalIgnoreCase) &&
            probeRoot.TryGetProperty("runtimeRoot", out JsonElement runtimeRoot) &&
            runtimeRoot.ValueKind == JsonValueKind.String &&
            SamePath(runtimeRoot.GetString(), expectedRuntimeRoot) &&
            (!probeRoot.TryGetProperty("coordinatorPid", out JsonElement pid) ||
                pid.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined);
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

        PromotionChildProcessResult doctor = await RunJsonCommandAsync(
                executable,
                ["doctor", "--json"],
                cancellationToken,
                workingDirectory: sourceRoot)
            .ConfigureAwait(false);
        bool ready = IsReady(doctor.ExitCode, doctor.Stdout);
        if (ready)
        {
            return new(true, doctor.Stdout, null)
            {
                ProcessEvidence = doctor
            };
        }

        bool structuredStdout = TryParse(doctor.Stdout, out JsonDocument? parsed);
        parsed?.Dispose();
        if (structuredStdout)
        {
            return new(
                false,
                doctor.Stdout,
                "Canonical post-commit doctor did not report READY.")
            {
                ErrorCode = "PROMOTION_POST_COMMIT_HEALTH_FAILED",
                ProcessEvidence = doctor
            };
        }

        string nestedCode = doctor.StartError is not null || doctor.ExitCode != 0
            ? "POST_COMMIT_CLI_START_FAILED"
            : "POST_COMMIT_CLI_NO_STRUCTURED_OUTPUT";
        string summary = JsonSerializer.Serialize(
            new
            {
                schemaVersion = "rimliaison-promotion-health/v1",
                status = "blocked",
                errorCode = nestedCode,
                childProcess = doctor
            },
            WriteOptions);
        return new(
            false,
            summary,
            "Canonical post-commit doctor did not report READY.")
        {
            ErrorCode = "PROMOTION_POST_COMMIT_HEALTH_FAILED",
            ProcessEvidence = doctor
        };
    }

    internal static async Task<PromotionChildProcessResult> RunJsonCommandAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null,
        string? workingDirectory = null)
    {
        const int MaximumStdoutCharacters = 512 * 1024;
        const int MaximumStderrCharacters = 16 * 1024;
        string resolvedWorkingDirectory = workingDirectory ?? Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory;
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = resolvedWorkingDirectory,
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
        try
        {
            if (!process.Start())
            {
                return new(-1, string.Empty, string.Empty, false, false, executable,
                    resolvedWorkingDirectory, "The child process could not be started.");
            }
        }
        catch (Win32Exception exception)
        {
            return new(-1, string.Empty, string.Empty, false, false, executable,
                resolvedWorkingDirectory, exception.Message);
        }

        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        bool timedOut = false;
        bool cancelled = false;
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            cancelled = cancellationToken.IsCancellationRequested;
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
        return new(
            process.HasExited ? process.ExitCode : -1,
            BoundProcessOutput(outputTask.Result, MaximumStdoutCharacters),
            BoundProcessOutput(errorTask.Result, MaximumStderrCharacters),
            timedOut,
            cancelled,
            executable,
            resolvedWorkingDirectory);
    }

    private static string BoundProcessOutput(string value, int maximumCharacters)
    {
        string trimmed = value?.Trim() ?? string.Empty;
        return trimmed.Length <= maximumCharacters
            ? trimmed
            : trimmed[..maximumCharacters] + " [truncated]";
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

    private static bool IsCapabilitiesReady(int exitCode, string output)
    {
        if (exitCode != 0 ||
            !TryParse(output, out JsonDocument? document))
        {
            return false;
        }

        using (document)
        {
            JsonElement root = document!.RootElement;
            return root.TryGetProperty("schemaVersion", out JsonElement schema) &&
                string.Equals(schema.GetString(), DevBridgeCapabilitySchemas.Output, StringComparison.Ordinal) &&
                root.TryGetProperty("status", out JsonElement status) &&
                string.Equals(status.GetString(), "ok", StringComparison.OrdinalIgnoreCase) &&
                root.TryGetProperty("capabilities", out JsonElement capabilities) &&
                capabilities.ValueKind == JsonValueKind.Array;
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
    private static bool PayloadDirectoriesMatch(string expected, string actual)
    {
        string[] expectedFiles = Directory.EnumerateFiles(expected, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(expected, path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[] actualFiles = Directory.EnumerateFiles(actual, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(actual, path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!expectedFiles.SequenceEqual(actualFiles, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (string relativePath in expectedFiles)
        {
            string expectedPath = Path.Combine(expected, relativePath);
            string actualPath = Path.Combine(actual, relativePath);
            if (!string.Equals(
                    ToolchainFileHash.Sha256(expectedPath),
                    ToolchainFileHash.Sha256(actualPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
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
    string? Error)
{
    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; init; }
    [JsonPropertyName("processEvidence")]
    public PromotionChildProcessResult? ProcessEvidence { get; init; }
}

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
