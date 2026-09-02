using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using RimLiaison.DevBridge;
using RimLiaison.Execution;
using RimLiaison.Recovery;
using RimLiaison.Toolchain;
using RimLiaison.Git;

namespace RimLiaison.Tests;

internal static class PromotedToolchainRecoveryTests
{
    public static void HealthyInstallationDoesNotRepair()
    {
        using Fixture fixture = new();
        FakeInstaller installer = new();
        PromotedToolchainRecoveryResult result = Recover(fixture, installer);
        Assert(result.Succeeded && result.AlreadyRepaired, "healthy production must be revalidated, not repaired");
        Assert(installer.Calls == 0, "healthy production must not invoke the installer");
    }

    public static void MissingArtifactRepairsAndPreservesIdentity()
    {
        using Fixture fixture = new();
        string manifestBefore = File.ReadAllText(fixture.ManifestPath);
        File.Delete(fixture.CliPath);
        FakeInstaller installer = new(() => File.Copy(fixture.QualifiedCliPath, fixture.CliPath));
        PromotedToolchainRecoveryResult result = Recover(fixture, installer);
        Assert(result.Succeeded, $"missing promoted artifact must recover: {result.ErrorCode} {result.Error}; direct={fixture.Resolve().Failure?.ErrorCode}");
        Assert(installer.Calls == 1, "repair must be bounded to one install");
        Assert(fixture.Resolve().Binding!.PromotedFingerprint == fixture.Fingerprint,
            "repair must preserve the promoted fingerprint");
        Assert(File.ReadAllText(fixture.ManifestPath) == manifestBefore,
            "restoration must not alter the promoted manifest identity");
    }
    public static void RecoveryIgnoresCurrentSourceDivergence()
    {
        using Fixture fixture = new();
        File.Delete(fixture.CliPath);
        string currentSource = Path.Combine(fixture.Root, "current-source");
        Directory.CreateDirectory(currentSource);
        File.WriteAllText(Path.Combine(currentSource, "dirty.cs"), "uncommitted newer source");
        File.WriteAllText(Path.Combine(currentSource, "new-qualification.json"), "valid newer unpromoted qualification");
        Environment.SetEnvironmentVariable("RIMLIAISON_PRODUCTION_CLI", Path.Combine(currentSource, "rimliaison.exe"));
        Environment.SetEnvironmentVariable("DEVBRIDGE_SOURCE_ROOT", currentSource);
        Environment.SetEnvironmentVariable("RIMTEST_DEVBRIDGE_ROOT", currentSource);
        PromotedToolchainRecoveryResult result =
            DevBridgeCapabilityRecovery.RecoverPromotedToolchainAsync(
                fixture.Failure,
                fixture.Root).GetAwaiter().GetResult();
        Assert(result.Succeeded, "current source divergence must not block restoration");
        Assert(File.ReadAllText(fixture.CliPath) == "qualified",
            "restoration must install the older promoted product");
    }

    public static void LocalReleaseCannotSubstitutePromotedPayload()
    {
        using Fixture fixture = new();
        File.WriteAllText(fixture.QualifiedCliPath, "newer local Release");
        File.Delete(fixture.CliPath);
        PromotedToolchainRecoveryResult result =
            DevBridgeCapabilityRecovery.RecoverPromotedToolchainAsync(
                fixture.Failure,
                fixture.Root).GetAwaiter().GetResult();
        Assert(result.Succeeded, "a different local Release must not block package restoration");
        Assert(File.ReadAllText(fixture.CliPath) == "qualified",
            "local Release binaries must not be used for restoration");
    }

    public static void MissingRecoveryPayloadBlocksInfrastructure()
    {
        using Fixture fixture = new();
        File.Delete(fixture.CliPath);
        File.Delete(fixture.PackagePath);
        PromotedToolchainRecoveryResult result =
            DevBridgeCapabilityRecovery.RecoverPromotedToolchainAsync(
                fixture.Failure,
                fixture.Root).GetAwaiter().GetResult();
        Assert(!result.Succeeded &&
               result.ErrorCode == "PRODUCTION_TOOLCHAIN_RECOVERY_PACKAGE_UNAVAILABLE",
            "missing promoted recovery payload must remain an infrastructure block");
    }

    public static void InvalidRecoveryPayloadBlocksInfrastructure()
    {
        using Fixture fixture = new();
        File.Delete(fixture.CliPath);
        File.WriteAllText(fixture.PackageCliPath, "tampered promoted payload");
        PromotedToolchainRecoveryResult result =
            DevBridgeCapabilityRecovery.RecoverPromotedToolchainAsync(
                fixture.Failure,
                fixture.Root).GetAwaiter().GetResult();
        Assert(!result.Succeeded &&
               result.ErrorCode == "PRODUCTION_TOOLCHAIN_RECOVERY_PACKAGE_HASH_MISMATCH",
            "tampered promoted recovery payload must remain an infrastructure block");
    }


    public static void CorruptArtifactRepairs()
    {
        using Fixture fixture = new();
        File.WriteAllText(fixture.CliPath, "corrupt");
        FakeInstaller installer = new(() => File.Copy(fixture.QualifiedCliPath, fixture.CliPath, overwrite: true));
        PromotedToolchainRecoveryResult result = Recover(fixture, installer);
        Assert(result.Succeeded, "hash-mismatched promoted artifact must recover");
        Assert(installer.Calls == 1, "hash repair must be bounded to one install");
    }

    public static void MissingRuntimeArtifactRepairs()
    {
        using Fixture fixture = new();
        string runtimeCommand = Path.Combine(fixture.RuntimeRoot, "DevBridge.cmd");
        File.Delete(runtimeCommand);
        PromotedToolchainRecoveryResult result =
            DevBridgeCapabilityRecovery.RecoverPromotedToolchainAsync(
                fixture.Failure with
                {
                    ExpectedArtifacts = [runtimeCommand],
                    MismatchingArtifacts = [runtimeCommand]
                },
                fixture.Root).GetAwaiter().GetResult();
        Assert(result.Succeeded, $"missing runtime artifact must recover: {result.ErrorCode} {result.Error}");
        Assert(File.Exists(runtimeCommand), "runtime repair must restore DevBridge.cmd");
        Assert(fixture.Resolve().Succeeded, "runtime repair must restore the promoted binding");
    }

    public static void RuntimeHashMismatchRepairs()
    {
        using Fixture fixture = new();
        string coordinator = Path.Combine(
            fixture.RuntimeRoot,
            "Coordinator",
            "DevBridge.Coordinator.exe");
        File.WriteAllText(coordinator, "corrupt");
        PromotedToolchainRecoveryResult result =
            DevBridgeCapabilityRecovery.RecoverPromotedToolchainAsync(
                fixture.Failure with
                {
                    MismatchingArtifacts = [coordinator]
                },
                fixture.Root).GetAwaiter().GetResult();
        Assert(result.Succeeded, $"runtime hash mismatch must recover: {result.ErrorCode} {result.Error}");
        Assert(File.ReadAllText(coordinator) == "coordinator",
            "runtime repair must restore the qualified coordinator");
    }

    public static void MissingCoordinatorRepairs()
    {
        using Fixture fixture = new();
        string coordinator = Path.Combine(
            fixture.RuntimeRoot,
            "Coordinator",
            "DevBridge.Coordinator.exe");
        File.Delete(coordinator);
        ProductionToolchainBindingResolution resolution = fixture.Resolve();
        Assert(
            resolution.Failure?.ErrorCode == "PRODUCTION_TOOLCHAIN_ARTIFACT_UNREADABLE",
            "a missing coordinator must be classified as an unreadable promoted artifact");
        PromotedToolchainRecoveryResult result =
            DevBridgeCapabilityRecovery.RecoverPromotedToolchainAsync(
                resolution.Failure!,
                fixture.Root).GetAwaiter().GetResult();
        Assert(result.Succeeded, $"missing coordinator must recover: {result.ErrorCode} {result.Error}");
        Assert(File.Exists(coordinator), "runtime repair must restore the qualified coordinator");
    }

    public static void ConcurrentRecoveryHasOneEffectiveRepair()
    {
        using Fixture fixture = new();
        File.Delete(fixture.CliPath);
        FakeInstaller installer = new(
            () => File.Copy(fixture.QualifiedCliPath, fixture.CliPath),
            delay: TimeSpan.FromMilliseconds(100));
        Task<PromotedToolchainRecoveryResult> first = RecoverAsync(fixture, installer);
        Task<PromotedToolchainRecoveryResult> second = RecoverAsync(fixture, installer);
        Task.WaitAll(first, second);
        Assert(first.Result.Succeeded && second.Result.Succeeded,
            "concurrent workflows must both revalidate the repaired installation");
        Assert(installer.Calls == 1, "concurrent workflows must share one effective repair");
    }

    public static void AuthoritativePackageRepairs()
    {
        using Fixture fixture = new();
        File.Delete(fixture.CliPath);
        PromotedToolchainRecoveryResult result =
            DevBridgeCapabilityRecovery.RecoverPromotedToolchainAsync(
                fixture.Failure,
                fixture.Root).GetAwaiter().GetResult();
        Assert(result.Succeeded, $"authoritative package must repair: {result.ErrorCode} {result.Error}");
        Assert(result.RecoveryPackagePath == Path.GetFullPath(fixture.PackagePath),
            "successful restoration must report the immutable recovery payload");
        Assert(result.PromotedSourceCommit == "qualified-commit",
            "successful restoration must report the promoted source commit");
        Assert(fixture.Resolve().Succeeded, "authoritative package repair must restore binding");
    }
    public static void LegacyPromotionMigratesAndRestores()
    {
        using Fixture fixture = new();
        string qualificationDirectory = Path.Combine(fixture.Root, ".rimdev", "qualification");
        Directory.CreateDirectory(qualificationDirectory);
        string historicalPackage = Path.Combine(
            qualificationDirectory,
            "qualified-toolchain-package-qualified-commit-legacy.json");
        File.Copy(fixture.PackagePath, historicalPackage);
        File.Copy(
            fixture.UnifiedManifestPath,
            Path.Combine(Path.GetDirectoryName(fixture.PackageCliPath)!, "unified-package.json"));
        JsonObject legacyManifest = JsonNode.Parse(File.ReadAllText(fixture.ManifestPath))!.AsObject();
        legacyManifest.Remove("promotionPackagePath");
        legacyManifest.Remove("promotionPackageSha256");
        File.WriteAllText(fixture.ManifestPath, legacyManifest.ToJsonString());
        File.Delete(fixture.CliPath);

        PromotedToolchainRecoveryResult result =
            DevBridgeCapabilityRecovery.RecoverPromotedToolchainAsync(
                fixture.Failure,
                fixture.Root).GetAwaiter().GetResult();
        Assert(result.Succeeded, $"legacy promotion must migrate: {result.ErrorCode} {result.Error}");
        Assert(File.ReadAllText(fixture.CliPath) == "qualified",
            "legacy migration must restore the exact promoted executable");
        JsonObject migratedManifest = JsonNode.Parse(File.ReadAllText(fixture.ManifestPath))!.AsObject();
        string migratedPackage = migratedManifest["promotionPackagePath"]!.GetValue<string>();
        Assert(File.Exists(migratedPackage) &&
               migratedPackage.Contains("promotion-recovery", StringComparison.OrdinalIgnoreCase),
            "legacy migration must persist a durable recovery package reference");
        Assert(migratedManifest["promotedFingerprint"]!.GetValue<string>() == fixture.Fingerprint &&
               migratedManifest["qualifiedSourceCommit"]!.GetValue<string>() == "qualified-commit",
            "legacy migration must preserve the active promoted identity");
        LegacyPromotionMigrationResult second =
            LegacyPromotionMigrationService.Ensure(fixture.Root);
        Assert(second.State == LegacyPromotionMigrationState.NotRequired,
            "legacy migration must be idempotent");
    }


    public static void LegacyPromotionWithoutExactMaterialBlocksSafely()
    {
        using Fixture fixture = new();
        JsonObject legacyManifest = JsonNode.Parse(File.ReadAllText(fixture.ManifestPath))!.AsObject();
        legacyManifest.Remove("promotionPackagePath");
        legacyManifest.Remove("promotionPackageSha256");
        File.WriteAllText(fixture.ManifestPath, legacyManifest.ToJsonString());
        File.Delete(fixture.PackagePath);
        LegacyPromotionMigrationResult result =
            LegacyPromotionMigrationService.Ensure(fixture.Root);
        Assert(result.State == LegacyPromotionMigrationState.Blocked &&
               result.ErrorCode == "PRODUCTION_TOOLCHAIN_LEGACY_RECOVERY_UNAVAILABLE" &&
               result.NextAction!.Contains("intentionally promote", StringComparison.OrdinalIgnoreCase),
            "legacy promotions without exact material must produce one safe promotion maintenance action");
    }
    public static void ModernPromotionSkipsMigration()
    {
        using Fixture fixture = new();
        LegacyPromotionMigrationResult result =
            LegacyPromotionMigrationService.Ensure(fixture.Root);
        Assert(result.State == LegacyPromotionMigrationState.NotRequired &&
               result.RecoveryPackagePath == Path.GetFullPath(fixture.PackagePath),
            "modern promotions must not perform legacy migration work");
    }

    public static void LegacyCandidateSelectionRequiresExactIdentity()
    {
        using Fixture fixture = new();
        string qualificationDirectory = Path.Combine(fixture.Root, ".rimdev", "qualification");
        Directory.CreateDirectory(qualificationDirectory);
        string exact = Path.Combine(
            qualificationDirectory,
            "qualified-toolchain-package-exact.json");
        File.Copy(fixture.PackagePath, exact);
        string mismatching = Path.Combine(
            qualificationDirectory,
            "qualified-toolchain-package-newest.json");
        JsonObject mismatchingPackage = JsonNode.Parse(File.ReadAllText(fixture.PackagePath))!.AsObject();
        mismatchingPackage["rimLiaisonExecutableSha256"] = new string('0', 64);
        File.WriteAllText(mismatching, mismatchingPackage.ToJsonString());
        JsonObject legacyManifest = JsonNode.Parse(File.ReadAllText(fixture.ManifestPath))!.AsObject();
        legacyManifest.Remove("promotionPackagePath");
        legacyManifest.Remove("promotionPackageSha256");
        File.WriteAllText(fixture.ManifestPath, legacyManifest.ToJsonString());
        File.WriteAllText(Path.Combine(fixture.Root, "dirty-development-change.cs"), "current source");
        LegacyPromotionMigrationResult result =
            LegacyPromotionMigrationService.Ensure(fixture.Root);
        Assert(result.Migrated &&
               result.PromotedSourceCommit == "qualified-commit" &&
               result.PromotedFingerprint == fixture.Fingerprint,
            "legacy discovery must select exact identity material, not a newer mismatching package");
    }


    public static void UnavailablePackageIsInfrastructureBlock()
    {
        using Fixture fixture = new();
        File.Delete(fixture.CliPath);
        PromotedToolchainRecoveryResult result = Recover(
            fixture,
            new FakeInstaller(
                result: new PromotedToolchainInstallResult(
                    false,
                    "PRODUCTION_TOOLCHAIN_RECOVERY_PACKAGE_UNAVAILABLE",
                    "qualified package is unavailable")));
        Assert(!result.Succeeded && result.ErrorCode == "PRODUCTION_TOOLCHAIN_RECOVERY_PACKAGE_UNAVAILABLE",
            "unavailable qualified package must remain an infrastructure block");
    }

    public static void ExperimentalReplacementIsRejected()
    {
        using Fixture fixture = new();
        File.Delete(fixture.CliPath);
        FakeInstaller installer = new(() => File.WriteAllText(fixture.CliPath, "experimental"));
        PromotedToolchainRecoveryResult result = Recover(fixture, installer);
        Assert(!result.Succeeded && result.ErrorCode == "PRODUCTION_TOOLCHAIN_FINGERPRINT_MISMATCH",
            "experimental or working-tree replacement must not satisfy promoted identity");
    }

    public static void SameIntegrityFailureIsBounded()
    {
        using Fixture fixture = new();
        File.Delete(fixture.CliPath);
        FakeInstaller installer = new();
        PromotedToolchainRecoveryResult result = Recover(fixture, installer);
        Assert(!result.Succeeded && installer.Calls == 1,
            "a repair that leaves the same integrity fault must terminate after one attempt");
    }

    public static void ProjectFailureIsNotToolchainRepair()
    {
        Assert(!ProductionExecutionPolicy.IsPromotedToolchainIntegrityCode("DEVELOPMENT_BUILD_FAILED"),
            "project build failures must not enter promoted-toolchain repair");
        Assert(ProductionExecutionPolicy.Classify("DEVELOPMENT_BUILD_FAILED").IsProjectFailure,
            "project build failures must remain project-owned");
        Assert(ProductionExecutionPolicy.Classify(
                "PRODUCTION_TOOLCHAIN_ARTIFACT_MISSING",
                buildOwnerType: "PROJECT_BUILD").IsProjectFailure,
            "project-owned failures must take precedence over an overlapping toolchain code");
        Assert(ProductionExecutionPolicy.IsPromotedToolchainIntegrityCode("DEVBRIDGE_RUNTIME_MISSING"),
            "DevBridge runtime absence must enter promoted-toolchain repair");
        Assert(ProductionExecutionPolicy.IsPromotedToolchainIntegrityCode("DEVBRIDGE_RUNTIME_INCOMPLETE"),
            "DevBridge runtime incompleteness must enter promoted-toolchain repair");
    }

    public static void ExternalRuntimeMissingUsesPromotedRecovery()
    {
        using Fixture fixture = new();
        string runtimeCommand = Path.Combine(fixture.RuntimeRoot, "DevBridge.cmd");
        File.Delete(runtimeCommand);
        string sourceFingerprint = Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant();
        var development = new TransactionAdapter((call, fingerprint, workflowId) =>
            call == 0
                ? DevelopmentFailure(workflowId, "DEVBRIDGE_RUNTIME_MISSING")
                : DevelopmentSuccess(fingerprint, workflowId));
        FakeInstaller installer = new(
            repair: () => File.WriteAllText(runtimeCommand, "runtime"),
            result: new PromotedToolchainInstallResult(
                true,
                PromotedSourceCommit: "qualified-commit"));

        ArtifactFreshnessTransactionResult result = PrepareTransaction(
            fixture,
            development,
            installer,
            sourceFingerprint,
            "wf-devbridge-runtime-missing");

        Assert(result.Success, "missing promoted DevBridge runtime must recover and retry");
        Assert(development.Calls == 2 && installer.Calls == 1,
            "runtime repair must be bounded to one repair and one retry");
        Assert(File.Exists(runtimeCommand), "promoted runtime materialization must restore DevBridge.cmd");
        Assert(result.RecoveryEvents?[0].Component == "promoted-production-toolchain",
            "runtime recovery must remain tooling-owned");
    }

    public static void ProductionFreshnessRecoveryRetriesInterruptedOperation()
    {
        using Fixture fixture = new();
        File.Delete(fixture.CliPath);
        string sourceFingerprint = Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant();
        var development = new TransactionAdapter((call, fingerprint, workflowId) =>
            call == 0
                ? DevelopmentFailure(workflowId, "PRODUCTION_TOOLCHAIN_ARTIFACT_MISSING")
                : DevelopmentSuccess(fingerprint, workflowId));
        FakeInstaller installer = new(
            repair: () => File.Copy(fixture.QualifiedCliPath, fixture.CliPath),
            result: new PromotedToolchainInstallResult(
                true,
                PromotedSourceCommit: "qualified-commit"));

        ArtifactFreshnessTransactionResult result = PrepareTransaction(
            fixture,
            development,
            installer,
            sourceFingerprint,
            "wf-production-recovery",
            new FixedGitRepositoryStateProvider(
                fixture.Root,
                "current-commit"));

        Assert(result.Success, "a recoverable promoted fault must resume the interrupted operation");
        Assert(development.Calls == 2, "the interrupted operation must be retried exactly once");
        Assert(installer.Calls == 1, "promoted repair must run exactly once");
        Assert(result.RecoveryEvents?.Count == 1, "the production repair must emit one recovery event");
        RimTestPrerequisiteRecovery recovery = result.RecoveryEvents![0];
        Assert(recovery.State == "recovered", "the recovery event must be recovered");
        Assert(recovery.OriginalFault == "PRODUCTION_TOOLCHAIN_ARTIFACT_MISSING",
            "the original promoted fault must remain visible");
        Assert(recovery.RepairResult == "repaired", "the repair result must be visible");
        Assert(recovery.VerificationResult == "promoted-identity-and-readiness-verified",
            "the promoted identity must be verified before retry");
        Assert(recovery.RetryResult == "success", "the retry result must be visible");
        Assert(recovery.ExpectedPromotedFingerprint == fixture.Fingerprint,
            "the retry must preserve the exact promoted fingerprint");
        Assert(recovery.PromotedSourceCommit == "qualified-commit",
            "the recovery event must retain the promoted source commit");
        Assert(recovery.CurrentSourceDiverged == true,
            "the recovery event must record current source divergence");
    }

    public static void ProductionFreshnessRecoveryPreservesProjectFailure()
    {
        using Fixture fixture = new();
        File.Delete(fixture.CliPath);
        string sourceFingerprint = Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant();
        var development = new TransactionAdapter((call, fingerprint, workflowId) =>
            call == 0
                ? DevelopmentFailure(workflowId, "PRODUCTION_TOOLCHAIN_ARTIFACT_MISSING")
                : DevelopmentProjectFailure(workflowId));
        FakeInstaller installer = new(() => File.Copy(
            fixture.QualifiedCliPath,
            fixture.CliPath));

        ArtifactFreshnessTransactionResult result = PrepareTransaction(
            fixture,
            development,
            installer,
            sourceFingerprint,
            "wf-production-project-failure");

        Assert(!result.Success, "a genuine project failure must remain terminal");
        Assert(development.Calls == 2 && installer.Calls == 1,
            "project validation must run once after the single promoted repair");
        Assert(result.Status.ErrorCode == "DEVELOPMENT_ASSERTION_FAILED",
            "the final project failure must remain the reported result");
        Assert(ProductionExecutionPolicy.Classify(
                result.Status.ErrorCode,
                buildOwnerType: "PROJECT_BUILD").IsProjectFailure,
            "the final failure must remain project-owned");
        Assert(result.RecoveryEvents![0].RetryResult == "project-or-runtime-failure",
            "the recovered tooling event must identify the later project result");
    }

    public static void ProductionFreshnessRecoveryBoundsRepeatedIntegrityFailure()
    {
        using Fixture fixture = new();
        File.Delete(fixture.CliPath);
        string sourceFingerprint = Convert.ToHexString(SHA256.HashData([])).ToLowerInvariant();
        var development = new TransactionAdapter((_, _, workflowId) =>
            DevelopmentFailure(workflowId, "PRODUCTION_TOOLCHAIN_ARTIFACT_MISSING"));
        FakeInstaller installer = new(() => File.Copy(
            fixture.QualifiedCliPath,
            fixture.CliPath));

        ArtifactFreshnessTransactionResult result = PrepareTransaction(
            fixture,
            development,
            installer,
            sourceFingerprint,
            "wf-production-repeated-fault");

        Assert(!result.Success, "a repeated promoted integrity fault must remain blocked");
        Assert(development.Calls == 2 && installer.Calls == 1,
            "a repeated integrity fault must not recurse or repair twice");
        Assert(result.Status.ErrorCode == "PRODUCTION_TOOLCHAIN_ARTIFACT_MISSING",
            "the original integrity fault must remain the final code");
        Assert(result.Status.RecoveryState == PrerequisiteRecoveryState.TransitionRecoveryExhausted,
            "the repeated integrity fault must be explicitly exhausted");
        Assert(result.RecoveryEvents![0].RetryResult == "same-integrity-fault",
            "the bounded retry result must be explicit");
    }

    private static ArtifactFreshnessTransactionResult PrepareTransaction(
        Fixture fixture,
        TransactionAdapter development,
        FakeInstaller installer,
        string sourceFingerprint,
        string workflowId,
        IGitRepositoryStateProvider? repositoryStateProvider = null) =>
        new ArtifactFreshnessTransaction(
                development,
                repositoryStateProvider: repositoryStateProvider,
                recoveryTransport: new NoopTransport(),
                promotedToolchainInstaller: installer
            )
            .PrepareAsync(new ArtifactFreshnessTransactionRequest(
                "fixture",
                fixture.Root,
                [],
                sourceFingerprint,
                workflowId))
            .GetAwaiter()
            .GetResult();

    private static DevBridgeModDevelopmentResult DevelopmentFailure(
        string? workflowId,
        string errorCode) =>
        new(
            "fixture",
            new DevBridgeAdapterStatus(
                DevBridgeOutcomeKind.InfrastructureFailure,
                errorCode,
                "promoted integrity failure"),
            false,
            "tx-failure",
            workflowId,
            null,
            null,
            null);

    private static DevBridgeModDevelopmentResult DevelopmentProjectFailure(
        string? workflowId) =>
        new(
            "fixture",
            new DevBridgeAdapterStatus(
                DevBridgeOutcomeKind.DevBridgeRefusal,
                "DEVELOPMENT_ASSERTION_FAILED",
                "project assertion failed"),
            false,
            "tx-project-failure",
            workflowId,
            null,
            null,
            null,
            new DevBridgeBuildDiagnostics(
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                BuildOwnerType: "PROJECT_BUILD"));

    private static DevBridgeModDevelopmentResult DevelopmentSuccess(
        string sourceFingerprint,
        string? workflowId) =>
        new(
            "fixture",
            new DevBridgeAdapterStatus(DevBridgeOutcomeKind.Success),
            true,
            "tx-success",
            workflowId,
            1,
            "lease",
            new DevBridgeArtifactFreshness(
                sourceFingerprint,
                new string('a', 64),
                new string('a', 64),
                "unchanged",
                1,
                1,
                1,
                true,
                "proof",
                "tx-success",
                workflowId,
                "lease"));

    private static PromotedToolchainRecoveryResult Recover(Fixture fixture, FakeInstaller installer) =>
        RecoverAsync(fixture, installer).GetAwaiter().GetResult();

    private static Task<PromotedToolchainRecoveryResult> RecoverAsync(
        Fixture fixture,
        FakeInstaller installer) =>
        DevBridgeCapabilityRecovery.RecoverPromotedToolchainAsync(
            fixture.Failure,
            fixture.Root,
            installer: installer);
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private sealed class FakeInstaller : IPromotedToolchainInstaller
    {
        private readonly Action? repair;
        private readonly TimeSpan delay;
        private readonly PromotedToolchainInstallResult? result;
        public int Calls;

        public FakeInstaller(
            Action? repair = null,
            TimeSpan delay = default,
            PromotedToolchainInstallResult? result = null)
        {
            this.repair = repair;
            this.delay = delay;
            this.result = result;
        }

        public async Task<PromotedToolchainInstallResult> RepairAsync(
            ProductionToolchainBindingFailure failure,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
            repair?.Invoke();
            return result ?? new PromotedToolchainInstallResult(true);
        }
    }
    private sealed class FixedGitRepositoryStateProvider(
        string root,
        string headSha) : IGitRepositoryStateProvider
    {
        public Task<GitRepositoryStateResult> ReadAsync(
            string rootPath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GitRepositoryStateResult(
                true,
                new GitRepositoryStateSnapshot(
                    Path.GetFullPath(root),
                    "fixture",
                    "main",
                    headSha,
                    null,
                    null,
                    null,
                    false,
                    [])));
    }

    private sealed class TransactionAdapter(
        Func<int, string, string?, DevBridgeModDevelopmentResult> factory)
        : IDevBridgeModDevelopmentAdapter
    {
        public int Calls { get; private set; }

        public Task<DevBridgeModDevelopmentResult> RunAsync(
            string project,
            string repositoryRoot,
            string sourceFingerprint,
            string? workflowId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(factory(Calls++, sourceFingerprint, workflowId));
    }

    private sealed class NoopTransport : IDevBridgeProcessTransport
    {
        public Task<DevBridgeProcessResult> ExecuteAsync(
            DevBridgeProcessRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new DevBridgeProcessResult(0, "{}", null));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string? priorSourceRoot = Environment.GetEnvironmentVariable("DEVBRIDGE_SOURCE_ROOT");
        private readonly string? priorRuntimeRoot = Environment.GetEnvironmentVariable("RIMTEST_DEVBRIDGE_ROOT");
        private readonly string? priorManifest = Environment.GetEnvironmentVariable("RIMLIAISON_PRODUCTION_TOOLCHAIN_MANIFEST");
        private readonly string? priorProductionCli = Environment.GetEnvironmentVariable("RIMLIAISON_PRODUCTION_CLI");
        public string Root { get; }
        public string CliPath { get; }
        public string QualifiedCliPath { get; }
        public string AssemblyPath { get; }
        public string RuntimeRoot { get; }
        public string ConsumerPath { get; }
        public string UnifiedManifestPath { get; }
        public string ManifestPath { get; }
        public string PackagePath { get; }
        public string PackageCliPath { get; }
        public string QualificationPath { get; }
        public string Fingerprint { get; }
        public ProductionToolchainBindingFailure Failure { get; }

        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "rimliaison-promoted-recovery-" + Guid.NewGuid().ToString("N"));
            CliPath = Path.Combine(Root, "installed", "rimliaison.exe");
            QualifiedCliPath = Path.Combine(Root, "qualified", "rimliaison.exe");
            AssemblyPath = Path.Combine(Root, "installed", "rimliaison.dll");
            RuntimeRoot = Path.Combine(Root, "runtime");
            ConsumerPath = Path.Combine(Root, "installed", "transaction-components", "mod-test.ps1");
            UnifiedManifestPath = Path.Combine(Root, "installed", "unified-package.json");
            PackagePath = Path.Combine(Root, "promotion-package.json");
            QualificationPath = Path.Combine(Root, "qualification.json");
            ManifestPath = Path.Combine(Root, "production-toolchain.json");
            Directory.CreateDirectory(Path.GetDirectoryName(CliPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(QualifiedCliPath)!);
            Directory.CreateDirectory(Path.Combine(RuntimeRoot, "Coordinator"));
            Directory.CreateDirectory(Path.GetDirectoryName(ConsumerPath)!);
            File.WriteAllText(QualifiedCliPath, "qualified");
            File.Copy(QualifiedCliPath, CliPath);
            File.WriteAllText(AssemblyPath, "assembly");
            File.WriteAllText(Path.Combine(RuntimeRoot, "DevBridge.cmd"), "runtime");
            string coordinatorPath = Path.Combine(RuntimeRoot, "Coordinator", "DevBridge.Coordinator.exe");
            File.WriteAllText(coordinatorPath, "coordinator");
            File.WriteAllText(ConsumerPath, "consumer");

            string cliHash = Hash(QualifiedCliPath);
            string assemblyHash = Hash(AssemblyPath);
            string consumerHash = Hash(ConsumerPath);
            string coordinatorHash = Hash(coordinatorPath);
            Fingerprint = ToolchainPromotionService.ComputePromotedFingerprint(
                "qualified-commit",
                cliHash,
                assemblyHash,
                coordinatorHash,
                "runtime-package",
                consumerHash,
                ToolchainPromotionSchemas.RuntimeProtocolContract,
                ToolchainPromotionSchemas.OwnerProduct,
                ToolchainPromotionSchemas.RuntimeSubsystem);
            File.WriteAllText(
                UnifiedManifestPath,
                JsonSerializer.Serialize(new
                {
                    schemaVersion = "rimliaison-unified-production-package/v2",
                    productFingerprint = Fingerprint,
                    ownerProduct = ToolchainPromotionSchemas.OwnerProduct,
                    runtimeSubsystem = ToolchainPromotionSchemas.RuntimeSubsystem,
                    rimBridgeServer = new
                    {
                        boundary = ToolchainPromotionSchemas.RimBridgeServerBoundary,
                        ownership = "RimBridgeServer"
                    },
                    sourceCommit = "qualified-commit",
                    rimLiaison = new
                    {
                        executablePath = "rimliaison.exe",
                        executableSha256 = cliHash,
                        assemblyPath = "rimliaison.dll",
                        assemblySha256 = assemblyHash
                    },
                    runtime = new
                    {
                        packageSha256 = "runtime-package",
                        coordinatorSha256 = coordinatorHash
                    },
                    transactionConsumer = new
                    {
                        path = "transaction-components/mod-test.ps1",
                        sha256 = consumerHash
                    }
                },
                new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(
                Path.Combine(RuntimeRoot, ".devbridge-runtime-manifest.json"),
                JsonSerializer.Serialize(new
                {
                    packageSha256 = "runtime-package",
                    files = new[] { new { path = "Coordinator/DevBridge.Coordinator.exe", sha256 = coordinatorHash } }
                }));
            File.WriteAllText(
                ManifestPath,
                JsonSerializer.Serialize(new
                {
                    schemaVersion = "rimliaison-production-toolchain/v1",
                    promotedFingerprint = Fingerprint,
                    fingerprint = Fingerprint,
                    rimLiaisonExecutablePath = CliPath,
                    rimLiaisonExecutableSha256 = cliHash,
                    rimLiaisonAssemblyPath = AssemblyPath,
                    rimLiaisonAssemblySha256 = assemblyHash,
                    ownerProduct = ToolchainPromotionSchemas.OwnerProduct,
                    runtimeSubsystem = ToolchainPromotionSchemas.RuntimeSubsystem,
                    devBridgeRuntimeRoot = RuntimeRoot,
                    devBridgePackageSha256 = "runtime-package",
                    devBridgeCoordinatorSha256 = coordinatorHash,
                    transactionConsumerPath = ConsumerPath,
                    transactionConsumerSha256 = consumerHash,
                    unifiedManifestPath = UnifiedManifestPath,
                    unifiedManifestSha256 = Hash(UnifiedManifestPath),
                    runtimeProtocolContract = ToolchainPromotionSchemas.RuntimeProtocolContract,
                    qualifiedSourceCommit = "qualified-commit"
                }));
            string packageArtifactRoot = Path.Combine(Root, "qualified-package");
            Directory.CreateDirectory(packageArtifactRoot);
            PackageCliPath = Path.Combine(packageArtifactRoot, "rimliaison.exe");
            File.Copy(QualifiedCliPath, PackageCliPath);
            File.Copy(AssemblyPath, Path.Combine(packageArtifactRoot, "rimliaison.dll"));
            string packageConsumer = Path.Combine(packageArtifactRoot, "transaction-components", "mod-test.ps1");
            Directory.CreateDirectory(Path.GetDirectoryName(packageConsumer)!);
            File.Copy(ConsumerPath, packageConsumer);
            string packageRuntimeRoot = Path.Combine(Root, "qualified-runtime");
            Directory.CreateDirectory(Path.Combine(packageRuntimeRoot, "Coordinator"));
            File.Copy(
                Path.Combine(RuntimeRoot, "DevBridge.cmd"),
                Path.Combine(packageRuntimeRoot, "DevBridge.cmd"));
            File.Copy(
                Path.Combine(RuntimeRoot, ".devbridge-runtime-manifest.json"),
                Path.Combine(packageRuntimeRoot, ".devbridge-runtime-manifest.json"));
            File.Copy(
                coordinatorPath,
                Path.Combine(packageRuntimeRoot, "Coordinator", "DevBridge.Coordinator.exe"));
            File.WriteAllText(
                QualificationPath,
                JsonSerializer.Serialize(new
                {
                    SourceCommit = "qualified-commit",
                    Passes = 1,
                    TotalRuns = 1,
                    InfrastructureFailures = 0,
                    FixtureFailures = 0,
                    QualifiedArtifactHashes = new
                    {
                        rimLiaisonExecutableSha256 = cliHash,
                        rimLiaisonAssemblySha256 = assemblyHash
                    }
                }));
            ToolchainPromotionPackage package = new()
            {
                SchemaVersion = ToolchainPromotionSchemas.LegacyPackage,
                SourceCommit = "qualified-commit",
                QualificationArtifactPath = QualificationPath,
                QualificationArtifactSha256 = Hash(QualificationPath),
                ArtifactRoot = packageArtifactRoot,
                RimLiaisonExecutableRelativePath = "rimliaison.exe",
                RimLiaisonAssemblyRelativePath = "rimliaison.dll",
                RimLiaisonExecutableSha256 = cliHash,
                RimLiaisonAssemblySha256 = assemblyHash,
                OwnerProduct = ToolchainPromotionSchemas.OwnerProduct,
                RuntimeSubsystem = ToolchainPromotionSchemas.RuntimeSubsystem,
                DevBridgeRuntimeRoot = RuntimeRoot,
                DevBridgeRuntimeArtifactRoot = packageRuntimeRoot,
                DevBridgePackageSha256 = "runtime-package",
                DevBridgeCoordinatorSha256 = coordinatorHash,
                TransactionConsumerPath = ConsumerPath,
                TransactionConsumerRelativePath = "transaction-components/mod-test.ps1",
                TransactionConsumerSha256 = consumerHash,
                UnifiedManifestRelativePath = "unified-package.json",
                RuntimeProtocolContract = ToolchainPromotionSchemas.RuntimeProtocolContract
            };
            File.WriteAllText(
                PackagePath,
                JsonSerializer.Serialize(package, new JsonSerializerOptions { WriteIndented = true }));
            JsonObject manifest = JsonNode.Parse(File.ReadAllText(ManifestPath))!.AsObject();
            manifest["promotionPackagePath"] = PackagePath;
            manifest["promotionPackageSha256"] = Hash(PackagePath);
            File.WriteAllText(ManifestPath, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Environment.SetEnvironmentVariable("DEVBRIDGE_SOURCE_ROOT", RuntimeRoot);
            Environment.SetEnvironmentVariable("RIMTEST_DEVBRIDGE_ROOT", RuntimeRoot);
            Environment.SetEnvironmentVariable("RIMLIAISON_PRODUCTION_TOOLCHAIN_MANIFEST", ManifestPath);
            Failure = new(
                "PRODUCTION_TOOLCHAIN_ARTIFACT_MISSING",
                "required artifact missing",
                "repair qualified package",
                [ManifestPath],
                Fingerprint,
                CliPath,
                RuntimeRoot,
                ManifestPath,
                [CliPath, AssemblyPath, Path.Combine(RuntimeRoot, "DevBridge.cmd"), ConsumerPath, UnifiedManifestPath],
                [CliPath]);
        }

        public ProductionToolchainBindingResolution Resolve() =>
            ProductionToolchainBindingResolver.Resolve(Root, currentExecutablePath: CliPath);

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("DEVBRIDGE_SOURCE_ROOT", priorSourceRoot);
            Environment.SetEnvironmentVariable("RIMTEST_DEVBRIDGE_ROOT", priorRuntimeRoot);
            Environment.SetEnvironmentVariable("RIMLIAISON_PRODUCTION_TOOLCHAIN_MANIFEST", priorManifest);
            Environment.SetEnvironmentVariable("RIMLIAISON_PRODUCTION_CLI", priorProductionCli);
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static string Hash(string path)
        {
            using FileStream stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
    }
}
