using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using RimLiaison.RimDev;
using RimLiaison.Stack;

namespace RimLiaison.DevBridge;

public static class ManagedReadyToTestCandidateSchemas
{
    public const string Current = "rimliaison-managed-ready-to-test-candidate/v1";
}
public sealed record ManagedReadyToTestCandidateOptions
{
    public required DevBridgeAdapterOptions BridgeOptions { get; init; }
    public string? DeploymentRoot { get; init; }
    public string? WorkflowId { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(20);
    public int MaxStdoutBytes { get; init; } = 1024 * 1024;
    public int MaxStderrBytes { get; init; } = 64 * 1024;
}

public sealed record ManagedReadyToTestCandidateResult
{
    [JsonPropertyName("schemaVersion")] public string SchemaVersion { get; init; } = ManagedReadyToTestCandidateSchemas.Current;
    [JsonPropertyName("success")] public bool Success { get; init; }
    [JsonPropertyName("outcome")] public string Outcome { get; init; } = "CANDIDATE_INPUT_FAILURE";
    [JsonPropertyName("errorCode")] public string? ErrorCode { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
    [JsonPropertyName("candidateId")] public string? CandidateId { get; init; }
    [JsonPropertyName("canonicalProjectId")] public string? CanonicalProjectId { get; init; }
    [JsonPropertyName("sourceFingerprint")] public string? SourceFingerprint { get; init; }
    [JsonPropertyName("candidatePackageSha256")] public string? CandidatePackageSha256 { get; init; }
    [JsonPropertyName("deployedPackageSha256")] public string? DeployedPackageSha256 { get; init; }
    [JsonPropertyName("generation")] public int? Generation { get; init; }
    [JsonPropertyName("rimBridgeMode")] public string? RimBridgeMode { get; init; }
    [JsonPropertyName("rimBridgePackageId")] public string? RimBridgePackageId { get; init; }
    [JsonPropertyName("rimBridgeVersion")] public string? RimBridgeVersion { get; init; }
    [JsonPropertyName("rimBridgeLifecycleState")] public string? RimBridgeLifecycleState { get; init; }
    [JsonPropertyName("targetConfigured")] public bool? TargetConfigured { get; init; }
    [JsonPropertyName("targetLoaded")] public bool? TargetLoaded { get; init; }
    [JsonPropertyName("defsValid")] public bool? DefsValid { get; init; }
    [JsonPropertyName("targetAssemblyIdentity")] public string? TargetAssemblyIdentity { get; init; }
    [JsonPropertyName("targetAssemblyLoadState")] public string? TargetAssemblyLoadState { get; init; }
    [JsonPropertyName("attributableTargetErrors")] public IReadOnlyList<string> AttributableTargetErrors { get; init; } = [];
    [JsonPropertyName("dependencyProfileIdentity")] public string? DependencyProfileIdentity { get; init; }
    [JsonPropertyName("resolvedDependencies")] public IReadOnlyList<string> ResolvedDependencies { get; init; } = [];
    [JsonPropertyName("modsConfigProfileFingerprint")] public string? ModsConfigProfileFingerprint { get; init; }
    [JsonPropertyName("runtimeReadinessResult")] public string? RuntimeReadinessResult { get; init; }
    [JsonPropertyName("cleanup")] public ManagedCandidateCleanupEvidence Cleanup { get; init; } = new();
    [JsonPropertyName("recipeEvidence")] public object? RecipeEvidence { get; init; }
    [JsonPropertyName("managedRuntimeEvidence")] public object? ManagedRuntimeEvidence { get; init; }
    [JsonPropertyName("managedLoadEvidence")] public GenericCandidateLoadEvidence? ManagedLoadEvidence { get; init; }
    [JsonPropertyName("assemblyEvidence")] public GenericCandidateAssemblyEvidence? AssemblyEvidence { get; init; }
}

public sealed record ManagedCandidateCleanupEvidence(
    [property: JsonPropertyName("leaseReleased")] bool LeaseReleased = false,
    [property: JsonPropertyName("runtimeRestored")] bool RuntimeRestored = false,
    [property: JsonPropertyName("outcome")] string Outcome = "not-established",
    [property: JsonPropertyName("errorCode")] string? ErrorCode = null);
public sealed record GenericCandidateModEvidence(
    [property: JsonPropertyName("packageId")] string PackageId,
    [property: JsonPropertyName("modId")] string? ModId,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("loadedInSession")] bool LoadedInSession,
    [property: JsonPropertyName("matchesLoadedSession")] bool MatchesLoadedSession,
    [property: JsonPropertyName("activeLoadOrder")] int? ActiveLoadOrder,
    [property: JsonPropertyName("loadedSessionOrder")] int? LoadedSessionOrder);

public sealed record GenericCandidateAssemblyEvidence(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("loaded")] bool Loaded,
    [property: JsonPropertyName("uniqueMatch")] bool UniqueMatch,
    [property: JsonPropertyName("assemblyName")] string? AssemblyName,
    [property: JsonPropertyName("assemblyFullName")] string? AssemblyFullName,
    [property: JsonPropertyName("location")] string? Location,
    [property: JsonPropertyName("fileName")] string? FileName,
    [property: JsonPropertyName("sha256")] string? Sha256,
    [property: JsonPropertyName("expectedSha256")] string? ExpectedSha256,
    [property: JsonPropertyName("hashMatches")] bool HashMatches,
    [property: JsonPropertyName("mvid")] string? Mvid,
    [property: JsonPropertyName("errorCode")] string? ErrorCode,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("moduleName")] string? ModuleName = null,
    [property: JsonPropertyName("loadedMvid")] string? LoadedMvid = null,
    [property: JsonPropertyName("expectedMvid")] string? ExpectedMvid = null,
    [property: JsonPropertyName("mvidMatches")] bool MvidMatches = false,
    [property: JsonPropertyName("backingFileReadable")] bool BackingFileReadable = false,
    [property: JsonPropertyName("runtimeSha256")] string? RuntimeSha256 = null,
    [property: JsonPropertyName("verificationMethod")] string VerificationMethod = "none");

public sealed record GenericCandidateLoadEvidence(
    [property: JsonPropertyName("targetLoaded")] bool TargetLoaded,
    [property: JsonPropertyName("defsValid")] bool DefsValid,
    [property: JsonPropertyName("assemblyLoadState")] string AssemblyLoadState,
    [property: JsonPropertyName("generation")] int? Generation,
    [property: JsonPropertyName("evidenceSource")] string EvidenceSource,
    [property: JsonPropertyName("errorCode")] string? ErrorCode,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("attributableErrors")] IReadOnlyList<string> AttributableErrors,
    [property: JsonPropertyName("matchedMod")] GenericCandidateModEvidence? MatchedMod = null,
    [property: JsonPropertyName("assemblyEvidence")] GenericCandidateAssemblyEvidence? AssemblyEvidence = null)
{
    public static GenericCandidateLoadEvidence Failure(string errorCode, string error) =>
        new(false, false, "not-proven", null, "rimworld/list_mods", errorCode, error, []);
}


public sealed class ManagedReadyToTestCandidateConsumer
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ModeGates = [];
    private const string ActiveModsArguments = "{\"includeInactive\":false}";
    private readonly IDevBridgeProcessTransport transport;
    private readonly ManagedReadyToTestCandidateOptions options;

    public ManagedReadyToTestCandidateConsumer(
        IDevBridgeProcessTransport transport,
        ManagedReadyToTestCandidateOptions options)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<ManagedReadyToTestCandidateResult> RunAsync(
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        if (!ReadyToTestCandidateVerifier.TryVerify(
                manifestPath,
                out VerifiedReadyToTestCandidate? verified,
                out string candidateErrorCode,
                out string candidateError))
        {
            return Failure("CANDIDATE_INPUT_FAILURE", candidateErrorCode, candidateError);
        }

        ReadyToTestCandidateManifest manifest = verified!.Manifest;
        string? sourceRoot = null;
        ProjectOwnedDescriptorMaterialization? materialization = null;
        try
        {
            sourceRoot = Path.GetFullPath(manifest.SourceRepositoryRoot);
            StackManifestResolution stack = StackManifestResolver.Discover(sourceRoot);
            if (!stack.Found || stack.Manifest is null)
            {
                return FailureFor(manifest, "DEPENDENCY_PREREQUISITE", "PROJECT_METADATA_MISSING",
                    "The candidate source repository no longer has a readable owning manifest.");
            }

            string? runtimeRoot = string.IsNullOrWhiteSpace(options.DeploymentRoot)
                ? null
                : Path.GetFullPath(options.DeploymentRoot);
            if (runtimeRoot is null)
            {
                ProjectRuntimeBindingResult binding = ProjectRuntimeBindingResolver.Resolve(
                    sourceRoot,
                    stack.Manifest,
                    enroll: false);
                if (!binding.Succeeded || string.IsNullOrWhiteSpace(binding.RuntimeRoot))
                {
                    return FailureFor(manifest, "DEPENDENCY_PREREQUISITE",
                        binding.ErrorCode ?? "PROJECT_RUNTIME_ROOT_MISSING",
                        binding.Error ?? "The candidate project runtime binding is not ready.");
                }
                runtimeRoot = binding.RuntimeRoot!;
            }

            materialization = ProjectOwnedDescriptorMaterializer.Materialize(
                manifest.CanonicalProjectId,
                sourceRoot,
                runtimeRoot,
                out string? metadataCode,
                out string? metadataError);
            if (materialization is null)
            {
                return FailureFor(manifest, "DEPENDENCY_PREREQUISITE",
                    metadataCode ?? "PROJECT_METADATA_MISSING",
                    metadataError ?? "The candidate project metadata could not be materialized.");
            }
            string? expectedAssembly = materialization.Descriptor.ExpectedAssembly;
            ReadyToTestCandidatePackageEntry? assemblyEntry = !string.IsNullOrWhiteSpace(expectedAssembly)
                ? verified.Package.FirstOrDefault(entry =>
                    string.Equals(entry.TargetPath, materialization.Descriptor.DeploymentTarget,
                        StringComparison.OrdinalIgnoreCase))
                : null;
            string? expectedAssemblyHash = assemblyEntry?.Sha256;
            string? expectedMvid = null;
            if (!string.IsNullOrWhiteSpace(expectedAssembly))
            {
                if (assemblyEntry is null ||
                    !AssemblyMetadataIdentity.TryReadModuleMvid(assemblyEntry.SourcePath, out expectedMvid))
                {
                    return FailureFor(manifest, "CANDIDATE_INPUT_FAILURE",
                        "TARGET_ASSEMBLY_MVID_UNAVAILABLE",
                        "The exact SHA-verified candidate assembly did not expose readable PE metadata for its module MVID.");
                }
            }

            RequiredRimBridgeProcessTransport runtimeTransport = new(transport);
            RimBridgeRequirementResult rimBridge = await EnsureRequiredRimBridgeAsync(
                    runtimeTransport,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!rimBridge.Succeeded)
            {
                return FailureFor(
                    manifest,
                    "DEPENDENCY_PREREQUISITE",
                    rimBridge.ErrorCode ?? "RIMBRIDGE_REQUIRED_UNAVAILABLE",
                    rimBridge.Error ?? "Required RimBridge routing could not be established.");
            }

            DevBridgeModDevelopmentAdapterOptions managedOptions = new()
            {
                RootPath = options.BridgeOptions.RootPath,
                ScriptRootPath = options.BridgeOptions.SourceRootPath,
                TransactionConsumerPath = options.BridgeOptions.CommandPath,
                DeploymentRoot = runtimeRoot,
                TestRecipe = materialization.Descriptor.TestRecipe,
                UseInternalTransaction = true,
                Timeout = options.Timeout,
                MaxStdoutBytes = options.MaxStdoutBytes,
                MaxStderrBytes = options.MaxStderrBytes
            };
            InternalDevelopmentTransactionService transaction =
                new(runtimeTransport, managedOptions);
            DevBridgeModDevelopmentResult managed = await transaction.RunAsync(
                    manifest.CanonicalProjectId,
                    sourceRoot,
                    manifest.SourceFingerprint,
                    options.WorkflowId,
                    new DevBridgeModDevelopmentExecutionContext(
                        Candidate: manifest,
                        CandidateManifestPath: Path.GetFullPath(manifestPath)),
                    cancellationToken)
                .ConfigureAwait(false);

            ManagedCandidateCleanupEvidence cleanup = new(
                LeaseReleased: managed.LeaseId is null,
                RuntimeRestored: managed.Status.IsSuccess,
                Outcome: managed.Status.IsSuccess ? "released" : "bounded-failure",
                ErrorCode: managed.Status.IsSuccess ? null : managed.Status.ErrorCode);
            GenericCandidateLoadEvidence generic = await ReadGenericLoadEvidenceAsync(
                    runtimeTransport,
                    expectedAssembly,
                    expectedAssemblyHash,
                    expectedMvid,
                    manifest.PackageId ?? manifest.CanonicalProjectId,
                    managed.Generation,
                    cancellationToken)
                .ConfigureAwait(false);
            RuntimeStatusEvidence status = await ReadStatusAsync(runtimeTransport, cancellationToken).ConfigureAwait(false);
            bool targetConfigured = IsTargetConfigured(status, manifest,
                materialization.Descriptor.CanonicalProjectId ?? manifest.CanonicalProjectId);
            bool targetLoaded = generic.TargetLoaded &&
                generic.Generation == managed.Generation;
            string? deployedHash = managed.Freshness?.DeployedPackageSha256;
            bool hashMatches = string.Equals(
                deployedHash,
                manifest.PackageSha256,
                StringComparison.OrdinalIgnoreCase);
            List<string> targetErrors = generic.AttributableErrors.ToList();
            if (status.Error is not null &&
                status.Error.Contains(manifest.PackageId ?? manifest.CanonicalProjectId,
                    StringComparison.OrdinalIgnoreCase))
            {
                targetErrors.Add(status.Error);
            }

            bool defsValid = targetLoaded && generic.DefsValid && targetErrors.Count == 0;
            bool dependenciesReady = DependenciesPresent(status, manifest.RequiredRuntimeDependencies);
            bool assemblyProof = expectedAssemblyHash is null ||
                generic.AssemblyLoadState is "loaded-and-hash-matched" or "loaded-and-mvid-matched";
            bool success = hashMatches && targetConfigured && targetLoaded && defsValid &&
                dependenciesReady && assemblyProof;
            string outcome = success
                ? "PASS"
                : targetErrors.Count > 0 || !assemblyProof
                    ? "MOD_FAILURE"
                    : !dependenciesReady
                        ? "DEPENDENCY_PREREQUISITE"
                        : "INFRASTRUCTURE_FAILURE";
            return new ManagedReadyToTestCandidateResult
            {
                Success = success,
                Outcome = outcome,
                ErrorCode = success ? null : managed.Status.ErrorCode ?? generic.ErrorCode ??
                    (targetErrors.Count > 0 ? "CANDIDATE_TARGET_LOAD_FAILED" :
                        hashMatches ? "CANDIDATE_TARGET_NOT_PROVEN" : "CANDIDATE_DEPLOYED_HASH_MISMATCH"),
                Error = success ? null : managed.Status.Error ?? generic.Error ??
                    (targetErrors.Count > 0 ? targetErrors[0] :
                        hashMatches ? "Generic runtime evidence did not prove candidate load." :
                        "The deployed package hash did not match the candidate package hash."),
                CandidateId = manifest.CandidateId,
                CanonicalProjectId = manifest.CanonicalProjectId,
                RimBridgeMode = status.RimBridgeMode,
                RimBridgePackageId = status.RimBridgePackageId,
                RimBridgeVersion = status.RimBridgeVersion,
                RimBridgeLifecycleState = status.RimBridgeLifecycleState,
                SourceFingerprint = manifest.SourceFingerprint,
                CandidatePackageSha256 = manifest.PackageSha256,
                DeployedPackageSha256 = deployedHash,
                Generation = managed.Generation,
                TargetConfigured = targetConfigured,
                TargetLoaded = targetLoaded,
                DefsValid = defsValid,
                TargetAssemblyIdentity = managed.Freshness?.DeployedArtifactSha256,
                TargetAssemblyLoadState = generic.AssemblyLoadState,
                AttributableTargetErrors = targetErrors,
                DependencyProfileIdentity = status.ProfileFingerprint,
                ResolvedDependencies = status.ResolvedMods,
                ModsConfigProfileFingerprint = status.ProfileFingerprint,
                RuntimeReadinessResult = managed.Freshness?.LoadedArtifactFreshnessProven == true &&
                    managed.Generation is > 0 ? "READY" : "NOT_PROVEN",
                Cleanup = cleanup,
                AssemblyEvidence = generic.AssemblyEvidence,
                ManagedLoadEvidence = generic,
                ManagedRuntimeEvidence = managed
            };
        }

        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return FailureFor(manifest, "INFRASTRUCTURE_FAILURE", "RIMTEST_CANCELLED", "Managed candidate execution was cancelled.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            return FailureFor(manifest, "INFRASTRUCTURE_FAILURE", "MANAGED_CANDIDATE_EXECUTION_FAILED", Bound(exception.Message));
        }
        finally
        {
            if (materialization is not null)
                ProjectOwnedDescriptorMaterializer.Delete(materialization);
        }
    }

    private async Task<GenericCandidateLoadEvidence> ReadGenericLoadEvidenceAsync(
        RequiredRimBridgeProcessTransport runtimeTransport,
        string? expectedAssembly,
        string? expectedAssemblyHash,
        string? expectedMvid,
        string packageId,
        int? expectedGeneration,
        CancellationToken cancellationToken)
    {
        DevBridgeLeaseAdapter leaseAdapter = new(runtimeTransport, options.BridgeOptions);
        DevBridgeLeaseResult lease = await leaseAdapter.BeginLeaseAsync(
                options.WorkflowId,
                cancellationToken)
            .ConfigureAwait(false);
        if (!lease.IsUsable)
        {
            return GenericCandidateLoadEvidence.Failure(
                lease.Status.ErrorCode ?? "RIMBRIDGE_LOAD_EVIDENCE_LEASE_UNAVAILABLE",
                lease.Status.Error ?? "DevBridge did not grant a lease for generic load evidence.");
        }

        GenericCandidateLoadEvidence evidence;
        DevBridgeLeaseResult released;
        try
        {
            evidence = await ReadGenericLoadEvidenceCoreAsync(
                    runtimeTransport,
                    lease.LeaseId!,
                    expectedAssembly,
                    expectedAssemblyHash,
                    expectedMvid,
                    packageId,
                    expectedGeneration,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            released = await leaseAdapter.EndLeaseAsync(
                    lease.LeaseId!,
                    options.WorkflowId,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        return released.Status.IsSuccess
            ? evidence
            : GenericCandidateLoadEvidence.Failure(
                "RIMBRIDGE_LOAD_EVIDENCE_LEASE_RELEASE_FAILED",
                released.Status.Error ?? "DevBridge did not prove generic-evidence lease release.");
    }

    private async Task<GenericCandidateLoadEvidence> ReadGenericLoadEvidenceCoreAsync(
        RequiredRimBridgeProcessTransport runtimeTransport,
        string leaseId,
        string? expectedAssembly,
        string? expectedAssemblyHash,
        string? expectedMvid,
        string packageId,
        int? expectedGeneration,
        CancellationToken cancellationToken)
    {
        DevBridgeProcessResult process = await runtimeTransport.ExecuteAsync(
                new DevBridgeProcessRequest(
                    options.BridgeOptions.CommandPath,
                    options.BridgeOptions.RootPath,
                    ["--root", options.BridgeOptions.RootPath, "bridge", "call",
                        "rimworld/list_mods", ActiveModsArguments, "--lease", leaseId, "--json"],
                    options.Timeout,
                    options.MaxStdoutBytes,
                    options.MaxStderrBytes,
                    DevBridgeProcessEnvironment.ForWorkflow(options.WorkflowId),
                    OperationKey: "candidate-generic-load-evidence"),
                cancellationToken)
            .ConfigureAwait(false);
        if (process.Cancelled)
            return GenericCandidateLoadEvidence.Failure("RIMTEST_CANCELLED",
                "Generic candidate load evidence was cancelled.");
        if (process.TimedOut)
            return GenericCandidateLoadEvidence.Failure("RIMBRIDGE_LOAD_EVIDENCE_TIMEOUT",
                "RimBridge did not return generic mod-load evidence in time.");
        if (!string.IsNullOrWhiteSpace(process.StartError) ||
            string.IsNullOrWhiteSpace(process.Stdout))
        {
            return GenericCandidateLoadEvidence.Failure("RIMBRIDGE_LOAD_EVIDENCE_UNAVAILABLE",
                Bound(process.StartError ?? process.Stderr ??
                    "RimBridge returned no generic mod-load evidence."));
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(process.Stdout);
            JsonElement envelope = Property(document.RootElement, "rimBridgeRoute");
            if (envelope.ValueKind == JsonValueKind.Undefined)
                envelope = document.RootElement;
            if (envelope.ValueKind != JsonValueKind.Object ||
                !TryGetBoolean(envelope, "success", out bool success))
            {
                return GenericCandidateLoadEvidence.Failure("RIMBRIDGE_LOAD_EVIDENCE_INVALID",
                    "RimBridge returned an invalid generic mod-load evidence envelope.");
            }
            if (!success)
            {
                return GenericCandidateLoadEvidence.Failure(
                    StringValue(envelope, "errorCode") ?? "RIMBRIDGE_LOAD_EVIDENCE_FAILED",
                    StringValue(envelope, "error") ??
                    "RimBridge rejected generic mod-load evidence.");
            }
            JsonElement result = Property(envelope, "result");
            if (result.ValueKind != JsonValueKind.Object ||
                !TryGetBoolean(result, "success", out bool resultSuccess))
            {
                return GenericCandidateLoadEvidence.Failure("RIMBRIDGE_LOAD_EVIDENCE_INVALID",
                    "RimBridge returned no canonical generic mod-load result.");
            }
            if (!resultSuccess)
            {
                return GenericCandidateLoadEvidence.Failure(
                    StringValue(result, "errorCode") ?? "RIMBRIDGE_LOAD_EVIDENCE_FAILED",
                    StringValue(result, "error") ??
                    "RimBridge rejected canonical generic mod-load evidence.");
            }
            JsonElement mods = Property(result, "mods");
            if (mods.ValueKind != JsonValueKind.Array)
            {
                return GenericCandidateLoadEvidence.Failure("RIMBRIDGE_LOAD_EVIDENCE_INVALID",
                    "RimBridge returned no canonical generic mod list.");
            }
            GenericCandidateModEvidence? matchedMod = FindTargetMod(mods, packageId);
            List<string> errors = CollectTargetLoadErrors(result, packageId);
            bool targetLoaded = matchedMod is not null &&
                matchedMod.Enabled &&
                matchedMod.LoadedInSession &&
                matchedMod.MatchesLoadedSession;
            GenericCandidateAssemblyEvidence? assemblyEvidence = null;
            string assemblyLoadState = expectedAssemblyHash is null ? "not-required" : "not-proven";
            string? assemblyErrorCode = null;
            string? assemblyError = null;
            if (targetLoaded && errors.Count == 0 && expectedAssemblyHash is not null)
            {
                assemblyEvidence = await ReadAssemblyVerificationCoreAsync(
                        runtimeTransport,
                        leaseId,
                        expectedAssembly!,
                        expectedAssemblyHash,
                        expectedMvid,
                        cancellationToken)
                    .ConfigureAwait(false);
                assemblyLoadState = AssemblyLoadState(assemblyEvidence);
                if (!assemblyEvidence.Success)
                {
                    assemblyErrorCode = assemblyEvidence.ErrorCode ?? "TARGET_ASSEMBLY_VERIFICATION_FAILED";
                    assemblyError = assemblyEvidence.Error ??
                        "The exact target assembly could not be verified.";
                }
            }
            int? generation = ReadGeneration(envelope);
            return new GenericCandidateLoadEvidence(
                targetLoaded,
                targetLoaded && errors.Count == 0,
                assemblyLoadState,
                generation,
                "rimworld/list_mods",
                assemblyErrorCode,
                assemblyError,
                errors,
                matchedMod,
                assemblyEvidence);
        }
        catch (JsonException exception)
        {
            return GenericCandidateLoadEvidence.Failure("RIMBRIDGE_LOAD_EVIDENCE_INVALID",
                Bound("RimBridge returned malformed generic mod-load evidence: " + exception.Message));
        }
    }


    private static GenericCandidateModEvidence? FindTargetMod(
        JsonElement mods,
        string packageId)
    {
        foreach (JsonElement mod in mods.EnumerateArray())
        {
            if (mod.ValueKind != JsonValueKind.Object ||
                !string.Equals(StringValue(mod, "packageId"), packageId,
                    StringComparison.OrdinalIgnoreCase))
                continue;
            return new GenericCandidateModEvidence(
                packageId,
                StringValue(mod, "modId"),
                TryGetBoolean(mod, "enabled", out bool enabled) && enabled,
                TryGetBoolean(mod, "loadedInSession", out bool loadedInSession) && loadedInSession,
                TryGetBoolean(mod, "matchesLoadedSession", out bool matchesLoadedSession) && matchesLoadedSession,
                TryGetInt(mod, "activeLoadOrder", out int activeLoadOrder) ? activeLoadOrder : null,
                TryGetInt(mod, "loadedSessionOrder", out int loadedSessionOrder) ? loadedSessionOrder : null);
        }
        return null;
    }
    private async Task<GenericCandidateAssemblyEvidence> ReadAssemblyVerificationCoreAsync(
        RequiredRimBridgeProcessTransport runtimeTransport,
        string leaseId,
        string expectedAssembly,
        string expectedAssemblyHash,
        string? expectedMvid,
        CancellationToken cancellationToken)
    {
        GenericCandidateAssemblyEvidence failure(string code, string message) =>
            new(false, false, false, expectedAssembly, null, null, null, null,
                expectedAssemblyHash, false, null, code, message);

        DevBridgeProcessResult process = await runtimeTransport.ExecuteAsync(
                new DevBridgeProcessRequest(
                    options.BridgeOptions.CommandPath,
                    options.BridgeOptions.RootPath,
                    ["--root", options.BridgeOptions.RootPath, "bridge", "call",
                        "devbridge/verify_loaded_assembly",
                        JsonSerializer.Serialize(new
                        {
                            assemblyName = expectedAssembly,
                            expectedSha256 = expectedAssemblyHash,
                            expectedMvid
                        }),
                        "--lease", leaseId, "--json"],
                    options.Timeout,
                    options.MaxStdoutBytes,
                    options.MaxStderrBytes,
                    DevBridgeProcessEnvironment.ForWorkflow(options.WorkflowId),
                    OperationKey: "candidate-assembly-verification"),
                cancellationToken)
            .ConfigureAwait(false);
        if (process.Cancelled)
            return failure("RIMTEST_CANCELLED", "Target assembly verification was cancelled.");
        if (process.TimedOut)
            return failure("TARGET_ASSEMBLY_VERIFICATION_TIMEOUT",
                "RimBridge did not return exact target assembly evidence in time.");
        if (!string.IsNullOrWhiteSpace(process.StartError) ||
            string.IsNullOrWhiteSpace(process.Stdout))
        {
            return failure("TARGET_ASSEMBLY_VERIFICATION_UNAVAILABLE",
                Bound(process.StartError ?? process.Stderr ??
                    "RimBridge returned no exact target assembly evidence."));
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(process.Stdout);
            JsonElement envelope = Property(document.RootElement, "rimBridgeRoute");
            if (envelope.ValueKind == JsonValueKind.Undefined)
                envelope = document.RootElement;
            if (envelope.ValueKind != JsonValueKind.Object ||
                !TryGetBoolean(envelope, "success", out bool routeSuccess))
            {
                return failure("TARGET_ASSEMBLY_VERIFICATION_INVALID",
                    "RimBridge returned an invalid target assembly evidence envelope.");
            }
            if (!routeSuccess)
            {
                return failure(
                    StringValue(envelope, "errorCode") ?? "TARGET_ASSEMBLY_VERIFICATION_FAILED",
                    StringValue(envelope, "error") ??
                    "RimBridge rejected exact target assembly evidence.");
            }
            JsonElement result = Property(envelope, "result");
            if (result.ValueKind != JsonValueKind.Object)
            {
                return failure("TARGET_ASSEMBLY_VERIFICATION_INVALID",
                    "RimBridge returned no target assembly evidence result.");
            }
            bool reportedSuccess = TryGetBoolean(result, "success", out bool success) && success;
            bool loaded = TryGetBoolean(result, "loaded", out bool loadedValue) && loadedValue;
            bool uniqueMatch = TryGetBoolean(result, "uniqueMatch", out bool uniqueValue) && uniqueValue;
            string? reportedExpectedHash = StringValue(result, "expectedSha256");
            string? sha256 = StringValue(result, "runtimeSha256") ?? StringValue(result, "sha256");
            bool backingFileReadable = TryGetBoolean(result, "backingFileReadable", out bool readableValue) &&
                readableValue;
            bool hashMatches = TryGetBoolean(result, "hashMatches", out bool hashValue) && hashValue;
            string? loadedMvid = StringValue(result, "loadedMvid") ?? StringValue(result, "mvid");
            string? reportedExpectedMvid = StringValue(result, "expectedMvid");
            bool mvidMatches = TryGetBoolean(result, "mvidMatches", out bool mvidValue) && mvidValue;
            string? errorCode = StringValue(result, "errorCode");
            string? error = StringValue(result, "error");
            bool hashProof = backingFileReadable && hashMatches &&
                string.Equals(reportedExpectedHash, expectedAssemblyHash,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(sha256, expectedAssemblyHash,
                    StringComparison.OrdinalIgnoreCase);
            bool mvidProof = loadedMvid is not null && mvidMatches &&
                string.Equals(reportedExpectedMvid, expectedMvid,
                    StringComparison.OrdinalIgnoreCase);
            bool exact = reportedSuccess && loaded && uniqueMatch &&
                (backingFileReadable ? hashProof && mvidProof : mvidProof);
            if (!exact && errorCode is null)
            {
                errorCode = !loaded
                    ? "TARGET_ASSEMBLY_NOT_LOADED"
                    : !uniqueMatch
                        ? "TARGET_ASSEMBLY_AMBIGUOUS"
                        : backingFileReadable && !hashProof || loadedMvid is not null && !mvidProof
                            ? "TARGET_ASSEMBLY_IDENTITY_MISMATCH"
                            : "TARGET_ASSEMBLY_EVIDENCE_UNAVAILABLE";
                error ??= "The loaded target assembly did not match the candidate SHA-256 and module MVID identity.";
            }
            return new GenericCandidateAssemblyEvidence(
                exact,
                loaded,
                uniqueMatch,
                StringValue(result, "assemblyName"),
                StringValue(result, "assemblyFullName"),
                StringValue(result, "location"),
                StringValue(result, "fileName"),
                sha256,
                reportedExpectedHash ?? expectedAssemblyHash,
                hashMatches,
                loadedMvid,
                exact ? null : errorCode,
                exact ? null : error,
                StringValue(result, "moduleName"),
                loadedMvid,
                reportedExpectedMvid ?? expectedMvid,
                mvidMatches,
                backingFileReadable,
                sha256,
                StringValue(result, "verificationMethod") ?? "none");
        }
        catch (JsonException exception)
        {
            return failure("TARGET_ASSEMBLY_VERIFICATION_INVALID",
                Bound("RimBridge returned malformed target assembly evidence: " + exception.Message));
        }
    }

    private static string AssemblyLoadState(GenericCandidateAssemblyEvidence evidence)
    {
        if (evidence.Success && evidence.Loaded && evidence.UniqueMatch)
        {
            return evidence.VerificationMethod == "mvid"
                ? "loaded-and-mvid-matched"
                : "loaded-and-hash-matched";
        }
        return evidence.ErrorCode switch
        {
            "TARGET_ASSEMBLY_NOT_LOADED" => "not-loaded",
            "TARGET_ASSEMBLY_AMBIGUOUS" => "ambiguous",
            "TARGET_ASSEMBLY_EVIDENCE_UNAVAILABLE" => "evidence-unavailable",
            "TARGET_ASSEMBLY_VERIFICATION_UNAVAILABLE" => "unavailable",
            "TARGET_ASSEMBLY_VERIFICATION_TIMEOUT" => "timeout",
            _ => evidence.Loaded && evidence.UniqueMatch ? "identity-mismatch" : "not-verified"
        };
    }

    private static List<string> CollectTargetLoadErrors(JsonElement value, string packageId)
    {
        List<string> errors = [];
        CollectTargetLoadErrors(value, packageId, errors);
        return errors;
    }

    private static void CollectTargetLoadErrors(JsonElement value, string packageId, List<string> errors)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            string? objectPackage = StringValue(value, "packageId");
            foreach (JsonProperty property in value.EnumerateObject())
            {
                bool errorField = property.Name.Equals("error", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Equals("errors", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Contains("load", StringComparison.OrdinalIgnoreCase) &&
                    property.Name.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Contains("def", StringComparison.OrdinalIgnoreCase) &&
                    property.Name.Contains("error", StringComparison.OrdinalIgnoreCase);
                if (errorField)
                    AddTargetLoadError(property.Value, objectPackage, packageId, errors);
                else
                    CollectTargetLoadErrors(property.Value, packageId, errors);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
                CollectTargetLoadErrors(item, packageId, errors);
        }
    }

    private static void AddTargetLoadError(
        JsonElement value,
        string? objectPackage,
        string packageId,
        List<string> errors)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            string? message = value.GetString();
            if (objectPackage is null ||
                string.Equals(objectPackage, packageId, StringComparison.OrdinalIgnoreCase))
                if (!string.IsNullOrWhiteSpace(message))
                    errors.Add(message);
            return;
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
                AddTargetLoadError(item, objectPackage, packageId, errors);
            return;
        }
        if (value.ValueKind != JsonValueKind.Object)
            return;
        string? itemPackage = StringValue(value, "packageId") ?? objectPackage;
        string? messageValue = StringValue(value, "message") ?? StringValue(value, "error");
        if (messageValue is not null &&
            (itemPackage is null ||
             string.Equals(itemPackage, packageId, StringComparison.OrdinalIgnoreCase)))
            errors.Add(messageValue);
        else
            CollectTargetLoadErrors(value, packageId, errors);
    }

    private static int? ReadGeneration(JsonElement envelope)
    {
        JsonElement provenance = Property(envelope, "provenance");
        return TryGetInt(provenance, "generation", out int generation) ? generation :
            TryGetInt(envelope, "generation", out generation) ? generation : null;
    }

    private static bool TryGetBoolean(JsonElement root, string name, out bool value)
    {
        JsonElement property = Property(root, name);
        if (property.ValueKind == JsonValueKind.True)
        {
            value = true;
            return true;
        }
        if (property.ValueKind == JsonValueKind.False)
        {
            value = false;
            return true;
        }
        value = false;
        return false;
    }

    private static bool TryGetInt(JsonElement root, string name, out int value)
    {
        JsonElement property = Property(root, name);
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out value))
            return true;
        value = 0;
        return false;
    }
    private async Task<RimBridgeRequirementResult> EnsureRequiredRimBridgeAsync(
        RequiredRimBridgeProcessTransport runtimeTransport,
        CancellationToken cancellationToken)
    {
        string key = Path.GetFullPath(options.BridgeOptions.RootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        SemaphoreSlim gate = ModeGates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RuntimeStatusEvidence status = await ReadStatusAsync(runtimeTransport, cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(status.LeaseId))
                return RimBridgeRequirementResult.Failed(
                    "RIMBRIDGE_MODE_TRANSITION_BUSY",
                    "The existing DevBridge coordinator has an active lease; required RimBridge mode was not forced.");
            bool stopped = string.Equals(status.State, "STOPPED", StringComparison.OrdinalIgnoreCase);
            bool staleStopped = stopped &&
                string.Equals(status.ErrorCode, "PROCESS_EXITED", StringComparison.OrdinalIgnoreCase);
            if (stopped &&
                (string.IsNullOrWhiteSpace(status.RimBridgeMode) ||
                 string.Equals(status.RimBridgeMode, "required", StringComparison.OrdinalIgnoreCase)))
            {
                // STOPPED is the normal lazy-launch boundary. A PROCESS_EXITED
                // error may remain as durable evidence after the owned process
                // has disappeared; the managed transaction owns the next launch.
                return RimBridgeRequirementResult.Success();
            }
            if (!string.IsNullOrWhiteSpace(status.Error) && !staleStopped)
                return RimBridgeRequirementResult.Failed("RIMBRIDGE_MODE_STATUS_FAILED", status.Error);
            if (string.IsNullOrWhiteSpace(status.RimBridgeMode))
                return RimBridgeRequirementResult.Failed(
                    "RIMBRIDGE_MODE_UNREPORTED",
                    "The coordinator status did not report an effective RimBridge mode.");
            if (string.Equals(status.RimBridgeMode, "required", StringComparison.OrdinalIgnoreCase))
                return RimBridgeRequirementResult.Success();
            DevBridgeProcessResult shutdown = await runtimeTransport.ExecuteAsync(
                    new DevBridgeProcessRequest(
                        options.BridgeOptions.CommandPath,
                        options.BridgeOptions.RootPath,
                        ["--root", options.BridgeOptions.RootPath, "coordinator", "shutdown", "--json"],
                        TimeSpan.FromSeconds(30),
                        options.MaxStdoutBytes,
                        options.MaxStderrBytes,
                        OperationKey: "candidate-rimbridge-mode-transition"),
                    cancellationToken)
                .ConfigureAwait(false);
            if (shutdown.ExitCode is not 0 || shutdown.TimedOut || shutdown.Cancelled ||
                !string.IsNullOrWhiteSpace(shutdown.StartError))
            {
                return RimBridgeRequirementResult.Failed(
                    "RIMBRIDGE_MODE_TRANSITION_FAILED",
                    Bound(shutdown.StartError ?? shutdown.Stderr ?? "The coordinator did not accept the required-mode shutdown."));
            }
            // The shutdown boundary leaves durable state untouched. The next runtime
            // request carries required mode and is the authoritative lazy restart.
            return RimBridgeRequirementResult.Success();
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<RuntimeStatusEvidence> ReadStatusAsync(
        IDevBridgeProcessTransport runtimeTransport,
        CancellationToken cancellationToken)
    {
        DevBridgeProcessResult result = await runtimeTransport.ExecuteAsync(
                new DevBridgeProcessRequest(
                    options.BridgeOptions.CommandPath,
                    options.BridgeOptions.RootPath,
                    ["--root", options.BridgeOptions.RootPath, "status", "--json"],
                    options.Timeout,
                    options.MaxStdoutBytes,
                    options.MaxStderrBytes,
                    OperationKey: "candidate-runtime-status"),
                cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode is not 0 || string.IsNullOrWhiteSpace(result.Stdout))
            return new RuntimeStatusEvidence(null, [], result.StartError ?? result.Stderr, null, null, null, null, null, null, null);
        using JsonDocument document = JsonDocument.Parse(result.Stdout);
        JsonElement root = document.RootElement;
        JsonElement rimBridge = Property(root, "rimBridge");
        return new RuntimeStatusEvidence(
            StringValue(root, "profileFingerprint"),
            StringArray(root, "resolvedMods"),
            StringValue(root, "error") ?? StringValue(root, "errorMessage"),
            StringValue(rimBridge, "configuredMode"),
            StringValue(root, "leaseId"),
            StringValue(rimBridge, "packageId"),
            StringValue(rimBridge, "version"),
            StringValue(rimBridge, "lifecycleState"),
            StringValue(root, "state"),
            StringValue(root, "errorCode"));
    }

    private static bool IsTargetConfigured(
        RuntimeStatusEvidence status,
        ReadyToTestCandidateManifest manifest,
        string canonicalProjectId)
    {
        if (!string.IsNullOrWhiteSpace(manifest.PackageId))
            return status.ResolvedMods.Any(value => string.Equals(value, manifest.PackageId, StringComparison.OrdinalIgnoreCase));
        return status.ResolvedMods.Any(value => string.Equals(value, canonicalProjectId, StringComparison.OrdinalIgnoreCase));
    }

    private static bool DependenciesPresent(RuntimeStatusEvidence status, IReadOnlyList<string> dependencies) =>
        dependencies.All(dependency => status.ResolvedMods.Any(value =>
            string.Equals(value, dependency, StringComparison.OrdinalIgnoreCase)));

    private static bool IsTargetModFailure(DevBridgeRecipeRunResult target) =>
        target.Status.Outcome == DevBridgeOutcomeKind.TestFailure ||
        target.Status.ErrorCode?.StartsWith("MOD_", StringComparison.OrdinalIgnoreCase) == true;

    private static string ClassifyManagedFailure(string? errorCode, DevBridgeOutcomeKind outcome) =>
        errorCode is "PROJECT_RECIPE_NOT_FOUND" or "PROJECT_RUNTIME_ROOT_MISSING" or "PROJECT_METADATA_MISSING"
            ? "DEPENDENCY_PREREQUISITE"
            : errorCode is "DEVBRIDGE_LEASE_RELEASE_FAILED" or "DEVELOPMENT_MAINTENANCE_NOT_CONFIRMED" or
                "DEVELOPMENT_GENERATION_NOT_READY" or "DEVBRIDGE_STATUS_FAILED"
                ? "INFRASTRUCTURE_FAILURE"
                : outcome == DevBridgeOutcomeKind.TestFailure ? "MOD_FAILURE" : "INFRASTRUCTURE_FAILURE";

    private static ManagedReadyToTestCandidateResult Failure(
        string outcome,
        string errorCode,
        string error) => new()
        {
            Success = false,
            Outcome = outcome,
            ErrorCode = errorCode,
            Error = error
        };

    private static ManagedReadyToTestCandidateResult FailureFor(
        ReadyToTestCandidateManifest manifest,
        string outcome,
        string errorCode,
        string error,
        DevBridgeModDevelopmentResult? managed = null,
        ManagedCandidateCleanupEvidence? cleanup = null) => new()
        {
            Success = false,
            Outcome = outcome,
            ErrorCode = errorCode,
            Error = error,
            CandidateId = manifest.CandidateId,
            CanonicalProjectId = manifest.CanonicalProjectId,
            SourceFingerprint = manifest.SourceFingerprint,
            CandidatePackageSha256 = manifest.PackageSha256,
            DeployedPackageSha256 = managed?.Freshness?.DeployedPackageSha256,
            Generation = managed?.Generation,
            Cleanup = cleanup ?? new ManagedCandidateCleanupEvidence(
                Outcome: "not-started"),
            ManagedRuntimeEvidence = managed
        };

    private static JsonElement Property(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return default;
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        }
        return default;
    }

    private static string? StringValue(JsonElement root, string name) =>
        Property(root, name).ValueKind == JsonValueKind.String
            ? Property(root, name).GetString()
            : null;

    private static IReadOnlyList<string> StringArray(JsonElement root, string name) =>
        Property(root, name).ValueKind == JsonValueKind.Array
            ? Property(root, name).EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!).ToArray()
            : [];

    private static string Bound(string value) => value.Length <= 4096 ? value : value[..4096];

    private sealed record RimBridgeRequirementResult(
        bool Succeeded,
        string? ErrorCode = null,
        string? Error = null)
    {
        public static RimBridgeRequirementResult Success() => new(true);
        public static RimBridgeRequirementResult Failed(string errorCode, string error) => new(false, errorCode, error);
    }

    private sealed class RequiredRimBridgeProcessTransport : IDevBridgeProcessTransport
    {
        private readonly IDevBridgeProcessTransport inner;

        public RequiredRimBridgeProcessTransport(IDevBridgeProcessTransport inner) =>
            this.inner = inner ?? throw new ArgumentNullException(nameof(inner));

        public Task<DevBridgeProcessResult> ExecuteAsync(
            DevBridgeProcessRequest request,
            CancellationToken cancellationToken)
        {
            Dictionary<string, string> environment = request.EnvironmentVariables is null
                ? new(StringComparer.Ordinal)
                : new(request.EnvironmentVariables, StringComparer.Ordinal);
            environment["DEVBRIDGE_RIMBRIDGE_MODE"] = "required";
            return inner.ExecuteAsync(request with { EnvironmentVariables = environment }, cancellationToken);
        }
    }
    private sealed record RuntimeStatusEvidence(
        string? ProfileFingerprint,
        IReadOnlyList<string> ResolvedMods,
        string? Error,
        string? RimBridgeMode,
        string? LeaseId,
        string? RimBridgePackageId,
        string? RimBridgeVersion,
        string? RimBridgeLifecycleState,
        string? State,
        string? ErrorCode);
}
