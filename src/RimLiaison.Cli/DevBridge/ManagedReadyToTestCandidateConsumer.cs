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
}

public sealed record ManagedCandidateCleanupEvidence(
    [property: JsonPropertyName("leaseReleased")] bool LeaseReleased = false,
    [property: JsonPropertyName("runtimeRestored")] bool RuntimeRestored = false,
    [property: JsonPropertyName("outcome")] string Outcome = "not-established",
    [property: JsonPropertyName("errorCode")] string? ErrorCode = null);
public sealed record GenericCandidateLoadEvidence(
    [property: JsonPropertyName("targetLoaded")] bool TargetLoaded,
    [property: JsonPropertyName("defsValid")] bool DefsValid,
    [property: JsonPropertyName("assemblyLoadState")] string AssemblyLoadState,
    [property: JsonPropertyName("generation")] int? Generation,
    [property: JsonPropertyName("evidenceSource")] string EvidenceSource,
    [property: JsonPropertyName("errorCode")] string? ErrorCode,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("attributableErrors")] IReadOnlyList<string> AttributableErrors)
{
    public static GenericCandidateLoadEvidence Failure(string errorCode, string error) =>
        new(false, false, "not-proven", null, "rimworld/list_mods", errorCode, error, []);
}


public sealed class ManagedReadyToTestCandidateConsumer
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ModeGates = [];
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
            if (managed.Status.Outcome != DevBridgeOutcomeKind.Success || managed.Success != true)
            {
                return FailureFor(
                    manifest,
                    ClassifyManagedFailure(managed.Status.ErrorCode, managed.Status.Outcome),
                    managed.Status.ErrorCode ?? "MANAGED_CANDIDATE_RUNTIME_FAILED",
                    managed.Status.Error ?? "The managed candidate runtime transaction failed.",
                    managed,
                    cleanup);
            }

            GenericCandidateLoadEvidence generic = await ReadGenericLoadEvidenceAsync(
                    runtimeTransport,
                    materialization!.Descriptor.ExpectedAssembly,
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
            bool success = hashMatches && targetConfigured && targetLoaded && defsValid && dependenciesReady;
            string outcome = success
                ? "PASS"
                : targetErrors.Count > 0
                    ? "MOD_FAILURE"
                    : !dependenciesReady
                        ? "DEPENDENCY_PREREQUISITE"
                        : "INFRASTRUCTURE_FAILURE";
            return new ManagedReadyToTestCandidateResult
            {
                Success = success,
                Outcome = outcome,
                ErrorCode = success ? null : generic.ErrorCode ??
                    (targetErrors.Count > 0 ? "CANDIDATE_TARGET_LOAD_FAILED" :
                        hashMatches ? "CANDIDATE_TARGET_NOT_PROVEN" : "CANDIDATE_DEPLOYED_HASH_MISMATCH"),
                Error = success ? null : generic.Error ??
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
        string packageId,
        int? expectedGeneration,
        CancellationToken cancellationToken)
    {
        DevBridgeProcessResult process = await runtimeTransport.ExecuteAsync(
                new DevBridgeProcessRequest(
                    options.BridgeOptions.CommandPath,
                    options.BridgeOptions.RootPath,
                    ["--root", options.BridgeOptions.RootPath, "bridge", "call",
                        "rimworld/list_mods", "{}", "--json"],
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
        if (process.ExitCode is not 0 ||
            !string.IsNullOrWhiteSpace(process.StartError) ||
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
            if (result.ValueKind is not JsonValueKind.Object and not JsonValueKind.Array)
            {
                return GenericCandidateLoadEvidence.Failure("RIMBRIDGE_LOAD_EVIDENCE_INVALID",
                    "RimBridge returned no structured generic mod-load evidence.");
            }
            List<string> errors = CollectTargetLoadErrors(result, packageId);
            bool targetLoaded = ContainsPackage(result, packageId);
            bool assemblyLoaded = !string.IsNullOrWhiteSpace(expectedAssembly) &&
                ContainsLoadedAssembly(result, expectedAssembly!);
            int? generation = ReadGeneration(envelope);
            return new GenericCandidateLoadEvidence(
                targetLoaded,
                targetLoaded && errors.Count == 0,
                assemblyLoaded ? "loaded-by-rimbridge-list-mods" : "not-reported",
                generation,
                "rimworld/list_mods",
                null,
                null,
                errors);
        }
        catch (JsonException exception)
        {
            return GenericCandidateLoadEvidence.Failure("RIMBRIDGE_LOAD_EVIDENCE_INVALID",
                Bound("RimBridge returned malformed generic mod-load evidence: " + exception.Message));
        }
    }

    private static bool ContainsPackage(JsonElement value, string packageId)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (property.Name.Equals("packageId", StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String &&
                    string.Equals(property.Value.GetString(), packageId, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (ContainsPackage(property.Value, packageId))
                    return true;
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
                if (ContainsPackage(item, packageId))
                    return true;
        }
        return false;
    }

    private static bool ContainsLoadedAssembly(JsonElement value, string expectedAssembly)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            bool loaded = !value.TryGetProperty("loaded", out JsonElement loadedValue) ||
                loadedValue.ValueKind != JsonValueKind.False;
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (property.Name.Equals("name", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Equals("assembly", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Equals("path", StringComparison.OrdinalIgnoreCase))
                {
                    if (loaded && property.Value.ValueKind == JsonValueKind.String &&
                        string.Equals(Path.GetFileName(property.Value.GetString()), expectedAssembly,
                            StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                if (ContainsLoadedAssembly(property.Value, expectedAssembly))
                    return true;
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
                if (ContainsLoadedAssembly(item, expectedAssembly))
                    return true;
        }
        return false;
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
            if (!string.IsNullOrWhiteSpace(status.Error))
                return RimBridgeRequirementResult.Failed("RIMBRIDGE_MODE_STATUS_FAILED", status.Error);
            if (string.IsNullOrWhiteSpace(status.RimBridgeMode))
                return RimBridgeRequirementResult.Failed(
                    "RIMBRIDGE_MODE_UNREPORTED",
                    "The coordinator status did not report an effective RimBridge mode.");
            if (string.Equals(status.RimBridgeMode, "required", StringComparison.OrdinalIgnoreCase))
                return RimBridgeRequirementResult.Success();
            if (!string.IsNullOrWhiteSpace(status.LeaseId))
                return RimBridgeRequirementResult.Failed(
                    "RIMBRIDGE_MODE_TRANSITION_BUSY",
                    "The existing DevBridge coordinator has an active lease; required RimBridge mode was not forced.");
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
            return new RuntimeStatusEvidence(null, [], result.StartError ?? result.Stderr, null, null, null, null, null);
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
            StringValue(rimBridge, "lifecycleState"));
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
        string? RimBridgeLifecycleState);
}
