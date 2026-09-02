using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimLiaison.Toolchain;

internal enum ToolchainMode
{
    Production,
    Experimental
}

public sealed record ProductionToolchainBinding(
    string Fingerprint,
    string PromotedFingerprint,
    string OwnerProduct,
    string RuntimeSubsystem,
    string RimLiaisonExecutablePath,
    string RimLiaisonExecutableHash,
    string RimLiaisonAssemblyPath,
    string RimLiaisonAssemblyHash,
    string DevBridgeCommandPath,
    string DevBridgeRuntimeRoot,
    string DevBridgePackageHash,
    string DevBridgeCoordinatorHash,
    string TransactionConsumerPath,
    string TransactionConsumerHash,
    string UnifiedManifestPath,
    string UnifiedManifestHash,
    string RuntimeProtocolContract)
{
    public string? RimLiaisonCliDeploymentManifestHash { get; init; }
    public string? RimLiaisonCliDeploymentPackageHash { get; init; }
    public string? DevBridgeModHash { get; init; }
    public string? DevBridgeRuntimeManifestHash { get; init; }
    public object ToEvidence() => new
    {
        mode = "production",
        fingerprint = Fingerprint,
        promotedFingerprint = PromotedFingerprint,
        ownerProduct = OwnerProduct,
        runtimeSubsystem = RuntimeSubsystem,
        rimLiaison = new
        {
            executablePath = RimLiaisonExecutablePath,
            executableSha256 = RimLiaisonExecutableHash,
            assemblyPath = RimLiaisonAssemblyPath,
            assemblySha256 = RimLiaisonAssemblyHash,
            cliDeploymentManifestSha256 = RimLiaisonCliDeploymentManifestHash,
            cliDeploymentPackageSha256 = RimLiaisonCliDeploymentPackageHash
        },
        runtime = new
        {
            commandPath = DevBridgeCommandPath,
            runtimeRoot = DevBridgeRuntimeRoot,
            packageSha256 = DevBridgePackageHash,
            coordinatorSha256 = DevBridgeCoordinatorHash,
            modSha256 = DevBridgeModHash,
            manifestSha256 = DevBridgeRuntimeManifestHash
        },
        rimBridgeServer = new
        {
            boundary = ToolchainPromotionSchemas.RimBridgeServerBoundary,
            ownership = "RimBridgeServer"
        },
        transactionConsumer = new
        {
            path = TransactionConsumerPath,
            sha256 = TransactionConsumerHash
        },
        unifiedManifest = new
        {
            path = UnifiedManifestPath,
            sha256 = UnifiedManifestHash
        },
        runtimeProtocolContract = RuntimeProtocolContract
    };
}

internal sealed record ProductionToolchainBindingFailure(
    string ErrorCode,
    string Error,
    string NextAction,
    IReadOnlyList<string> RejectedCandidates,
    string? ExpectedFingerprint = null,
    string? CurrentExecutablePath = null,
    string? DevBridgeRuntimeRoot = null,
    string? ManifestPath = null,
    IReadOnlyList<string>? ExpectedArtifacts = null,
    IReadOnlyList<string>? MismatchingArtifacts = null)
{
    public object ToEvidence() => new
    {
        mode = "production",
        errorCode = ErrorCode,
        error = Error,
        nextAction = NextAction,
        rejectedCandidates = RejectedCandidates,
        expectedFingerprint = ExpectedFingerprint,
        currentExecutablePath = CurrentExecutablePath,
        devBridgeRuntimeRoot = DevBridgeRuntimeRoot,
        manifestPath = ManifestPath,
        expectedArtifacts = ExpectedArtifacts,
        mismatchingArtifacts = MismatchingArtifacts
    };
}

internal sealed record ProductionToolchainBindingResolution(
    ProductionToolchainBinding? Binding,
    ProductionToolchainBindingFailure? Failure)
{
    public bool Succeeded => Binding is not null;
}

internal sealed class ProductionToolchainManifest
{
    [JsonPropertyName("schemaVersion")]
    public string? SchemaVersion { get; init; }
    [JsonPropertyName("productionState")]
    public string? ProductionState { get; init; }
    [JsonPropertyName("bootstrapStatus")]
    public string? BootstrapStatus { get; init; }
    [JsonPropertyName("bootstrapErrorCode")]
    public string? BootstrapErrorCode { get; init; }
    [JsonPropertyName("bootstrapError")]
    public string? BootstrapError { get; init; }
    [JsonPropertyName("bootstrapArchivePath")]
    public string? BootstrapArchivePath { get; init; }
    [JsonPropertyName("promotedFingerprint")]
    public string? PromotedFingerprint { get; init; }
    [JsonPropertyName("fingerprint")]
    public string? Fingerprint { get; init; }
    [JsonPropertyName("ownerProduct")]
    public string? OwnerProduct { get; init; }
    [JsonPropertyName("runtimeSubsystem")]
    public string? RuntimeSubsystem { get; init; }
    [JsonPropertyName("rimLiaisonExecutablePath")]
    public string? RimLiaisonExecutablePath { get; init; }
    [JsonPropertyName("rimLiaisonExecutableSha256")]
    public string? RimLiaisonExecutableSha256 { get; init; }
    [JsonPropertyName("rimLiaisonAssemblyPath")]
    public string? RimLiaisonAssemblyPath { get; init; }
    [JsonPropertyName("rimLiaisonAssemblySha256")]
    public string? RimLiaisonAssemblySha256 { get; init; }
    [JsonPropertyName("rimLiaisonCliDeploymentManifestSha256")]
    public string? RimLiaisonCliDeploymentManifestSha256 { get; init; }
    [JsonPropertyName("rimLiaisonCliDeploymentPackageSha256")]
    public string? RimLiaisonCliDeploymentPackageSha256 { get; init; }
    [JsonPropertyName("devBridgeRuntimeRoot")]
    public string? DevBridgeRuntimeRoot { get; init; }
    [JsonPropertyName("devBridgePackageSha256")]
    public string? DevBridgePackageSha256 { get; init; }
    [JsonPropertyName("transactionConsumerPath")]
    public string? TransactionConsumerPath { get; init; }
    [JsonPropertyName("transactionConsumerSha256")]
    public string? TransactionConsumerSha256 { get; init; }
    [JsonPropertyName("runtimeProtocolContract")]
    public string? RuntimeProtocolContract { get; init; }
    [JsonPropertyName("qualifiedSourceCommit")]
    public string? QualifiedSourceCommit { get; init; }
    [JsonPropertyName("qualificationArtifactPath")]
    public string? QualificationArtifactPath { get; init; }
    [JsonPropertyName("qualificationArtifactSha256")]
    public string? QualificationArtifactSha256 { get; init; }
    [JsonPropertyName("devBridgeCoordinatorSha256")]
    public string? DevBridgeCoordinatorSha256 { get; init; }
    [JsonPropertyName("devBridgeModSha256")]
    public string? DevBridgeModSha256 { get; init; }
    [JsonPropertyName("devBridgeRuntimeManifestSha256")]
    public string? DevBridgeRuntimeManifestSha256 { get; init; }
    [JsonPropertyName("unifiedManifestPath")]
    public string? UnifiedManifestPath { get; init; }
    [JsonPropertyName("unifiedManifestSha256")]
    public string? UnifiedManifestSha256 { get; init; }
    [JsonPropertyName("promotionPackagePath")]
    public string? PromotionPackagePath { get; init; }
    [JsonPropertyName("promotionPackageSha256")]
    public string? PromotionPackageSha256 { get; init; }
}

internal static class ProductionToolchainBindingResolver
{
    private const string ManifestSchema = "rimliaison-production-toolchain/v1";
    private const string RuntimeProtocolContract = "devbridge-mod-development/v1";
    private const string ManifestEnvironment = "RIMLIAISON_PRODUCTION_TOOLCHAIN_MANIFEST";
    private const string CliEnvironment = "RIMLIAISON_PRODUCTION_CLI";

    public static ProductionToolchainBindingResolution Resolve(
        string repositoryRoot,
        string? requestedCliPath = null,
        string? requestedDevBridgePath = null,
        string? requestedDevBridgeRoot = null,
        string? currentExecutablePath = null) =>
        ResolveCore(
            repositoryRoot,
            requestedCliPath,
            requestedDevBridgePath,
            requestedDevBridgeRoot,
            currentExecutablePath,
            requireCurrentExecutable: true);

    /// <summary>
    /// Verifies the already-promoted production identity without consulting the
    /// caller's executable, source checkout, or source-checkout environment.
    /// This is the only binding operation allowed during restoration.
    /// </summary>
    internal static ProductionToolchainBindingResolution ResolvePromotedIdentity(
        string repositoryRoot) =>
        ResolveCore(
            repositoryRoot,
            requestedCliPath: null,
            requestedDevBridgePath: null,
            requestedDevBridgeRoot: null,
            currentExecutablePath: null,
            requireCurrentExecutable: false);

    private static ProductionToolchainBindingResolution ResolveCore(
        string _repositoryRoot,
        string? requestedCliPath,
        string? requestedDevBridgePath,
        string? requestedDevBridgeRoot,
        string? currentExecutablePath,
        bool requireCurrentExecutable)
    {
        var rejected = new List<string>();
        string? manifestPath = Environment.GetEnvironmentVariable(ManifestEnvironment);
        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            string? configuredRoot = Environment.GetEnvironmentVariable("RIMLIAISON_PRODUCTION_ROOT");
            manifestPath = string.IsNullOrWhiteSpace(configuredRoot)
                ? Path.Combine("C:\\RimDev", ".rimdev", "production-toolchain.json")
                : Path.Combine(configuredRoot, "production-toolchain.json");
        }

        if (!TryReadManifest(manifestPath, out ProductionToolchainManifest? manifest, out string? manifestError))
        {
            return Fail(
                "PRODUCTION_TOOLCHAIN_MANIFEST_MISSING",
                manifestError ?? "The promoted production toolchain manifest could not be loaded.",
                "Install and promote the unified production toolchain, then retry.",
                rejected,
                manifestPath);
        }
        if (string.Equals(manifest!.ProductionState, "NO_PRODUCTION", StringComparison.Ordinal))
        {
            return Fail(
                "PRODUCTION_TOOLCHAIN_NO_PRODUCTION",
                manifest.BootstrapStatus == "BOOTSTRAP_FAILED"
                    ? manifest.BootstrapError ?? "The production toolchain bootstrap failed."
                    : "No production toolchain is authoritative.",
                "Run the explicit qualified bootstrap promotion for the current source.",
                rejected,
                manifestPath);
        }


        if (!string.Equals(manifest!.SchemaVersion, ManifestSchema, StringComparison.Ordinal) ||
            !string.Equals(manifest.OwnerProduct, ToolchainPromotionSchemas.OwnerProduct, StringComparison.Ordinal) ||
            !string.Equals(manifest.RuntimeSubsystem, ToolchainPromotionSchemas.RuntimeSubsystem, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(manifest.RimLiaisonExecutablePath) ||
            string.IsNullOrWhiteSpace(manifest.RimLiaisonExecutableSha256) ||
            string.IsNullOrWhiteSpace(manifest.RimLiaisonAssemblyPath) ||
            string.IsNullOrWhiteSpace(manifest.RimLiaisonAssemblySha256) ||
            string.IsNullOrWhiteSpace(manifest.DevBridgeRuntimeRoot) ||
            string.IsNullOrWhiteSpace(manifest.DevBridgePackageSha256) ||
            string.IsNullOrWhiteSpace(manifest.DevBridgeCoordinatorSha256) ||
            string.IsNullOrWhiteSpace(manifest.TransactionConsumerPath) ||
            string.IsNullOrWhiteSpace(manifest.TransactionConsumerSha256) ||
            string.IsNullOrWhiteSpace(manifest.UnifiedManifestPath) ||
            string.IsNullOrWhiteSpace(manifest.UnifiedManifestSha256) ||
            !string.Equals(manifest.RuntimeProtocolContract, RuntimeProtocolContract, StringComparison.Ordinal))
        {
            return Fail(
                "PRODUCTION_TOOLCHAIN_MANIFEST_INVALID",
                "The unified production toolchain manifest is incomplete or incompatible.",
                "Regenerate the production manifest from the unified promoted package.",
                rejected,
                manifestPath,
                manifest.PromotedFingerprint);
        }

        string cliPath = FullPath(manifest.RimLiaisonExecutablePath!);
        string assemblyPath = FullPath(manifest.RimLiaisonAssemblyPath!);
        string currentCli = cliPath;
        if (requireCurrentExecutable)
        {
            string? configuredCli = Environment.GetEnvironmentVariable(CliEnvironment);
            if (!string.IsNullOrWhiteSpace(configuredCli) && !SamePath(configuredCli, cliPath))
            {
                rejected.Add(FullPath(configuredCli));
                return Fail(
                    "PRODUCTION_TOOLCHAIN_OVERRIDE_REJECTED",
                    "A production CLI override does not match the unified promoted CLI.",
                    "Use the promoted CLI recorded by the production manifest.",
                    rejected,
                    manifestPath,
                    manifest.PromotedFingerprint,
                    configuredCli);
            }

            currentCli = FullPath(currentExecutablePath ?? Environment.ProcessPath ?? string.Empty);
            if (!SamePath(currentCli, cliPath))
            {
                rejected.Add(currentCli);
                return Fail(
                    "PRODUCTION_TOOLCHAIN_SOURCE_FALLBACK",
                    "Production execution was requested from a non-promoted RimLiaison executable.",
                    "Invoke the immutable promoted RimLiaison executable; use --experimental only for qualification or tooling work.",
                    rejected,
                    manifestPath,
                    manifest.PromotedFingerprint,
                    currentCli);
            }
        }


        string runtimeRoot = FullPath(manifest.DevBridgeRuntimeRoot!);
        if (!string.IsNullOrWhiteSpace(requestedDevBridgeRoot) &&
            !SamePath(requestedDevBridgeRoot, runtimeRoot))
        {
            rejected.Add(FullPath(requestedDevBridgeRoot));
            return Fail(
                "PRODUCTION_TOOLCHAIN_OVERRIDE_REJECTED",
                "The requested DevBridge root is not the unified promoted runtime root.",
                "Use the installed DevBridge runtime root recorded by the production manifest.",
                rejected,
                manifestPath,
                manifest.PromotedFingerprint,
                currentCli,
                runtimeRoot);
        }

        string commandPath = Path.Combine(runtimeRoot, "DevBridge.cmd");
        if (!string.IsNullOrWhiteSpace(requestedDevBridgePath) &&
            !SamePath(requestedDevBridgePath, commandPath))
        {
            rejected.Add(FullPath(requestedDevBridgePath));
            return Fail(
                "PRODUCTION_TOOLCHAIN_OVERRIDE_REJECTED",
                "The requested DevBridge command is not the unified promoted runtime command.",
                "Use the installed DevBridge.cmd recorded by the production manifest.",
                rejected,
                manifestPath,
                manifest.PromotedFingerprint,
                currentCli,
                runtimeRoot);
        }

        string consumerPath = FullPath(manifest.TransactionConsumerPath!);
        string unifiedManifestPath = FullPath(manifest.UnifiedManifestPath!);
        string packageRoot = FullPath(Path.GetDirectoryName(unifiedManifestPath) ?? string.Empty);
        string[] expectedArtifacts =
        [
            cliPath,
            assemblyPath,
            commandPath,
            consumerPath,
            unifiedManifestPath
        ];
        string[] missingArtifacts = expectedArtifacts
            .Where(static path => !File.Exists(path))
            .ToArray();
        if (missingArtifacts.Length > 0)
        {
            return Fail(
                "PRODUCTION_TOOLCHAIN_ARTIFACT_MISSING",
                "A unified production toolchain artifact is missing.",
                "Repair or reinstall the unified promoted production package.",
                rejected,
                manifestPath,
                manifest.PromotedFingerprint,
                currentCli,
                runtimeRoot,
                expectedArtifacts,
                missingArtifacts);
        }
        if (!string.IsNullOrWhiteSpace(manifest.RimLiaisonCliDeploymentManifestSha256) ||
            !string.IsNullOrWhiteSpace(manifest.RimLiaisonCliDeploymentPackageSha256))
        {
            string cliRoot = Path.GetDirectoryName(cliPath)!;
            string cliManifest = Path.Combine(cliRoot, CliDeploymentManifestService.FileName);
            CliDeploymentManifest? deployment = null;
            string? cliError = null;
            bool cliIdentityComplete =
                !string.IsNullOrWhiteSpace(manifest.RimLiaisonCliDeploymentManifestSha256) &&
                !string.IsNullOrWhiteSpace(manifest.RimLiaisonCliDeploymentPackageSha256);
            bool cliVerified = cliIdentityComplete &&
                IsWithin(packageRoot, cliPath) &&
                CliDeploymentManifestService.Verify(
                    cliRoot,
                    cliManifest,
                    manifest.RimLiaisonCliDeploymentManifestSha256,
                    manifest.RimLiaisonCliDeploymentPackageSha256,
                    out deployment,
                    out cliError);
            if (!cliVerified ||
                deployment is null ||
                !string.Equals(deployment.SourceCommit, manifest.QualifiedSourceCommit,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Fail(
                    "PRODUCTION_TOOLCHAIN_CLI_DEPLOYMENT_INVALID",
                    cliError ?? "The installed CLI deployment manifest source commit is invalid.",
                    "Repair or reinstall the complete promoted RimLiaison CLI deployment.",
                    rejected,
                    manifestPath,
                    manifest.PromotedFingerprint,
                    currentCli,
                    runtimeRoot);
            }
        }

        if (!IsWithin(packageRoot, consumerPath) || !IsWithin(packageRoot, unifiedManifestPath))
        {
            return Fail(
                "PRODUCTION_TOOLCHAIN_SOURCE_FALLBACK",
                "The production transaction consumer or unified manifest escapes the immutable package.",
                "Re-promote the unified package with all RimLiaison-owned inputs staged beneath the promoted CLI.",
                rejected,
                manifestPath,
                manifest.PromotedFingerprint,
                currentCli,
                runtimeRoot);
        }

        if (requireCurrentExecutable)
        {
            foreach (string variable in new[] { "DEVBRIDGE_SOURCE_ROOT", "RIMTEST_DEVBRIDGE_ROOT" })
            {
                string? value = Environment.GetEnvironmentVariable(variable);
                if (!string.IsNullOrWhiteSpace(value) && !SamePath(value, runtimeRoot))
                {
                    return Fail(
                        "PRODUCTION_TOOLCHAIN_SOURCE_FALLBACK",
                        $"Production environment variable {variable} points at a source checkout instead of the installed runtime.",
                        "Clear the source-checkout override; production binds the staged transaction consumer directly.",
                        rejected,
                        manifestPath,
                        manifest.PromotedFingerprint,
                        currentCli,
                        runtimeRoot);
                }
            }
        }

        if (!string.Equals(Path.GetFileName(consumerPath), "mod-test.ps1", StringComparison.OrdinalIgnoreCase))
        {
            return Fail(
                "PRODUCTION_TOOLCHAIN_CONSUMER_INVALID",
                "The production transaction consumer is not the supported mod-test.ps1 consumer.",
                "Regenerate the unified production package from the supported DevBridge consumer.",
                rejected,
                manifestPath,
                manifest.PromotedFingerprint,
                currentCli,
                runtimeRoot);
        }

        string runtimeManifestPath = Path.Combine(runtimeRoot, ".devbridge-runtime-manifest.json");
        if (!File.Exists(runtimeManifestPath))
        {
            return Fail(
                "PRODUCTION_TOOLCHAIN_RUNTIME_MANIFEST_MISSING",
                "The installed DevBridge runtime manifest is missing.",
                "Repair or reinstall the promoted DevBridge runtime.",
                rejected,
                manifestPath,
                manifest.PromotedFingerprint,
                currentCli,
                runtimeRoot);
        }

        string cliHash;
        string assemblyHash;
        string consumerHash;
        string unifiedManifestHash;
        string packageHash;
        string coordinatorHash;
        string modHash;
        string runtimeManifestHash;
        string? runtimeSourceCommit;
        string recordedCoordinatorHash;
        string recordedModHash;
        string coordinatorPath = Path.Combine(
            runtimeRoot,
            "Coordinator",
            "DevBridge.Coordinator.exe");
        string modPath = Path.Combine(runtimeRoot, "1.6", "Assemblies", "DevBridge2.dll");
        bool modernRuntimeIdentity =
            !string.IsNullOrWhiteSpace(manifest.DevBridgeModSha256) &&
            !string.IsNullOrWhiteSpace(manifest.DevBridgeRuntimeManifestSha256);
        try
        {
            cliHash = Sha256(cliPath);
            assemblyHash = Sha256(assemblyPath);
            consumerHash = Sha256(consumerPath);
            unifiedManifestHash = Sha256(unifiedManifestPath);
            runtimeManifestHash = Sha256(runtimeManifestPath);
            runtimeSourceCommit = ReadRuntimeSourceCommit(runtimeManifestPath);
            packageHash = ReadRuntimePackageHash(runtimeManifestPath);
            recordedCoordinatorHash = ReadRuntimeFileHash(
                runtimeManifestPath,
                "Coordinator/DevBridge.Coordinator.exe");
            coordinatorHash = Sha256(coordinatorPath);
            recordedModHash = modernRuntimeIdentity
                ? ReadRuntimeFileHash(runtimeManifestPath, "1.6/Assemblies/DevBridge2.dll")
                : string.Empty;
            modHash = modernRuntimeIdentity ? Sha256(modPath) : string.Empty;
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or JsonException or CryptographicException)
        {
            return Fail(
                "PRODUCTION_TOOLCHAIN_ARTIFACT_UNREADABLE",
                $"A promoted production artifact could not be read: {exception.Message}",
                "Repair or reinstall the unified promoted production package.",
                rejected,
                manifestPath,
                manifest.PromotedFingerprint,
                currentCli,
                runtimeRoot);
        }
        var mismatching = new List<string>();
        if (!string.Equals(cliHash, manifest.RimLiaisonExecutableSha256, StringComparison.OrdinalIgnoreCase))
            mismatching.Add(cliPath);
        if (!string.Equals(assemblyHash, manifest.RimLiaisonAssemblySha256, StringComparison.OrdinalIgnoreCase))
            mismatching.Add(assemblyPath);
        if (!string.Equals(packageHash, manifest.DevBridgePackageSha256, StringComparison.OrdinalIgnoreCase))
            mismatching.Add(runtimeManifestPath);
        if (!string.Equals(recordedCoordinatorHash, manifest.DevBridgeCoordinatorSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(coordinatorHash, manifest.DevBridgeCoordinatorSha256, StringComparison.OrdinalIgnoreCase))
            mismatching.Add(coordinatorPath);
        if (!string.Equals(consumerHash, manifest.TransactionConsumerSha256, StringComparison.OrdinalIgnoreCase))
            mismatching.Add(consumerPath);
        if (!string.Equals(unifiedManifestHash, manifest.UnifiedManifestSha256, StringComparison.OrdinalIgnoreCase))
            mismatching.Add(unifiedManifestPath);
        if (!string.IsNullOrWhiteSpace(manifest.DevBridgeRuntimeManifestSha256) &&
            !string.Equals(runtimeManifestHash, manifest.DevBridgeRuntimeManifestSha256,
                StringComparison.OrdinalIgnoreCase))
            mismatching.Add(runtimeManifestPath);
        if (!string.IsNullOrWhiteSpace(manifest.DevBridgeModSha256) &&
            (!string.Equals(modHash, manifest.DevBridgeModSha256, StringComparison.OrdinalIgnoreCase) ||
             !string.Equals(recordedModHash, manifest.DevBridgeModSha256, StringComparison.OrdinalIgnoreCase)))
            mismatching.Add(modPath);
        if (!string.IsNullOrWhiteSpace(manifest.QualifiedSourceCommit) &&
            ((modernRuntimeIdentity && string.IsNullOrWhiteSpace(runtimeSourceCommit)) ||
             (!string.IsNullOrWhiteSpace(runtimeSourceCommit) &&
              !string.Equals(runtimeSourceCommit, manifest.QualifiedSourceCommit,
                  StringComparison.OrdinalIgnoreCase))))
            mismatching.Add(runtimeManifestPath);
        if (mismatching.Count > 0)
        {
            return Fail(
                "PRODUCTION_TOOLCHAIN_FINGERPRINT_MISMATCH",
                "Unified production artifact hashes do not match the production manifest.",
                "Repair or reinstall the unified promoted production package.",
                rejected,
                manifestPath,
                manifest.PromotedFingerprint,
                currentCli,
                runtimeRoot,
                [
                    cliPath,
                    assemblyPath,
                    Path.Combine(runtimeRoot, "DevBridge.cmd"),
                    modPath,
                    runtimeManifestPath,
                    consumerPath,
                    unifiedManifestPath
                ],
                mismatching);
        }
        if (!UnifiedManifestOwnsProductionRuntime(
                unifiedManifestPath,
                manifest.PromotedFingerprint,
                manifest.QualifiedSourceCommit,
                manifest.DevBridgeModSha256,
                manifest.DevBridgeRuntimeManifestSha256,
                manifest.RimLiaisonCliDeploymentManifestSha256,
                manifest.RimLiaisonCliDeploymentPackageSha256))
            return Fail(
                "PRODUCTION_TOOLCHAIN_MANIFEST_INVALID",
                "The staged unified manifest does not identify RimLiaison as its production owner.",
                "Re-promote the unified package with the RimLiaison runtime ownership metadata.",
                rejected,
                manifestPath,
                manifest.PromotedFingerprint,
                currentCli,
                runtimeRoot);

        string fingerprint = manifest.PromotedFingerprint!;
        if (!string.IsNullOrWhiteSpace(manifest.RimLiaisonCliDeploymentManifestSha256) &&
            !string.IsNullOrWhiteSpace(manifest.RimLiaisonCliDeploymentPackageSha256))
        {
            string computedFingerprint = ToolchainPromotionService.ComputePromotedFingerprint(
                manifest.QualifiedSourceCommit!,
                manifest.RimLiaisonExecutableSha256!,
                manifest.RimLiaisonAssemblySha256!,
                manifest.DevBridgeCoordinatorSha256!,
                manifest.DevBridgePackageSha256!,
                manifest.TransactionConsumerSha256!,
                manifest.RuntimeProtocolContract!,
                manifest.OwnerProduct!,
                manifest.RuntimeSubsystem!,
                manifest.DevBridgeModSha256,
                manifest.DevBridgeRuntimeManifestSha256,
                manifest.RimLiaisonCliDeploymentManifestSha256,
                manifest.RimLiaisonCliDeploymentPackageSha256);
            if (!string.Equals(computedFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                return Fail(
                    "PRODUCTION_TOOLCHAIN_FINGERPRINT_MISMATCH",
                    "The production fingerprint does not include the complete CLI deployment identity.",
                    "Re-promote the complete published CLI deployment.",
                    rejected,
                    manifestPath,
                    manifest.PromotedFingerprint,
                    currentCli,
                    runtimeRoot);
            }
        }
        if (!string.IsNullOrWhiteSpace(manifest.Fingerprint) &&
            !string.Equals(manifest.Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            return Fail(
                "PRODUCTION_TOOLCHAIN_FINGERPRINT_MISMATCH",
                "The production manifest exposes more than one product fingerprint.",
                "Re-promote the unified package so its product fingerprint is the sole runtime identity.",
                rejected,
                manifestPath,
                manifest.PromotedFingerprint,
                currentCli,
                runtimeRoot);
        }

        ProductionToolchainBinding binding = new(
            fingerprint,
            manifest.PromotedFingerprint!,
            manifest.OwnerProduct!,
            manifest.RuntimeSubsystem!,
            cliPath,
            cliHash,
            assemblyPath,
            assemblyHash,
            commandPath,
            runtimeRoot,
            packageHash,
            coordinatorHash,
            consumerPath,
            consumerHash,
            unifiedManifestPath,
            unifiedManifestHash,
            manifest.RuntimeProtocolContract!)
        {
            RimLiaisonCliDeploymentManifestHash = manifest.RimLiaisonCliDeploymentManifestSha256,
            RimLiaisonCliDeploymentPackageHash = manifest.RimLiaisonCliDeploymentPackageSha256,
            DevBridgeModHash = string.IsNullOrWhiteSpace(manifest.DevBridgeModSha256)
                ? null
                : modHash,
            DevBridgeRuntimeManifestHash = string.IsNullOrWhiteSpace(manifest.DevBridgeRuntimeManifestSha256)
                ? null
                : runtimeManifestHash
        };
        return new ProductionToolchainBindingResolution(binding, null);
    }

    private static ProductionToolchainBindingResolution Fail(
        string code,
        string error,
        string nextAction,
        List<string> rejected,
        string? manifestPath,
        string? expectedFingerprint = null,
        string? currentCli = null,
        string? runtimeRoot = null,
        IReadOnlyList<string>? expectedArtifacts = null,
        IReadOnlyList<string>? mismatchingArtifacts = null)
    {
        if (!string.IsNullOrWhiteSpace(manifestPath))
        {
            rejected.Add(FullPath(manifestPath));
        }
        return new ProductionToolchainBindingResolution(
            null,
            new ProductionToolchainBindingFailure(
                code,
                error,
                nextAction,
                rejected.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                expectedFingerprint,
                currentCli,
                runtimeRoot,
                ManifestPath: manifestPath,
                ExpectedArtifacts: expectedArtifacts,
                MismatchingArtifacts: mismatchingArtifacts));
    }

    private static bool TryReadManifest(
        string path,
        out ProductionToolchainManifest? manifest,
        out string? error)
    {
        manifest = null;
        error = null;
        try
        {
            if (!File.Exists(path))
            {
                error = $"Production manifest was not found: {FullPath(path)}.";
                return false;
            }

            manifest = JsonSerializer.Deserialize<ProductionToolchainManifest>(
                File.ReadAllText(path),
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = false,
                    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
                });
            if (manifest is null)
            {
                error = "Production manifest was empty.";
                return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            error = $"Production manifest could not be read: {exception.Message}";
            return false;
        }
    }

    private static string ReadRuntimePackageHash(string runtimeManifestPath)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(runtimeManifestPath));
        return document.RootElement.TryGetProperty("packageSha256", out JsonElement packageHash)
            ? packageHash.GetString() ?? string.Empty
            : string.Empty;
    }
    private static string? ReadRuntimeSourceCommit(string runtimeManifestPath)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(runtimeManifestPath));
        return document.RootElement.TryGetProperty("sourceCommit", out JsonElement sourceCommit)
            ? sourceCommit.GetString()
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

    private static bool UnifiedManifestOwnsProductionRuntime(
        string path,
        string? fingerprint,
        string? expectedSourceCommit = null,
        string? expectedModHash = null,
        string? expectedRuntimeManifestHash = null,
        string? expectedCliManifestHash = null,
        string? expectedCliPackageHash = null)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = document.RootElement;
            bool ownerMatches = root.TryGetProperty("ownerProduct", out JsonElement owner) &&
                string.Equals(owner.GetString(), ToolchainPromotionSchemas.OwnerProduct, StringComparison.Ordinal);
            bool subsystemMatches = root.TryGetProperty("runtimeSubsystem", out JsonElement subsystem) &&
                string.Equals(subsystem.GetString(), ToolchainPromotionSchemas.RuntimeSubsystem, StringComparison.Ordinal);
            bool boundaryMatches = root.TryGetProperty("rimBridgeServer", out JsonElement bridge) &&
                bridge.TryGetProperty("boundary", out JsonElement boundary) &&
                string.Equals(boundary.GetString(), ToolchainPromotionSchemas.RimBridgeServerBoundary, StringComparison.Ordinal);
            bool fingerprintMatches = fingerprint is null ||
                root.TryGetProperty("productFingerprint", out JsonElement productFingerprint) &&
                string.Equals(productFingerprint.GetString(), fingerprint, StringComparison.OrdinalIgnoreCase);
            bool sourceMatches = expectedSourceCommit is null ||
                root.TryGetProperty("sourceCommit", out JsonElement sourceCommit) &&
                string.Equals(sourceCommit.GetString(), expectedSourceCommit, StringComparison.OrdinalIgnoreCase);
            bool modMatches = expectedModHash is null ||
                root.TryGetProperty("modSha256", out JsonElement mod) &&
                string.Equals(mod.GetString(), expectedModHash, StringComparison.OrdinalIgnoreCase);
            bool runtimeManifestMatches = expectedRuntimeManifestHash is null ||
                root.TryGetProperty("runtimeManifestSha256", out JsonElement runtimeManifest) &&
                string.Equals(runtimeManifest.GetString(), expectedRuntimeManifestHash, StringComparison.OrdinalIgnoreCase);
            bool cliDeploymentMatches = expectedCliManifestHash is null &&
                expectedCliPackageHash is null ||
                root.TryGetProperty("rimLiaison", out JsonElement rimLiaison) &&
                rimLiaison.TryGetProperty("deploymentManifestSha256", out JsonElement cliManifest) &&
                rimLiaison.TryGetProperty("deploymentPackageSha256", out JsonElement cliPackage) &&
                string.Equals(cliManifest.GetString(), expectedCliManifestHash, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(cliPackage.GetString(), expectedCliPackageHash, StringComparison.OrdinalIgnoreCase);
            bool schemaMatches = !root.TryGetProperty("schemaVersion", out JsonElement schema) ||
                string.Equals(schema.GetString(), "rimliaison-unified-production-package/v2", StringComparison.Ordinal) ||
                string.Equals(schema.GetString(), "rimliaison-unified-production-package/v3", StringComparison.Ordinal);
            bool unifiedHashesPresent = !root.TryGetProperty("schemaVersion", out schema) ||
                !string.Equals(schema.GetString(), "rimliaison-unified-production-package/v3", StringComparison.Ordinal) ||
                root.TryGetProperty("runtimeManifestSha256", out JsonElement runtimeManifestField) &&
                runtimeManifestField.ValueKind == JsonValueKind.String &&
                root.TryGetProperty("modSha256", out JsonElement modField) &&
                modField.ValueKind == JsonValueKind.String;
            return ownerMatches && subsystemMatches && boundaryMatches && fingerprintMatches &&
                sourceMatches && modMatches && runtimeManifestMatches && cliDeploymentMatches &&
                schemaMatches && unifiedHashesPresent;
        }
        catch (Exception) when (File.Exists(path))
        {
            return false;
        }
    }

    private static bool IsWithin(string root, string path)
    {
        string normalizedRoot = FullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string normalizedPath = FullPath(path);
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
            SamePath(normalizedRoot.TrimEnd(Path.DirectorySeparatorChar), normalizedPath);
    }

    internal static string ComputeExecutionFingerprint(params string[] values)
    {
        string payload = string.Join("\n", values);
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return "tcx-" + Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static string Sha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string FullPath(string path) =>
        string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFullPath(path);

    private static bool SamePath(string left, string right) =>
        string.Equals(FullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            FullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
