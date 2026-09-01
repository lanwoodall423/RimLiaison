using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RimLiaison.DevBridge;
using RimLiaison.Toolchain;

namespace RimLiaison.Recovery;

internal sealed record PromotedToolchainInstallResult(
    bool Succeeded,
    string? ErrorCode = null,
    string? Error = null,
    string? PackagePath = null,
    string Action = "reinstall-qualified-promoted-package",
    string? PromotedSourceCommit = null);

internal interface IPromotedToolchainInstaller
{
    Task<PromotedToolchainInstallResult> RepairAsync(
        ProductionToolchainBindingFailure failure,
        CancellationToken cancellationToken);
}

internal sealed record PromotedToolchainRecoveryResult(
    PrerequisiteRecoveryState State,
    int Attempts,
    string? ErrorCode = null,
    string? Error = null,
    bool AlreadyRepaired = false,
    string Action = "reinstall-qualified-promoted-package",
    string Verification = "not-verified",
    long ElapsedRecoveryMilliseconds = 0,
    string? RecoveryPackagePath = null,
    string? PromotedSourceCommit = null)
{
    public bool Succeeded => State == PrerequisiteRecoveryState.Recovered;
}

public static partial class DevBridgeCapabilityRecovery
{
    private static readonly ConcurrentDictionary<
        string,
        Lazy<Task<PromotedToolchainRecoveryResult>>> PromotedToolchainInFlight = [];

    internal static async Task<PromotedToolchainRecoveryResult> RecoverPromotedToolchainAsync(
        ProductionToolchainBindingFailure failure,
        string repositoryRoot,
        IDevBridgeProcessTransport? transport = null,
        DevBridgeAdapterOptions? options = null,
        IPromotedToolchainInstaller? installer = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(failure);
        string key = Path.GetFullPath(failure.ManifestPath ?? string.Empty)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(key))
        {
            return ToolchainRecoveryFailure(
                failure,
                "PRODUCTION_TOOLCHAIN_RECOVERY_MANIFEST_UNAVAILABLE",
                "The authoritative production manifest path is unavailable.");
        }

        Lazy<Task<PromotedToolchainRecoveryResult>> lazy = PromotedToolchainInFlight.GetOrAdd(
            key,
            _ => new(
                () => RecoverPromotedToolchainCoreAsync(
                    failure,
                    repositoryRoot,
                    transport,
                    options,
                    installer ?? new QualifiedPromotedToolchainInstaller()),
                LazyThreadSafetyMode.ExecutionAndPublication));
        Task<PromotedToolchainRecoveryResult> task = lazy.Value;
        try
        {
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (task.IsCompleted)
            {
                PromotedToolchainInFlight.TryRemove(
                    new KeyValuePair<string, Lazy<Task<PromotedToolchainRecoveryResult>>>(key, lazy));
            }
        }
    }

    private static async Task<PromotedToolchainRecoveryResult> RecoverPromotedToolchainCoreAsync(
        ProductionToolchainBindingFailure failure,
        string repositoryRoot,
        IDevBridgeProcessTransport? transport,
        DevBridgeAdapterOptions? options,
        IPromotedToolchainInstaller installer)
    {
        long started = Stopwatch.GetTimestamp();
        ProductionToolchainBindingResolution initial =
            ProductionToolchainBindingResolver.ResolvePromotedIdentity(repositoryRoot);
        if (initial.Succeeded)
            return new(
                PrerequisiteRecoveryState.Recovered,
                0,
                ErrorCode: failure.ErrorCode,
                AlreadyRepaired: true,
                Action: "revalidate-promoted-package",
                Verification: "promoted-identity-verified",
                ElapsedRecoveryMilliseconds: ToolchainElapsedMilliseconds(started));
        string? manifestPath = failure.ManifestPath;
        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            return ToolchainRecoveryFailure(
                failure,
                "PRODUCTION_TOOLCHAIN_RECOVERY_MANIFEST_UNAVAILABLE",
                "The authoritative production manifest path is unavailable.",
                started);
        }

        FileStream? recoveryLock = null;
        string lockPath = manifestPath + ".recovery.lock";
        try
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                ProductionToolchainBindingResolution revalidated =
                    ProductionToolchainBindingResolver.ResolvePromotedIdentity(repositoryRoot);
                if (revalidated.Succeeded)
                {
                    return new(
                        PrerequisiteRecoveryState.Recovered,
                        0,
                        ErrorCode: failure.ErrorCode,
                        AlreadyRepaired: true,
                        Action: "revalidate-after-concurrent-repair",
                        Verification: "promoted-identity-verified",
                        ElapsedRecoveryMilliseconds: ElapsedMilliseconds(started));
                }

                try
                {
                    recoveryLock = new FileStream(
                        lockPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None);
                    break;
                }
                catch (IOException)
                {
                    await Task.Delay(100).ConfigureAwait(false);
                }
                catch (UnauthorizedAccessException exception)
                {
                    return ToolchainRecoveryFailure(
                        failure,
                        "PRODUCTION_TOOLCHAIN_RECOVERY_LOCK_FAILED",
                        BoundToolchain(exception.Message),
                        started);
                }
            }

            if (recoveryLock is null)
            {
                return ToolchainRecoveryFailure(
                    failure,
                    "PRODUCTION_TOOLCHAIN_RECOVERY_CONTENTION",
                    "Another production-toolchain recovery did not complete within the bounded recovery window.",
                    started,
                    PrerequisiteRecoveryState.Contended);
            }

            ProductionToolchainBindingResolution afterLock =
                ProductionToolchainBindingResolver.ResolvePromotedIdentity(repositoryRoot);
            if (afterLock.Succeeded)
            {
                return new(
                    PrerequisiteRecoveryState.Recovered,
                    0,
                    ErrorCode: failure.ErrorCode,
                    AlreadyRepaired: true,
                    Action: "revalidate-after-lock",
                    Verification: "promoted-identity-verified",
                    ElapsedRecoveryMilliseconds: ElapsedMilliseconds(started));
            }

            LegacyPromotionMigrationResult migration =
                LegacyPromotionMigrationService.Ensure(repositoryRoot);
            if (migration.State == LegacyPromotionMigrationState.Blocked)
            {
                return ToolchainRecoveryFailure(
                    failure,
                    migration.ErrorCode ?? "PRODUCTION_TOOLCHAIN_LEGACY_RECOVERY_UNAVAILABLE",
                    migration.Error ?? "The active legacy promotion could not be made self-restorable.",
                    started,
                    PrerequisiteRecoveryState.RecoveryFailed,
                    migration.NextAction ?? "Create and intentionally promote a new qualified RimLiaison production package.");
            }

            PromotedToolchainInstallResult installation = await installer
                .RepairAsync(failure, CancellationToken.None)
                .ConfigureAwait(false);
            if (!installation.Succeeded)
            {
                return ToolchainRecoveryFailure(
                    failure,
                    installation.ErrorCode ?? "PRODUCTION_TOOLCHAIN_RECOVERY_FAILED",
                    installation.Error ?? "The qualified promoted package could not be reinstalled.",
                    started,
                    installation.ErrorCode == "PRODUCTION_TOOLCHAIN_RECOVERY_CONTENTION"
                        ? PrerequisiteRecoveryState.Contended
                        : PrerequisiteRecoveryState.RecoveryFailed,
                    installation.Action);
            }

            ProductionToolchainBindingResolution verified =
                ProductionToolchainBindingResolver.ResolvePromotedIdentity(repositoryRoot);
            if (!verified.Succeeded)
            {
                return ToolchainRecoveryFailure(
                    failure,
                    verified.Failure?.ErrorCode ?? "PRODUCTION_TOOLCHAIN_RECOVERY_VERIFICATION_FAILED",
                    verified.Failure?.Error ?? "The reinstalled package did not prove the promoted identity.",
                    started,
                    PrerequisiteRecoveryState.RecoveryFailed,
                    "verify-promoted-identity");
            }

            if (transport is not null && options is not null)
            {
                DevBridgeCapabilityRecoveryResult runtimeRecovery = await RecoverAsync(
                        transport,
                        options,
                        workflowId: null,
                        triggerCode: failure.ErrorCode,
                        checkpoint: ProductionCheckpoint.PreMutation)
                    .ConfigureAwait(false);
                if (!runtimeRecovery.Succeeded)
                {
                    return ToolchainRecoveryFailure(
                        failure,
                        runtimeRecovery.ErrorCode ?? "PRODUCTION_TOOLCHAIN_RUNTIME_NOT_READY",
                        runtimeRecovery.Error ?? "The promoted package was installed but readiness was not re-established.",
                        started,
                        PrerequisiteRecoveryState.RecoveryFailed,
                        "verify-promoted-readiness");
                }
            }

            ProductionToolchainBindingResolution final =
                ProductionToolchainBindingResolver.ResolvePromotedIdentity(repositoryRoot);
            return final.Succeeded
                ? new(
                    PrerequisiteRecoveryState.Recovered,
                    1,
                    ErrorCode: failure.ErrorCode,
                    Action: installation.Action,
                    Verification: "promoted-identity-and-readiness-verified",
                    ElapsedRecoveryMilliseconds: ElapsedMilliseconds(started),
                    RecoveryPackagePath: installation.PackagePath,
                    PromotedSourceCommit: installation.PromotedSourceCommit)
                : ToolchainRecoveryFailure(
                    failure,
                    final.Failure?.ErrorCode ?? "PRODUCTION_TOOLCHAIN_RECOVERY_VERIFICATION_FAILED",
                    final.Failure?.Error ?? "The promoted identity was not stable after verification.",
                    started,
                    PrerequisiteRecoveryState.RecoveryFailed,
                    "verify-promoted-identity");
        }
        catch (UnauthorizedAccessException exception)
        {
            return ToolchainRecoveryFailure(
                failure,
                "PRODUCTION_TOOLCHAIN_RECOVERY_WRITE_FAILED",
                BoundToolchain(exception.Message),
                started);
        }
        catch (IOException exception)
        {
            return ToolchainRecoveryFailure(
                failure,
                "PRODUCTION_TOOLCHAIN_RECOVERY_WRITE_FAILED",
                BoundToolchain(exception.Message),
                started);
        }
        finally
        {
            recoveryLock?.Dispose();
            try
            {
                if (File.Exists(lockPath)) File.Delete(lockPath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static PromotedToolchainRecoveryResult ToolchainRecoveryFailure(
        ProductionToolchainBindingFailure failure,
        string code,
        string error,
        long? started = null,
        PrerequisiteRecoveryState state = PrerequisiteRecoveryState.RecoveryFailed,
        string action = "reinstall-qualified-promoted-package") =>
        new(
            state,
            1,
            code,
            error,
            Action: action,
            Verification: "not-verified",
            ElapsedRecoveryMilliseconds: started is long value ? ToolchainElapsedMilliseconds(value) : 0);

    private static long ToolchainElapsedMilliseconds(long started) =>
        Math.Max(0, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);

    private static string BoundToolchain(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? "The production-toolchain repair failed without a bounded diagnostic."
            : value.Trim().Length <= 1024 ? value.Trim() : value.Trim()[..1024];
}

internal sealed class QualifiedPromotedToolchainInstaller : IPromotedToolchainInstaller
{
    private const string PackageEnvironment = "RIMLIAISON_PROMOTION_PACKAGE";
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

    public Task<PromotedToolchainInstallResult> RepairAsync(
        ProductionToolchainBindingFailure failure,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (string.IsNullOrWhiteSpace(failure.ManifestPath) ||
                !File.Exists(failure.ManifestPath))
            {
                return Task.FromResult(Fail(
                    "PRODUCTION_TOOLCHAIN_RECOVERY_MANIFEST_UNAVAILABLE",
                    "The authoritative promoted production manifest is unavailable."));
            }

            ProductionToolchainManifest? manifest = JsonSerializer.Deserialize<ProductionToolchainManifest>(
                File.ReadAllText(failure.ManifestPath), ReadOptions);
            string? packagePath = manifest?.PromotionPackagePath ??
                Environment.GetEnvironmentVariable(PackageEnvironment);
            if (manifest is null || string.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath))
            {
                return Task.FromResult(Fail(
                    "PRODUCTION_TOOLCHAIN_RECOVERY_PACKAGE_UNAVAILABLE",
                    "The immutable qualified promotion package required for repair is unavailable."));
            }

            packagePath = Path.GetFullPath(packagePath);
            if (!string.IsNullOrWhiteSpace(manifest.PromotionPackageSha256) &&
                !string.Equals(
                    ToolchainFileHash.Sha256(packagePath),
                    manifest.PromotionPackageSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(Fail(
                    "PRODUCTION_TOOLCHAIN_RECOVERY_PACKAGE_HASH_MISMATCH",
                    "The authoritative promotion package failed its recorded integrity check."));
            }

            ToolchainPromotionPackage? package = ToolchainPromotionService.ReadPackage(
                packagePath,
                out string? packageError);
            string? validationError = package is null
                ? packageError ?? "The immutable promotion package could not be loaded."
                : ValidatePackage(package, manifest);
            if (validationError is not null)
            {
                return Task.FromResult(Fail(
                    "PRODUCTION_TOOLCHAIN_RECOVERY_PACKAGE_INVALID",
                    validationError));
            }
            string qualificationPath = Path.GetFullPath(package!.QualificationArtifactPath!);
            if (!File.Exists(qualificationPath) ||
                !string.Equals(
                    ToolchainFileHash.Sha256(qualificationPath),
                    package.QualificationArtifactSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(Fail(
                    "PRODUCTION_TOOLCHAIN_RECOVERY_PACKAGE_INVALID",
                    "The immutable qualification artifact is missing or failed its recorded hash."));
            }
            using JsonDocument qualification = JsonDocument.Parse(File.ReadAllText(qualificationPath));
            bool proofValid = ToolchainPromotionService.PromotionProofIsComplete(
                qualification.RootElement,
                package.SourceCommit,
                out string? proofError);
            bool hashesValid = ToolchainPromotionService.QualifiedHashesMatch(
                qualification.RootElement,
                package,
                out string? artifactHashError);
            if (!proofValid || !hashesValid)
            {
                return Task.FromResult(Fail(
                    "PRODUCTION_TOOLCHAIN_RECOVERY_PACKAGE_INVALID",
                    proofError ?? artifactHashError ?? "The qualified promotion proof is incomplete."));
            }

            string artifactRoot = Path.GetFullPath(package!.ArtifactRoot!);
            string? sourceCli = SafePath(artifactRoot, package.RimLiaisonExecutableRelativePath);
            string? sourceAssembly = SafePath(artifactRoot, package.RimLiaisonAssemblyRelativePath);
            string? sourceConsumer = SafePath(artifactRoot, package.TransactionConsumerRelativePath);
            string? sourceUnified = SafePath(artifactRoot, package.UnifiedManifestRelativePath);
            if (sourceCli is null || sourceAssembly is null || sourceConsumer is null ||
                !File.Exists(sourceCli) || !File.Exists(sourceAssembly) ||
                !File.Exists(sourceConsumer))
            {
                return Task.FromResult(Fail(
                    "PRODUCTION_TOOLCHAIN_RECOVERY_PACKAGE_ARTIFACT_MISSING",
                    $"The immutable qualified package is incomplete; no replacement was installed (artifactRoot={artifactRoot}, cli={sourceCli}, assembly={sourceAssembly}, consumer={sourceConsumer})."));
            }

            if (!HashEquals(sourceCli, package.RimLiaisonExecutableSha256) ||
                !HashEquals(sourceAssembly, package.RimLiaisonAssemblySha256) ||
                !HashEquals(sourceConsumer, package.TransactionConsumerSha256))
            {
                return Task.FromResult(Fail(
                    "PRODUCTION_TOOLCHAIN_RECOVERY_PACKAGE_HASH_MISMATCH",
                    "The immutable qualified package artifacts do not match their qualified hashes."));
            }

            bool runtimeFault =
                failure.ErrorCode.Contains("RUNTIME", StringComparison.OrdinalIgnoreCase) ||
                failure.ErrorCode.Contains("COORDINATOR", StringComparison.OrdinalIgnoreCase) ||
                failure.ErrorCode.Contains("ARTIFACT_UNREADABLE", StringComparison.OrdinalIgnoreCase) ||
                failure.MismatchingArtifacts?.Any(path =>
                    IsRuntimeArtifact(path, manifest.DevBridgeRuntimeRoot!)) == true ||
                failure.ExpectedArtifacts?.Any(path =>
                    IsRuntimeArtifact(path, manifest.DevBridgeRuntimeRoot!) &&
                    !File.Exists(path)) == true;
            if (runtimeFault)
            {
                if (string.IsNullOrWhiteSpace(package.DevBridgeRuntimeArtifactRoot) ||
                    !Directory.Exists(package.DevBridgeRuntimeArtifactRoot))
                {
                    return Task.FromResult(Fail(
                        "PRODUCTION_TOOLCHAIN_RECOVERY_PACKAGE_UNAVAILABLE",
                        "The qualified package contains no immutable DevBridge runtime materialization source."));
                }

                string runtimeManifest = Path.Combine(
                    package.DevBridgeRuntimeArtifactRoot,
                    ".devbridge-runtime-manifest.json");
                string coordinator = Path.Combine(
                    package.DevBridgeRuntimeArtifactRoot,
                    "Coordinator",
                    "DevBridge.Coordinator.exe");
                if (!File.Exists(runtimeManifest) ||
                    !File.Exists(coordinator) ||
                    !string.Equals(
                        ReadRuntimePackageHash(runtimeManifest),
                        package.DevBridgePackageSha256,
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(
                        ReadRuntimeFileHash(runtimeManifest, "Coordinator/DevBridge.Coordinator.exe"),
                        package.DevBridgeCoordinatorSha256,
                        StringComparison.OrdinalIgnoreCase) ||
                    !HashEquals(coordinator, package.DevBridgeCoordinatorSha256))
                {
                    return Task.FromResult(Fail(
                        "PRODUCTION_TOOLCHAIN_RECOVERY_PACKAGE_INVALID",
                        "The immutable qualified DevBridge runtime snapshot is missing or failed its recorded hashes."));
                }
            }

            string unifiedText = sourceUnified is not null && File.Exists(sourceUnified)
                ? File.ReadAllText(sourceUnified)
                : BuildUnifiedManifest(manifest, package);
            if (!string.Equals(
                    Sha256Text(unifiedText),
                    manifest.UnifiedManifestSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(Fail(
                    "PRODUCTION_TOOLCHAIN_RECOVERY_PACKAGE_INVALID",
                    "The qualified package cannot reproduce the promoted unified manifest identity."));
            }

            // Validate every source before changing the installed product. A bad
            // qualified package must never leave a partially repaired install.
            if (runtimeFault)
            {
                CopyDirectory(
                    package.DevBridgeRuntimeArtifactRoot!,
                    manifest.DevBridgeRuntimeRoot!);
            }
            CopyFile(sourceCli, manifest.RimLiaisonExecutablePath!);
            CopyFile(sourceAssembly, manifest.RimLiaisonAssemblyPath!);
            CopyFile(sourceConsumer, manifest.TransactionConsumerPath!);
            Directory.CreateDirectory(Path.GetDirectoryName(manifest.UnifiedManifestPath!)!);
            File.WriteAllText(manifest.UnifiedManifestPath!, unifiedText);

            return Task.FromResult(new PromotedToolchainInstallResult(
                true,
                PackagePath: packagePath,
                PromotedSourceCommit: package.SourceCommit));
        }
        catch (UnauthorizedAccessException exception)
        {
            return Task.FromResult(Fail("PRODUCTION_TOOLCHAIN_RECOVERY_WRITE_FAILED", exception.Message));
        }
        catch (IOException exception)
        {
            return Task.FromResult(Fail("PRODUCTION_TOOLCHAIN_RECOVERY_WRITE_FAILED", exception.Message));
        }
        catch (JsonException exception)
        {
            return Task.FromResult(Fail("PRODUCTION_TOOLCHAIN_RECOVERY_PACKAGE_INVALID", exception.Message));
        }
    }

    private static string BuildUnifiedManifest(
        ProductionToolchainManifest manifest,
        ToolchainPromotionPackage package)
    {
        string unifiedRoot = Path.GetDirectoryName(manifest.UnifiedManifestPath!)!;
        string Relative(string path) => Path.GetRelativePath(unifiedRoot, path)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');

        return JsonSerializer.Serialize(
            new
            {
                schemaVersion = "rimliaison-unified-production-package/v2",
                productFingerprint = manifest.PromotedFingerprint,
                ownerProduct = ToolchainPromotionSchemas.OwnerProduct,
                runtimeSubsystem = ToolchainPromotionSchemas.RuntimeSubsystem,
                rimBridgeServer = new
                {
                    boundary = ToolchainPromotionSchemas.RimBridgeServerBoundary,
                    ownership = "RimBridgeServer"
                },
                sourceCommit = package.SourceCommit,
                rimLiaison = new
                {
                    executablePath = Relative(manifest.RimLiaisonExecutablePath!),
                    executableSha256 = manifest.RimLiaisonExecutableSha256,
                    assemblyPath = Relative(manifest.RimLiaisonAssemblyPath!),
                    assemblySha256 = manifest.RimLiaisonAssemblySha256
                },
                runtime = new
                {
                    packageSha256 = package.DevBridgePackageSha256,
                    coordinatorSha256 = package.DevBridgeCoordinatorSha256
                },
                transactionConsumer = new
                {
                    path = Relative(manifest.TransactionConsumerPath!),
                    sha256 = package.TransactionConsumerSha256
                }
            },
            WriteOptions);
    }

    private static string Sha256Text(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string? ValidatePackage(
        ToolchainPromotionPackage? package,
        ProductionToolchainManifest manifest)
    {
        if (package is null || package.SchemaVersion != ToolchainPromotionSchemas.Package ||
            !string.Equals(package.OwnerProduct, ToolchainPromotionSchemas.OwnerProduct, StringComparison.Ordinal) ||
            !string.Equals(package.SourceCommit, manifest.QualifiedSourceCommit, StringComparison.OrdinalIgnoreCase))
        {
            return "The promotion package source commit differs from the promoted production identity.";
        }
        if (!SamePath(package.DevBridgeRuntimeRoot, manifest.DevBridgeRuntimeRoot))
        {
            return "The promotion package runtime root differs from the promoted production identity.";
        }
        if (!string.Equals(package.RimLiaisonExecutableSha256, manifest.RimLiaisonExecutableSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(package.RimLiaisonAssemblySha256, manifest.RimLiaisonAssemblySha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(package.DevBridgePackageSha256, manifest.DevBridgePackageSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(package.DevBridgeCoordinatorSha256, manifest.DevBridgeCoordinatorSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(package.TransactionConsumerSha256, manifest.TransactionConsumerSha256, StringComparison.OrdinalIgnoreCase))
        {
            return "The promotion package identity does not equal the already-promoted production identity.";
        }
        string fingerprint = ToolchainPromotionService.ComputePromotedFingerprint(
            package.SourceCommit!,
            package.RimLiaisonExecutableSha256!,
            package.RimLiaisonAssemblySha256!,
            package.DevBridgeCoordinatorSha256!,
            package.DevBridgePackageSha256!,
            package.TransactionConsumerSha256!,
            package.RuntimeProtocolContract!,
            package.OwnerProduct!,
            package.RuntimeSubsystem!);
        if (!string.Equals(fingerprint, manifest.PromotedFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            return "The qualified package fingerprint does not equal the previously promoted identity.";
        }
        return null;
    }

    private static PromotedToolchainInstallResult Fail(string code, string error) =>
        new(false, code, error);

    private static bool HashEquals(string path, string? expected) =>
        !string.IsNullOrWhiteSpace(expected) &&
        string.Equals(ToolchainFileHash.Sha256(path), expected, StringComparison.OrdinalIgnoreCase);

    private static string ReadRuntimePackageHash(string runtimeManifestPath)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(runtimeManifestPath));
        return document.RootElement.TryGetProperty("packageSha256", out JsonElement value)
            ? value.GetString() ?? string.Empty
            : string.Empty;
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

    private static bool IsRuntimeArtifact(string path, string runtimeRoot)
    {
        string root = Path.GetFullPath(runtimeRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string candidate = Path.GetFullPath(path);
        return candidate.Equals(
                   Path.Combine(root, "DevBridge.cmd"),
                   StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }


    private static void CopyFile(string source, string target)
    {
        string fullSource = Path.GetFullPath(source);
        string fullTarget = Path.GetFullPath(target);
        if (File.Exists(fullTarget) &&
            string.Equals(
                ToolchainFileHash.Sha256(fullSource),
                ToolchainFileHash.Sha256(fullTarget),
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(fullTarget)!);
        string temporary = fullTarget + "." + Guid.NewGuid().ToString("N") + ".recovery";
        try
        {
            File.Copy(source, temporary, overwrite: false);
            File.Move(temporary, fullTarget, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        string fullSource = Path.GetFullPath(source);
        string fullDestination = Path.GetFullPath(destination);
        Directory.CreateDirectory(fullDestination);
        foreach (string directory in Directory.EnumerateDirectories(fullSource, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(fullDestination, Path.GetRelativePath(fullSource, directory)));
        }
        foreach (string file in Directory.EnumerateFiles(fullSource, "*", SearchOption.AllDirectories))
        {
            CopyFile(file, Path.Combine(fullDestination, Path.GetRelativePath(fullSource, file)));
        }
    }

    private static string? SafePath(string root, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':')) return null;
        string candidate = Path.GetFullPath(Path.Combine(root, relative));
        string boundary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(boundary, StringComparison.OrdinalIgnoreCase) ? candidate : null;
    }

    private static bool SamePath(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
