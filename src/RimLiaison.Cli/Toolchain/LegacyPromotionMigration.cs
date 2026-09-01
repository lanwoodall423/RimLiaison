using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RimLiaison.Toolchain;

internal enum LegacyPromotionMigrationState
{
    NotRequired,
    Migrated,
    Blocked
}

internal sealed record LegacyPromotionMigrationResult(
    LegacyPromotionMigrationState State,
    string? ErrorCode = null,
    string? Error = null,
    string? NextAction = null,
    string? PromotedSourceCommit = null,
    string? PromotedFingerprint = null,
    string? RecoveryPackagePath = null,
    long ElapsedMilliseconds = 0)
{
    public bool Succeeded => State is LegacyPromotionMigrationState.NotRequired or LegacyPromotionMigrationState.Migrated;
    public bool Migrated => State == LegacyPromotionMigrationState.Migrated;
}

/// <summary>
/// Upgrades an already-active legacy promotion without qualifying or promoting
/// anything. Every candidate is checked against the active identity first.
/// </summary>
internal static class LegacyPromotionMigrationService
{
    private const string ManifestEnvironment = "RIMLIAISON_PRODUCTION_TOOLCHAIN_MANIFEST";
    private const string DefaultManifest = "C:/RimDev/.rimdev/production-toolchain.json";
    private const string RecoveryDirectoryName = "promotion-recovery";
    private const string LegacyRecoveryCode = "PRODUCTION_TOOLCHAIN_LEGACY_RECOVERY_UNAVAILABLE";
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

    internal static LegacyPromotionMigrationResult Ensure(
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        long started = Environment.TickCount64;
        string manifestPath = Environment.GetEnvironmentVariable(ManifestEnvironment) ?? DefaultManifest;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryReadManifest(manifestPath, out ProductionToolchainManifest? manifest) ||
                manifest is null)
            {
                return NotRequired(started);
            }
            bool hadRecoveryReference = !string.IsNullOrWhiteSpace(manifest.PromotionPackagePath) ||
                !string.IsNullOrWhiteSpace(manifest.PromotionPackageSha256);
            if (HasModernRecoveryReference(manifest, out string? modernError))
            {
                return new(
                    LegacyPromotionMigrationState.NotRequired,
                    PromotedSourceCommit: manifest.QualifiedSourceCommit,
                    PromotedFingerprint: manifest.PromotedFingerprint,
                    RecoveryPackagePath: manifest.PromotionPackagePath,
                    ElapsedMilliseconds: Elapsed(started));
            }

            if (string.IsNullOrWhiteSpace(manifest.QualifiedSourceCommit) ||
                string.IsNullOrWhiteSpace(manifest.PromotedFingerprint))
            {
                return Blocked(
                    manifest,
                    "The active legacy promotion has no complete source or product identity.",
                    started);
            }

            string lockPath = manifestPath + ".migration.lock";
            using FileStream migrationLock = AcquireLock(lockPath, cancellationToken);
            if (HasModernRecoveryReference(manifest, out modernError))
            {
                return new(
                    LegacyPromotionMigrationState.NotRequired,
                    PromotedSourceCommit: manifest.QualifiedSourceCommit,
                    PromotedFingerprint: manifest.PromotedFingerprint,
                    RecoveryPackagePath: manifest.PromotionPackagePath,
                    ElapsedMilliseconds: Elapsed(started));
            }

            HistoricalCandidate? candidate = DiscoverExactCandidate(
                repositoryRoot,
                manifestPath,
                manifest,
                cancellationToken);
            if (candidate is null)
            {
                return Blocked(
                    manifest,
                    "The active legacy promotion cannot be reconstructed because no exact immutable qualified recovery payload is available.",
                    started,
                    hadRecoveryReference
                        ? "PRODUCTION_TOOLCHAIN_RECOVERY_PACKAGE_UNAVAILABLE"
                        : LegacyRecoveryCode);
            }

            string recoveryRoot = Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(manifestPath))!,
                RecoveryDirectoryName);
            string payloadRoot = Path.Combine(
                recoveryRoot,
                "qualified-toolchain-payload-" + manifest.QualifiedSourceCommit);
            string packagePath = Path.Combine(
                recoveryRoot,
                "qualified-toolchain-package-" + manifest.QualifiedSourceCommit + ".json");
            Directory.CreateDirectory(recoveryRoot);
            MaterializePayload(candidate, payloadRoot, cancellationToken);
            ToolchainPromotionPackage package = CreatePackage(candidate, payloadRoot);
            string packageJson = JsonSerializer.Serialize(package, WriteOptions);
            if (File.Exists(packagePath))
            {
                ToolchainPromotionPackage? existing = ToolchainPromotionService.ReadPackage(
                    packagePath,
                    out string? packageError);
                HistoricalCandidate? existingCandidate = existing is null
                    ? null
                    : CandidateMatches(manifest, existing, out HistoricalCandidate? validated)
                        ? validated
                        : null;
                if (existing is null || existingCandidate is null || !PackageMatches(existing, package, out _))
                {
                    return Blocked(
                        manifest,
                        packageError ?? "The durable legacy recovery package conflicts with the active promoted identity.",
                        started,
                        "PRODUCTION_TOOLCHAIN_LEGACY_RECOVERY_PACKAGE_CONFLICT");
                }
            }
            else
            {
                WriteCreateNew(packagePath, packageJson);
            }

            string packageHash = ToolchainFileHash.Sha256(packagePath);
            ProductionToolchainManifest migrated = WithRecoveryReference(manifest, packagePath, packageHash);
            AtomicReplace(manifestPath, JsonSerializer.Serialize(migrated, WriteOptions));

            return new(
                LegacyPromotionMigrationState.Migrated,
                PromotedSourceCommit: manifest.QualifiedSourceCommit,
                PromotedFingerprint: manifest.PromotedFingerprint,
                RecoveryPackagePath: packagePath,
                ElapsedMilliseconds: Elapsed(started));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnauthorizedAccessException exception)
        {
            return Blocked(
                null,
                "The legacy promotion recovery payload could not be persisted: " + Bound(exception.Message),
                started,
                "PRODUCTION_TOOLCHAIN_LEGACY_RECOVERY_WRITE_FAILED");
        }
        catch (IOException exception)
        {
            return Blocked(
                null,
                "The legacy promotion recovery payload could not be persisted: " + Bound(exception.Message),
                started,
                "PRODUCTION_TOOLCHAIN_LEGACY_RECOVERY_WRITE_FAILED");
        }
        catch (JsonException exception)
        {
            return Blocked(
                null,
                "Historical promotion material was malformed: " + Bound(exception.Message),
                started,
                "PRODUCTION_TOOLCHAIN_LEGACY_RECOVERY_PACKAGE_INVALID");
        }
    }

    private static HistoricalCandidate? DiscoverExactCandidate(
        string repositoryRoot,
        string manifestPath,
        ProductionToolchainManifest manifest,
        CancellationToken cancellationToken)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddDirectory(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
            foreach (string path in Directory.EnumerateFiles(
                         directory,
                         "qualified-toolchain-package-*.json",
                         SearchOption.TopDirectoryOnly))
            {
                paths.Add(Path.GetFullPath(path));
            }
        }

        string? qualificationPath = manifest.QualificationArtifactPath;
        AddDirectory(Path.GetDirectoryName(qualificationPath ?? string.Empty));
        AddDirectory(Path.Combine(repositoryRoot, ".rimdev", "qualification"));
        AddDirectory(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(manifestPath))!, "qualification"));
        if (!string.IsNullOrWhiteSpace(manifest.PromotionPackagePath) &&
            File.Exists(manifest.PromotionPackagePath))
        {
            paths.Add(Path.GetFullPath(manifest.PromotionPackagePath));
        }

        foreach (string path in paths.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase))
        {
            if (!TryReadPackage(path, out ToolchainPromotionPackage? package) ||
                package is null) continue;
            if (!CandidateMatches(manifest, package, out HistoricalCandidate? candidate) ||
                candidate is null) continue;
            return candidate with { PackagePath = path };
        }
        return null;
    }

    private static bool CandidateMatches(
        ProductionToolchainManifest manifest,
        ToolchainPromotionPackage package,
        out HistoricalCandidate? candidate)
    {
        candidate = null;
        if (!string.Equals(package.SourceCommit, manifest.QualifiedSourceCommit, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(package.OwnerProduct, ToolchainPromotionSchemas.OwnerProduct, StringComparison.Ordinal) ||
            !string.Equals(package.RuntimeSubsystem, ToolchainPromotionSchemas.RuntimeSubsystem, StringComparison.Ordinal) ||
            !string.Equals(package.RuntimeProtocolContract, manifest.RuntimeProtocolContract, StringComparison.Ordinal) ||
            !SamePath(package.DevBridgeRuntimeRoot, manifest.DevBridgeRuntimeRoot) ||
            !string.Equals(package.RimLiaisonExecutableSha256, manifest.RimLiaisonExecutableSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(package.RimLiaisonAssemblySha256, manifest.RimLiaisonAssemblySha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(package.DevBridgePackageSha256, manifest.DevBridgePackageSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(package.DevBridgeCoordinatorSha256, manifest.DevBridgeCoordinatorSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(package.TransactionConsumerSha256, manifest.TransactionConsumerSha256, StringComparison.OrdinalIgnoreCase))
        {
            return false;
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
            return false;

        string artifactRoot = FullPath(package.ArtifactRoot);
        string? cli = SafePath(artifactRoot, package.RimLiaisonExecutableRelativePath);
        string? assembly = SafePath(artifactRoot, package.RimLiaisonAssemblyRelativePath);
        string? consumer = SafePath(artifactRoot, package.TransactionConsumerRelativePath);
        if (cli is null || assembly is null || consumer is null ||
            !HashEquals(cli, package.RimLiaisonExecutableSha256) ||
            !HashEquals(assembly, package.RimLiaisonAssemblySha256) ||
            !HashEquals(consumer, package.TransactionConsumerSha256))
        {
            return false;
        }

        string qualificationPath = FullPath(package.QualificationArtifactPath);
        if (!File.Exists(qualificationPath) ||
            !string.Equals(ToolchainFileHash.Sha256(qualificationPath), package.QualificationArtifactSha256, StringComparison.OrdinalIgnoreCase))
            return false;
        using JsonDocument qualification = JsonDocument.Parse(File.ReadAllText(qualificationPath));
        if (!ToolchainPromotionService.PromotionProofIsComplete(
                qualification.RootElement,
                package.SourceCommit,
                out _) ||
            !ToolchainPromotionService.QualifiedHashesMatch(qualification.RootElement, package, out _))
        {
            return false;
        }

        string? unified = SafePath(artifactRoot, package.UnifiedManifestRelativePath);
        string unifiedText = unified is not null && File.Exists(unified)
            ? File.ReadAllText(unified)
            : BuildUnifiedManifest(manifest, package);
        if (!string.Equals(
                Sha256Text(unifiedText),
                manifest.UnifiedManifestSha256,
                StringComparison.OrdinalIgnoreCase))
            return false;

        string runtimeSource = !string.IsNullOrWhiteSpace(package.DevBridgeRuntimeArtifactRoot) &&
                               Directory.Exists(package.DevBridgeRuntimeArtifactRoot)
            ? package.DevBridgeRuntimeArtifactRoot!
            : FullPath(package.DevBridgeRuntimeRoot);
        if (!RuntimeMatches(runtimeSource, package)) return false;

        candidate = new(
            string.Empty,
            package,
            cli,
            assembly,
            consumer,
            unifiedText,
            runtimeSource,
            qualificationPath);
        return true;
    }

    private static void MaterializePayload(
        HistoricalCandidate candidate,
        string payloadRoot,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(payloadRoot);
        CopyImmutable(candidate.CliPath, Path.Combine(payloadRoot, candidate.Package.RimLiaisonExecutableRelativePath!));
        CopyImmutable(candidate.AssemblyPath, Path.Combine(payloadRoot, candidate.Package.RimLiaisonAssemblyRelativePath!));
        CopyImmutable(candidate.ConsumerPath, Path.Combine(payloadRoot, candidate.Package.TransactionConsumerRelativePath!));
        CopyImmutable(candidate.QualificationPath, Path.Combine(payloadRoot, "qualification.json"));
        string unifiedPath = Path.Combine(payloadRoot, candidate.Package.UnifiedManifestRelativePath!);
        if (File.Exists(unifiedPath))
        {
            if (!string.Equals(
                    ToolchainFileHash.Sha256(unifiedPath),
                    Sha256Text(candidate.UnifiedText),
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The immutable recovery payload contains a substituted unified manifest.");
        }
        else
        {
            File.WriteAllText(unifiedPath, candidate.UnifiedText, new UTF8Encoding(false));
        }
        string runtimeRoot = Path.Combine(payloadRoot, "runtime");
        Directory.CreateDirectory(runtimeRoot);
        foreach (string file in Directory.EnumerateFiles(candidate.RuntimeSource, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            CopyImmutable(file, Path.Combine(runtimeRoot, Path.GetRelativePath(candidate.RuntimeSource, file)));
        }
    }

    private static ToolchainPromotionPackage CreatePackage(HistoricalCandidate candidate, string payloadRoot) =>
        new()
        {
            SchemaVersion = ToolchainPromotionSchemas.Package,
            SourceCommit = candidate.Package.SourceCommit,
            QualificationArtifactPath = Path.Combine(payloadRoot, "qualification.json"),
            QualificationArtifactSha256 = ToolchainFileHash.Sha256(Path.Combine(payloadRoot, "qualification.json")),
            ArtifactRoot = payloadRoot,
            RimLiaisonExecutableRelativePath = candidate.Package.RimLiaisonExecutableRelativePath,
            RimLiaisonAssemblyRelativePath = candidate.Package.RimLiaisonAssemblyRelativePath,
            RimLiaisonExecutableSha256 = candidate.Package.RimLiaisonExecutableSha256,
            RimLiaisonAssemblySha256 = candidate.Package.RimLiaisonAssemblySha256,
            OwnerProduct = candidate.Package.OwnerProduct,
            RuntimeSubsystem = candidate.Package.RuntimeSubsystem,
            DevBridgeRuntimeRoot = candidate.Package.DevBridgeRuntimeRoot,
            DevBridgeRuntimeArtifactRoot = Path.Combine(payloadRoot, "runtime"),
            DevBridgePackageSha256 = candidate.Package.DevBridgePackageSha256,
            DevBridgeCoordinatorSha256 = candidate.Package.DevBridgeCoordinatorSha256,
            TransactionConsumerPath = Path.Combine(payloadRoot, candidate.Package.TransactionConsumerRelativePath!),
            TransactionConsumerRelativePath = candidate.Package.TransactionConsumerRelativePath,
            TransactionConsumerSha256 = candidate.Package.TransactionConsumerSha256,
            UnifiedManifestRelativePath = candidate.Package.UnifiedManifestRelativePath,
            RuntimeProtocolContract = candidate.Package.RuntimeProtocolContract
        };

    private static ProductionToolchainManifest WithRecoveryReference(
        ProductionToolchainManifest manifest,
        string packagePath,
        string packageHash) => new()
        {
            SchemaVersion = manifest.SchemaVersion,
            PromotedFingerprint = manifest.PromotedFingerprint,
            Fingerprint = manifest.Fingerprint,
            OwnerProduct = manifest.OwnerProduct,
            RuntimeSubsystem = manifest.RuntimeSubsystem,
            RimLiaisonExecutablePath = manifest.RimLiaisonExecutablePath,
            RimLiaisonExecutableSha256 = manifest.RimLiaisonExecutableSha256,
            RimLiaisonAssemblyPath = manifest.RimLiaisonAssemblyPath,
            RimLiaisonAssemblySha256 = manifest.RimLiaisonAssemblySha256,
            DevBridgeRuntimeRoot = manifest.DevBridgeRuntimeRoot,
            DevBridgePackageSha256 = manifest.DevBridgePackageSha256,
            TransactionConsumerPath = manifest.TransactionConsumerPath,
            TransactionConsumerSha256 = manifest.TransactionConsumerSha256,
            RuntimeProtocolContract = manifest.RuntimeProtocolContract,
            LegacyCompatibilityContract = manifest.LegacyCompatibilityContract,
            QualifiedSourceCommit = manifest.QualifiedSourceCommit,
            QualificationArtifactPath = manifest.QualificationArtifactPath,
            QualificationArtifactSha256 = manifest.QualificationArtifactSha256,
            DevBridgeCoordinatorSha256 = manifest.DevBridgeCoordinatorSha256,
            UnifiedManifestPath = manifest.UnifiedManifestPath,
            UnifiedManifestSha256 = manifest.UnifiedManifestSha256,
            PromotionPackagePath = Path.GetFullPath(packagePath),
            PromotionPackageSha256 = packageHash
        };

    private static bool HasModernRecoveryReference(
        ProductionToolchainManifest manifest,
        out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(manifest.PromotionPackagePath) ||
            string.IsNullOrWhiteSpace(manifest.PromotionPackageSha256) ||
            !File.Exists(manifest.PromotionPackagePath))
            return false;
        ToolchainPromotionPackage? package = ToolchainPromotionService.ReadPackage(
            manifest.PromotionPackagePath,
            out error);
        return package is not null &&
            string.Equals(package.SourceCommit, manifest.QualifiedSourceCommit, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(package.RimLiaisonExecutableSha256, manifest.RimLiaisonExecutableSha256, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(package.RimLiaisonAssemblySha256, manifest.RimLiaisonAssemblySha256, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(package.DevBridgePackageSha256, manifest.DevBridgePackageSha256, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(package.DevBridgeCoordinatorSha256, manifest.DevBridgeCoordinatorSha256, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(package.DevBridgeRuntimeArtifactRoot) &&
            Directory.Exists(package.DevBridgeRuntimeArtifactRoot) &&
            Directory.Exists(package.ArtifactRoot);
    }
    private static bool PackageMatches(
        ToolchainPromotionPackage left,
        ToolchainPromotionPackage right,
        out string? error)
    {
        error = null;
        bool matches = left.SourceCommit == right.SourceCommit &&
            left.RimLiaisonExecutableSha256 == right.RimLiaisonExecutableSha256 &&
            left.RimLiaisonAssemblySha256 == right.RimLiaisonAssemblySha256 &&
            left.DevBridgePackageSha256 == right.DevBridgePackageSha256 &&
            left.DevBridgeCoordinatorSha256 == right.DevBridgeCoordinatorSha256 &&
            left.TransactionConsumerSha256 == right.TransactionConsumerSha256 &&
            left.QualificationArtifactSha256 == right.QualificationArtifactSha256;
        if (!matches) error = "The durable recovery package identity differs from the active promotion.";
        return matches;
    }

    private static bool RuntimeMatches(string root, ToolchainPromotionPackage package)
    {
        string manifest = Path.Combine(root, ".devbridge-runtime-manifest.json");
        string coordinator = Path.Combine(root, "Coordinator", "DevBridge.Coordinator.exe");
        return File.Exists(Path.Combine(root, "DevBridge.cmd")) &&
            File.Exists(manifest) &&
            File.Exists(coordinator) &&
            string.Equals(ReadRuntimePackageHash(manifest), package.DevBridgePackageSha256, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(ReadRuntimeFileHash(manifest, "Coordinator/DevBridge.Coordinator.exe"), package.DevBridgeCoordinatorSha256, StringComparison.OrdinalIgnoreCase) &&
            HashEquals(coordinator, package.DevBridgeCoordinatorSha256);
    }

    private static string BuildUnifiedManifest(
        ProductionToolchainManifest manifest,
        ToolchainPromotionPackage package)
    {
        string unifiedRoot = Path.GetDirectoryName(manifest.UnifiedManifestPath!)!;
        string Relative(string path) => Path.GetRelativePath(unifiedRoot, path).Replace(Path.DirectorySeparatorChar, '/');
        return JsonSerializer.Serialize(new
        {
            schemaVersion = "rimliaison-unified-production-package/v2",
            productFingerprint = manifest.PromotedFingerprint,
            ownerProduct = ToolchainPromotionSchemas.OwnerProduct,
            runtimeSubsystem = ToolchainPromotionSchemas.RuntimeSubsystem,
            rimBridgeServer = new { boundary = ToolchainPromotionSchemas.RimBridgeServerBoundary, ownership = "RimBridgeServer" },
            sourceCommit = package.SourceCommit,
            rimLiaison = new
            {
                executablePath = Relative(manifest.RimLiaisonExecutablePath!),
                executableSha256 = manifest.RimLiaisonExecutableSha256,
                assemblyPath = Relative(manifest.RimLiaisonAssemblyPath!),
                assemblySha256 = manifest.RimLiaisonAssemblySha256
            },
            runtime = new { packageSha256 = package.DevBridgePackageSha256, coordinatorSha256 = package.DevBridgeCoordinatorSha256 },
            transactionConsumer = new { path = Relative(manifest.TransactionConsumerPath!), sha256 = package.TransactionConsumerSha256 }
        }, WriteOptions);
    }

    private static FileStream AcquireLock(string path, CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
            catch (IOException) { Thread.Sleep(50); }
        }
        throw new IOException("Another legacy promotion migration did not complete within the bounded recovery window.");
    }

    private static void WriteCreateNew(string path, string content)
    {
        using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using StreamWriter writer = new(stream, new UTF8Encoding(false));
        writer.Write(content);
    }

    private static void AtomicReplace(string path, string content)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".migration";
        File.WriteAllText(temporary, content, new UTF8Encoding(false));
        try { File.Replace(temporary, path, null); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void CopyImmutable(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination))
        {
            if (!HashEquals(source, ToolchainFileHash.Sha256(destination)))
                throw new InvalidDataException("The immutable recovery payload contains a substituted artifact.");
            return;
        }
        File.Copy(source, destination, overwrite: false);
    }

    private static bool TryReadManifest(string path, out ProductionToolchainManifest? manifest)
    {
        manifest = null;
        try
        {
            if (!File.Exists(path)) return false;
            manifest = JsonSerializer.Deserialize<ProductionToolchainManifest>(File.ReadAllText(path), ReadOptions);
            return manifest is not null;
        }
        catch (Exception) when (File.Exists(path)) { return false; }
    }

    private static bool TryReadPackage(string path, out ToolchainPromotionPackage? package)
    {
        package = ToolchainPromotionService.ReadPackage(path, out _);
        return package is not null;
    }

    private static LegacyPromotionMigrationResult Blocked(
        ProductionToolchainManifest? manifest,
        string error,
        long started,
        string code = LegacyRecoveryCode) => new(
            LegacyPromotionMigrationState.Blocked,
            code,
            error,
            "Create and intentionally promote a new qualified RimLiaison production package.",
            manifest?.QualifiedSourceCommit,
            manifest?.PromotedFingerprint,
            ElapsedMilliseconds: Elapsed(started));

    private static LegacyPromotionMigrationResult NotRequired(long started) => new(
        LegacyPromotionMigrationState.NotRequired,
        ElapsedMilliseconds: Elapsed(started));

    private static long Elapsed(long started) => Math.Max(0, Environment.TickCount64 - started);
    private static string FullPath(string? path) => string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFullPath(path);
    private static bool HashEquals(string path, string? expected) => File.Exists(path) && !string.IsNullOrWhiteSpace(expected) && string.Equals(ToolchainFileHash.Sha256(path), expected, StringComparison.OrdinalIgnoreCase);
    private static bool SamePath(string? left, string? right) => !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) && string.Equals(FullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), FullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    private static string? SafePath(string root, string? relative)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':')) return null;
        string candidate = Path.GetFullPath(Path.Combine(root, relative));
        string boundary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(boundary, StringComparison.OrdinalIgnoreCase) ? candidate : null;
    }
    private static string ReadRuntimePackageHash(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.TryGetProperty("packageSha256", out JsonElement value) ? value.GetString() ?? string.Empty : string.Empty;
    }
    private static string ReadRuntimeFileHash(string path, string relativePath)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("files", out JsonElement files) || files.ValueKind != JsonValueKind.Array) return string.Empty;
        foreach (JsonElement file in files.EnumerateArray())
        {
            if (file.TryGetProperty("path", out JsonElement pathValue) && string.Equals(pathValue.GetString(), relativePath, StringComparison.OrdinalIgnoreCase) && file.TryGetProperty("sha256", out JsonElement hash)) return hash.GetString() ?? string.Empty;
        }
        return string.Empty;
    }
    private static string Sha256Text(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Bound(string? value) => string.IsNullOrWhiteSpace(value) ? "unknown storage error" : value.Trim().Length <= 1024 ? value.Trim() : value.Trim()[..1024];

    private sealed record HistoricalCandidate(
        string PackagePath,
        ToolchainPromotionPackage Package,
        string CliPath,
        string AssemblyPath,
        string ConsumerPath,
        string UnifiedText,
        string RuntimeSource,
        string QualificationPath);
}
