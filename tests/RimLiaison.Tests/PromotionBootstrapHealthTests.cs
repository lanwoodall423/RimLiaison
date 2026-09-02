using System.Security.Cryptography;
using System.Text.Json;
using RimLiaison.Git;
using RimLiaison.Qualification;
using RimLiaison.Toolchain;

namespace RimLiaison.Tests;

internal static class PromotionBootstrapHealthTests
{
    public static void HealthyProductionHealthyCandidateSuccess() => AssertSuccess("healthy");
    public static void MissingLegacyRuntimeReplacement() => AssertSuccess("missing");
    public static void CorruptLegacyRuntimeReplacement() => AssertSuccess("corrupt");
    public static void UnrecoverableLegacyReplacement() => AssertSuccess("unrecoverable");

    public static void MissingLegacyUnhealthyCandidateRollback()
    {
        using Fixture fixture = new("missing");
        string before = File.ReadAllText(fixture.ManifestPath);
        ToolchainPromotionResult result = fixture.Promote(candidatePass: false);
        Assert(result.ErrorCode == "PROMOTION_CANDIDATE_HEALTH_FAILED", "candidate failure was not classified");
        Assert(File.ReadAllText(fixture.ManifestPath) == before, "candidate failure changed the active manifest");
        Assert(!Directory.Exists(fixture.ProductionRuntimeRoot), "candidate failure activated a missing legacy runtime");
    }

    public static void CandidateRootHealthBinding()
    {
        using Fixture fixture = new("missing");
        ToolchainPromotionResult result = fixture.Promote();
        Assert(result.Status == "promoted", "candidate-root promotion failed");
        Assert(fixture.CandidateVerifier.Binding is not null &&
               fixture.CandidateVerifier.Binding.CandidateRuntimeRoot.Contains(
                   "runtime-promoted-source",
                   StringComparison.OrdinalIgnoreCase),
            "candidate health did not receive the staged runtime root");
        Assert(fixture.CandidateVerifier.Binding?.CandidateRuntimeRoot != fixture.ProductionRuntimeRoot,
            "candidate health used the active runtime root");
    }

    public static void UnrelatedHealthyInstallationCannotSatisfyIdentity()
    {
        using Fixture fixture = new("missing");
        fixture.CandidateVerifier.ExpectedRuntimeRoot = Path.Combine(fixture.Root, "unrelated-runtime");
        ToolchainPromotionResult result = fixture.Promote(candidatePass: false);
        Assert(result.ErrorCode == "PROMOTION_CANDIDATE_HEALTH_FAILED", "unrelated runtime was accepted");
        Assert(fixture.CandidateVerifier.Binding?.CandidateRuntimeRoot != fixture.CandidateVerifier.ExpectedRuntimeRoot,
            "candidate binding was silently substituted with an unrelated runtime");
    }
    public static void StagedCoordinatorQuiescenceRequiresAbsentProcess()
    {
        string stagedRoot = Path.Combine(Path.GetTempPath(), "rimliaison-staged-runtime");
        using JsonDocument absent = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            state = "Absent",
            runtimeRoot = stagedRoot
        }));
        Assert(
            ToolchainPromotionService.IsCoordinatorQuiesced(absent.RootElement, stagedRoot, 4),
            "an absent coordinator probe must prove the staged process is quiesced.");

        using JsonDocument responsive = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            state = "Responsive",
            runtimeRoot = stagedRoot,
            coordinatorPid = 1234
        }));
        Assert(
            !ToolchainPromotionService.IsCoordinatorQuiesced(responsive.RootElement, stagedRoot, 0),
            "a responsive coordinator probe must not prove staged process quiescence.");
    }

    public static void ActiveRuntimeIsQuiescedBeforeSwap()
    {
        using Fixture fixture = new("healthy");
        fixture.RuntimeQuiescenceVerifier.OnVerify = root =>
            Assert(
                File.ReadAllText(Path.Combine(root, "DevBridge.cmd")) == "healthy",
                "runtime swap started before quiescence completed");
        ToolchainPromotionResult result = fixture.Promote();
        Assert(result.Status == "promoted", "quiesced runtime promotion failed");
        Assert(fixture.RuntimeQuiescenceVerifier.Calls == 1, "runtime quiescence was not invoked exactly once");
        Assert(result.RuntimeQuiescence?.Status == "stopped", "promotion omitted stopped-runtime evidence");
    }

    public static void AlreadyAbsentRuntimeSkipsShutdown()
    {
        using Fixture fixture = new("healthy");
        fixture.RuntimeQuiescenceVerifier.Status = "already-absent";
        ToolchainPromotionResult result = fixture.Promote();
        Assert(result.Status == "promoted", "already-absent runtime promotion failed");
        Assert(fixture.RuntimeQuiescenceVerifier.Calls == 1, "already-absent runtime was not probed");
        Assert(fixture.RuntimeQuiescenceVerifier.ShutdownCalls == 0, "already-absent runtime issued shutdown");
    }

    public static void FailedRuntimeQuiescenceBlocksWithoutMutation()
    {
        using Fixture fixture = new("healthy");
        string beforeManifest = File.ReadAllText(fixture.ManifestPath);
        fixture.RuntimeQuiescenceVerifier.Pass = false;
        fixture.RuntimeQuiescenceVerifier.Error = "shutdown refused";
        ToolchainPromotionResult result = fixture.Promote();
        Assert(result.ErrorCode == "PROMOTION_RUNTIME_QUIESCE_FAILED", "quiescence failure was not classified");
        Assert(result.PromotionPhase == "runtime-quiescence", "quiescence failure phase was not recorded");
        Assert(File.ReadAllText(fixture.ManifestPath) == beforeManifest, "quiescence failure changed the active manifest");
        Assert(File.ReadAllText(Path.Combine(fixture.ProductionRuntimeRoot, "DevBridge.cmd")) == "healthy",
            "quiescence failure changed the active runtime");
        Assert(fixture.RuntimeDirectoryTransaction.CommitCalls == 0, "swap ran after quiescence failure");
    }

    public static void RuntimeSwapFailureIncludesTransactionEvidence()
    {
        using Fixture fixture = new("healthy");
        fixture.RuntimeDirectoryTransaction.ThrowOnCommit = true;
        ToolchainPromotionResult result = fixture.Promote();
        Assert(result.ErrorCode == "PROMOTION_RUNTIME_SWAP_FAILED", "runtime swap failure was not classified");
        Assert(!string.IsNullOrWhiteSpace(result.TransactionEvidence?.SourcePath) &&
               !string.Equals(result.TransactionEvidence.SourcePath, fixture.ProductionRuntimeRoot,
                   StringComparison.OrdinalIgnoreCase),
            "runtime swap evidence omitted the staged source path");
        Assert(result.TransactionEvidence?.DestinationPath == fixture.ProductionRuntimeRoot,
            "runtime swap evidence omitted the production destination path");
        Assert(result.TransactionEvidence?.HResult is not null, "runtime swap evidence omitted HResult");
        Assert(result.RuntimeQuiescence?.Status == "stopped", "runtime swap evidence omitted quiescence proof");
    }

    public static void ProductionQuiescenceUsesProbeShutdownProbe()
    {
        string root = Path.Combine(Path.GetTempPath(), "rimliaison-runtime-quiescence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(
                Path.Combine(root, "DevBridge.cmd"),
                """
                @echo off
                pwsh -NoLogo -NoProfile -NonInteractive -File "%~dp0fake-devbridge.ps1" %*
                exit /b %ERRORLEVEL%
                """);
            File.WriteAllText(
                Path.Combine(root, "fake-devbridge.ps1"),
                """
                $Arguments = $args
                $root = [IO.Path]::GetFullPath($PSScriptRoot)
                $marker = Join-Path $root "shutdown.marker"
                if ($Arguments -contains "probe") {
                    if (Test-Path -LiteralPath $marker -PathType Leaf) {
                        @{ state = "Absent"; runtimeRoot = $root } | ConvertTo-Json -Compress
                        exit 1
                    }
                    @{ state = "Responsive"; runtimeRoot = $root; coordinatorPid = 1234 } |
                        ConvertTo-Json -Compress
                    exit 0
                }
                if ($Arguments -contains "shutdown") {
                    New-Item -ItemType File -Path $marker | Out-Null
                    @{ success = $true } | ConvertTo-Json -Compress
                    exit 0
                }
                exit 3
                """);

            PromotionRuntimeQuiescenceResult result =
                new ProductionRuntimeQuiescenceVerifier()
                    .VerifyAsync(root, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            Assert(result.Passed, result.Error ?? "production quiescence verifier failed");
            Assert(result.Evidence.Status == "stopped", "production verifier did not report stopped state");
            Assert(result.Evidence.Before?.State == "Responsive", "production verifier skipped responsive probe");
            Assert(result.Evidence.Shutdown?.ExitCode == 0, "production verifier did not accept shutdown");
            Assert(result.Evidence.After?.State == "Absent", "production verifier did not prove absent state");
            Assert(result.Evidence.After?.CoordinatorPid is null, "production verifier accepted a remaining PID");
            Assert(result.Evidence.After?.RuntimeRoot == root, "production verifier accepted a different runtime root");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
    public static void ExplicitBootstrapRetiresLegacyAndInstallsModern()
    {
        using Fixture fixture = new("missing", modernState: false);
        ToolchainPromotionResult result = fixture.Promote(bootstrap: true);
        Assert(result.Status == "promoted", $"explicit bootstrap did not promote: {result.ErrorCode} {result.Error}");
        Assert(result.PreviousProductionHealth!.StartsWith("NO_PRODUCTION", StringComparison.Ordinal),
            "bootstrap did not classify the legacy baseline as NO_PRODUCTION");
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(fixture.ManifestPath));
        Assert(manifest.RootElement.GetProperty("productionState").GetString() == "MODERN_PRODUCTION",
            "bootstrap did not publish a modern production state");
        Assert(Directory.Exists(fixture.ProductionRuntimeRoot) &&
               File.Exists(Path.Combine(fixture.ProductionRuntimeRoot, "DevBridge.cmd")),
            "bootstrap did not install the modern runtime");
        Assert(Directory.EnumerateFiles(fixture.Root, "*.legacy-*.json").Any(),
            "bootstrap did not archive the legacy manifest");
    }

    public static void BootstrapRejectsExistingModernIdentity()
    {
        using Fixture fixture = new("missing");
        Assert(fixture.Promote().Status == "promoted", "modern setup promotion failed");
        ToolchainPromotionResult result = fixture.Promote(bootstrap: true);
        Assert(result.ErrorCode == "BOOTSTRAP_MODERN_PRODUCTION_EXISTS",
            "bootstrap overwrote an existing modern identity");
    }

    public static void BootstrapFailureLeavesNoProduction()
    {
        using Fixture fixture = new("missing", modernState: false) { CanonicalPass = false };
        ToolchainPromotionResult result = fixture.Promote(bootstrap: true);
        Assert(result.ErrorCode == "PROMOTION_POST_COMMIT_HEALTH_FAILED",
            "bootstrap post-commit failure was not classified");
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(fixture.ManifestPath));
        Assert(manifest.RootElement.GetProperty("productionState").GetString() == "NO_PRODUCTION" &&
               manifest.RootElement.GetProperty("bootstrapStatus").GetString() == "BOOTSTRAP_FAILED",
            "bootstrap failure did not leave an explicit NO_PRODUCTION state");
        Assert(!Directory.Exists(fixture.ProductionRuntimeRoot),
            "bootstrap failure left a partially installed runtime authoritative");
        Assert(Directory.EnumerateFiles(fixture.Root, "*.legacy-*.json").Any(),
            "bootstrap failure lost the retired legacy manifest evidence");
    }
    public static void OrdinaryPromotionRejectsLegacyBaseline()
    {
        using Fixture fixture = new("missing", modernState: false);
        ToolchainPromotionResult result = fixture.Promote();
        Assert(result.ErrorCode == "PROMOTION_LEGACY_BASELINE_REQUIRES_BOOTSTRAP",
            "ordinary promotion silently accepted the legacy baseline");
    }

    public static void BootstrapRejectsExecutableLegacyRuntime()
    {
        using Fixture fixture = new("missing", modernState: false);
        Directory.CreateDirectory(Path.Combine(fixture.ProductionRuntimeRoot, "Coordinator"));
        File.WriteAllText(
            Path.Combine(fixture.ProductionRuntimeRoot, "Coordinator", "DevBridge.Coordinator.exe"),
            "legacy-coordinator");
        ToolchainPromotionResult result = fixture.Promote(bootstrap: true);
        Assert(result.ErrorCode == "BOOTSTRAP_RUNTIME_PRECONDITION_FAILED",
            "bootstrap accepted an executable legacy runtime baseline");
        Assert(File.Exists(fixture.ManifestPath),
            "runtime precondition failure removed the active legacy manifest");
    }
    public static void IsolatedCandidateUsesNarrowCapabilitiesProbe()
    {
        using IsolatedCandidateFixture fixture = new();
        PromotionCandidateHealthResult result = fixture.Verify();
        Assert(result.Passed, result.Error ?? "isolated candidate health failed");
        using JsonDocument summary = JsonDocument.Parse(result.Summary);
        JsonElement checks = summary.RootElement;
        Assert(
            checks.GetProperty("rimLiaisonCapabilities").GetString() == "ready",
            "candidate health did not pass the narrow capabilities probe");
        Assert(
            !checks.TryGetProperty("rimLiaisonDoctor", out _),
            "candidate health still reports the full workspace doctor");
        Assert(
            fixture.LiveVerifier.Calls == 1,
            "candidate health did not complete the live staged-runtime verification");
    }

    public static void CandidateCapabilitiesProbeFailureBlocksHealth()
    {
        using IsolatedCandidateFixture fixture = new("capability-failure");
        PromotionCandidateHealthResult result = fixture.Verify();
        Assert(!result.Passed, "capabilities probe failure was accepted");
        Assert(
            result.Error?.Contains("capabilities probe", StringComparison.OrdinalIgnoreCase) == true,
            "capabilities probe failure was not classified");
        Assert(
            fixture.LiveVerifier.Calls == 0,
            "candidate health continued after capabilities probe failure");
    }

    public static void CandidateCapabilitiesRejectsWrongRuntimeIdentity()
    {
        using IsolatedCandidateFixture fixture = new("wrong-runtime");
        PromotionCandidateHealthResult result = fixture.Verify();
        Assert(!result.Passed, "wrong runtime identity was accepted");
        Assert(
            result.Error?.Contains("different runtime root", StringComparison.OrdinalIgnoreCase) == true,
            "wrong runtime identity was not rejected");
    }
    public static void CandidateDevBridgeHealthFailuresBlockCandidate()
    {
        foreach (string mode in new[] { "status-failure", "doctor-failure" })
        {
            using IsolatedCandidateFixture fixture = new(mode);
            PromotionCandidateHealthResult result = fixture.Verify();
            Assert(!result.Passed, $"{mode} was accepted");
            Assert(fixture.LiveVerifier.Calls == 0, $"{mode} continued to live verification");
        }
    }



    public static void MissingCandidateDllFailsAccurately()
    {
        using Fixture fixture = new("missing");
        File.Delete(Path.Combine(fixture.CandidateRuntimeRoot, "1.6", "Assemblies", "DevBridge2.dll"));
        ToolchainPromotionResult result = fixture.Promote();
        Assert(result.ErrorCode == "PROMOTION_RECOVERY_PAYLOAD_INVALID", "missing candidate DLL was not rejected");
        Assert(File.ReadAllText(fixture.ManifestPath) == fixture.ManifestBefore, "missing DLL changed active identity");
    }

    public static void CorruptCandidateCoordinatorFailsAccurately()
    {
        using Fixture fixture = new("missing");
        File.WriteAllText(Path.Combine(fixture.CandidateRuntimeRoot, "Coordinator", "DevBridge.Coordinator.exe"), "corrupt");
        ToolchainPromotionResult result = fixture.Promote();
        Assert(result.ErrorCode == "PROMOTION_RECOVERY_PAYLOAD_INVALID", "corrupt coordinator was not rejected");
        Assert(File.ReadAllText(fixture.ManifestPath) == fixture.ManifestBefore, "corrupt coordinator changed active identity");
    }

    public static void CandidateFingerprintMismatchFails()
    {
        using Fixture fixture = new("missing");
        fixture.MutateQualificationAssemblyHash();
        ToolchainPromotionResult result = fixture.Promote();
        Assert(result.ErrorCode == "PROMOTION_QUALIFIED_ARTIFACT_MISMATCH", "candidate fingerprint mismatch was accepted");
    }

    public static void PostCommitDoctorResolvesNewFingerprint()
    {
        using Fixture fixture = new("missing");
        ToolchainPromotionResult result = fixture.Promote();
        Assert(result.Status == "promoted" && result.PromotedFingerprint == fixture.ExpectedFingerprint,
            "post-commit health did not resolve the new fingerprint");
        Assert(fixture.CanonicalVerifier.InstalledExecutable is not null, "post-commit doctor was not invoked");
        Assert(fixture.CanonicalVerifier.ResolvedFingerprint == fixture.ExpectedFingerprint,
            "post-commit doctor did not resolve the active new fingerprint");
        Assert(result.CandidateHealth?.ActiveManifestChanged == true, "promotion evidence omitted manifest activation");
    }

    public static void PostCommitDoctorFailureRollsBack()
    {
        using Fixture fixture = new("missing") { CanonicalPass = false };
        string before = File.ReadAllText(fixture.ManifestPath);
        ToolchainPromotionResult result = fixture.Promote();
        Assert(result.ErrorCode == "PROMOTION_POST_COMMIT_HEALTH_FAILED", "post-commit failure was not classified");
        using JsonDocument beforeManifest = JsonDocument.Parse(before);
        using JsonDocument afterManifest = JsonDocument.Parse(File.ReadAllText(fixture.ManifestPath));
        Assert(
            afterManifest.RootElement.GetProperty("promotedFingerprint").GetString() ==
            beforeManifest.RootElement.GetProperty("promotedFingerprint").GetString(),
            "post-commit failure left a new manifest active");
        Assert(!Directory.Exists(Path.Combine(fixture.ProductionRuntimeRoot, "Coordinator")),
            "post-commit rollback left candidate runtime contents active");
    }

    public static void FailedPromotionNeverChangesActiveIdentity()
    {
        using Fixture fixture = new("healthy");
        string before = File.ReadAllText(fixture.ManifestPath);
        ToolchainPromotionResult result = fixture.Promote(candidatePass: false);
        Assert(result.Status == "blocked", "failed promotion did not block");
        Assert(File.ReadAllText(fixture.ManifestPath) == before, "failed promotion changed active identity");
    }

    public static void ConcurrentPromotionsAreLockSafe()
    {
        using Fixture fixture = new("missing") { CandidateDelay = TimeSpan.FromMilliseconds(250) };
        Task<ToolchainPromotionResult> first = Task.Run(() => fixture.Promote());
        Task<ToolchainPromotionResult> second = Task.Run(() => fixture.Promote());
        Task.WaitAll(first, second);
        Assert(
            new[] { first.Result.Status, second.Result.Status }.Count(status => status == "promoted") == 1 &&
            new[] { first.Result.ErrorCode, second.Result.ErrorCode }.Any(code =>
                code is "PROMOTION_LOCKED" or "PROMOTION_DESTINATION_EXISTS"),
            $"concurrent promotions did not preserve single-writer transaction safety: {first.Result.Status}/{first.Result.ErrorCode}/{first.Result.Error}, {second.Result.Status}/{second.Result.ErrorCode}/{second.Result.Error}");
    }

    public static void CancellationLeavesDeterministicRollbackState()
    {
        using Fixture fixture = new("missing") { CancelCandidate = true };
        ToolchainPromotionResult result = fixture.Promote();
        Assert(result.ErrorCode == "PROMOTION_TRANSACTION_FAILED", "cancellation was not contained by the transaction");
        using JsonDocument beforeManifest = JsonDocument.Parse(fixture.ManifestBefore);
        using JsonDocument afterManifest = JsonDocument.Parse(File.ReadAllText(fixture.ManifestPath));
        Assert(
            afterManifest.RootElement.GetProperty("promotedFingerprint").GetString() ==
            beforeManifest.RootElement.GetProperty("promotedFingerprint").GetString(),
            "cancellation changed the active manifest");
        Assert(!Directory.Exists(fixture.ProductionRuntimeRoot), "cancellation activated candidate runtime");
    }

    public static void GeneratedQualificationOutputDoesNotBlockPromotion()
    {
        using Fixture fixture = new("missing")
        {
            SourceChanges =
            [
                new GitRepositoryChange(".rimdev/qualification/latest.json", "??", false, true)
            ]
        };
        ToolchainPromotionResult result = fixture.Promote();
        Assert(result.Status == "promoted",
            $"generated qualification output blocked promotion: {result.ErrorCode} {result.Error}");
        Assert(result.MeaningfulDirtyPaths is null,
            "generated qualification output produced meaningful dirty-path evidence.");
    }

    public static void MeaningfulSourceChangeBlocksPromotion()
    {
        using Fixture fixture = new("missing")
        {
            SourceChanges =
            [
                new GitRepositoryChange(".rimdev/stack.json", "M", false, false)
            ]
        };
        ToolchainPromotionResult result = fixture.Promote();
        Assert(result.Status == "blocked" && result.ErrorCode == "PROMOTION_SOURCE_DIRTY",
            "meaningful source change did not block promotion.");
        Assert(result.MeaningfulDirtyPaths?.SequenceEqual([".rimdev/stack.json"]) == true,
            "promotion did not return the bounded meaningful dirty path.");
    }

    public static void UnknownTrackedArtifactBlocksPromotion()
    {
        using Fixture fixture = new("missing")
        {
            SourceChanges =
            [
                new GitRepositoryChange("random/location/Unknown.dll", "??", false, false)
            ]
        };
        ToolchainPromotionResult result = fixture.Promote();
        Assert(result.Status == "blocked" && result.ErrorCode == "PROMOTION_SOURCE_DIRTY",
            "unknown tracked artifact was silently ignored by promotion.");
        Assert(result.MeaningfulDirtyPaths?.SequenceEqual(["random/location/Unknown.dll"]) == true,
            "promotion did not preserve unknown tracked artifact evidence.");
    }

    public static void HeadMismatchStillBlocksPromotion()
    {
        using Fixture fixture = new("missing")
        {
            SourceHeadCommit = "different-source-commit"
        };
        ToolchainPromotionResult result = fixture.Promote();
        Assert(result.Status == "blocked" && result.ErrorCode == "PROMOTION_SOURCE_FINGERPRINT_STALE",
            "HEAD mismatch did not block exact promotion provenance.");
    }

    private static void AssertSuccess(string legacyState)
    {
        using Fixture fixture = new(legacyState);
        ToolchainPromotionResult result = fixture.Promote();
        Assert(result.Status == "promoted", $"healthy candidate could not replace {legacyState} production: {result.ErrorCode} {result.Error}");
        Assert(File.Exists(Path.Combine(fixture.ProductionRuntimeRoot, "DevBridge.cmd")), "candidate runtime was not committed");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class IsolatedCandidateFixture : IDisposable
    {
        private const string CandidateSourceCommit = "isolated-candidate-source";
        private readonly string mode;

        public string Root { get; } = Path.Combine(
            Path.GetTempPath(),
            "rimliaison-isolated-candidate-" + Guid.NewGuid().ToString("N"));
        public string CandidateExecutable { get; }
        public string RuntimeRoot { get; }
        public PassingLiveVerifier LiveVerifier { get; } = new();

        public IsolatedCandidateFixture(string mode = "healthy")
        {
            this.mode = mode;
            string candidateCliRoot = Path.Combine(Root, "candidate-cli");
            RuntimeRoot = Path.Combine(Root, "candidate-runtime");
            Directory.CreateDirectory(candidateCliRoot);
            Directory.CreateDirectory(RuntimeRoot);
            CopyDirectory(
                Path.GetDirectoryName(typeof(ToolchainPromotionService).Assembly.Location)!,
                candidateCliRoot);
            CandidateExecutable = Path.Combine(candidateCliRoot, "rimliaison.exe");
            File.WriteAllText(
                Path.Combine(RuntimeRoot, "DevBridge.cmd"),
                """
                @echo off
                pwsh -NoLogo -NoProfile -NonInteractive -File "%~dp0fake-devbridge.ps1" %1 %2 %3 %4 %5 %6
                exit /b %ERRORLEVEL%
                """);
            File.WriteAllText(
                Path.Combine(RuntimeRoot, "fake-devbridge.ps1"),
                """
                param([string[]] $Arguments)
                $root = [IO.Path]::GetFullPath($PSScriptRoot)
                $identityRoot = if ("__MODE__" -eq "wrong-runtime") {
                    Join-Path $root "wrong-runtime"
                } else {
                    $root
                }
                if ("__MODE__" -eq "capability-failure" -and $Arguments -contains "bridge") {
                    @{ success = $false; errorCode = "RIMBRIDGE_NOT_READY"; error = "capability probe failed" } |
                        ConvertTo-Json -Compress -Depth 5
                    exit 3
                }
                if ($Arguments -contains "bridge") {
                    @{
                        success = $true
                        rimBridgeRoute = @{
                            success = $true
                            result = @{ tools = @() }
                        }
                    } | ConvertTo-Json -Compress -Depth 5
                    exit 0
                }
                if ($Arguments -contains "restart") {
                    @{ status = "ready" } | ConvertTo-Json -Compress -Depth 5
                    exit 0
                }
                if ("__MODE__" -eq "status-failure" -and $Arguments -contains "status") {
                    @{
                        status = "failed"
                        runtimeIdentity = @{ devBridgeRuntimeRoot = $identityRoot }
                        activeTests = 0
                        generation = 7
                    } | ConvertTo-Json -Compress -Depth 5
                    exit 3
                }
                if ($Arguments -contains "status") {
                    @{
                        status = "ready"
                        runtimeIdentity = @{ devBridgeRuntimeRoot = $identityRoot }
                        activeTests = 0
                        generation = 7
                    } | ConvertTo-Json -Compress -Depth 5
                    exit 0
                }
                if ("__MODE__" -eq "doctor-failure" -and $Arguments -contains "doctor") {
                    @{
                        status = "ready"
                        healthy = $false
                        runtimeIdentity = @{ devBridgeRuntimeRoot = $identityRoot }
                        findings = @(
                            @{ code = "LEASES_VALIDATED"; details = @{ leaseCount = "0" } }
                        )
                    } | ConvertTo-Json -Compress -Depth 5
                    exit 3
                }
                if ($Arguments -contains "doctor") {
                    @{
                        status = "ready"
                        healthy = $true
                        runtimeIdentity = @{ devBridgeRuntimeRoot = $identityRoot }
                        findings = @(
                            @{ code = "LEASES_VALIDATED"; details = @{ leaseCount = "0" } }
                        )
                    } | ConvertTo-Json -Compress -Depth 5
                    exit 0
                }
                if ($Arguments -contains "coordinator") {
                    $marker = Join-Path $root "coordinator-shutdown.marker"
                    if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) {
                        New-Item -ItemType File -Path $marker | Out-Null
                        @{ success = $true } | ConvertTo-Json -Compress -Depth 5
                        exit 0
                    }
                    @{ state = "Absent"; runtimeRoot = $identityRoot } | ConvertTo-Json -Compress -Depth 5
                    exit 1
                }
                @{ success = $false; errorCode = "FAKE_COMMAND_UNSUPPORTED"; error = "unsupported" } |
                    ConvertTo-Json -Compress -Depth 5
                exit 3
                """.Replace("__MODE__", mode, StringComparison.Ordinal));
        }

        public PromotionCandidateHealthResult Verify()
        {
            var binding = new PromotionCandidateHealthBinding(
                CandidateExecutable,
                RuntimeRoot,
                "candidate-fingerprint",
                CandidateSourceCommit,
                "candidate-package",
                "candidate-coordinator",
                "candidate-consumer",
                ToolchainPromotionSchemas.RuntimeProtocolContract,
                Environment.ProcessPath!);
            return ToolchainPromotionService.RunCandidateHealthAsync(
                    binding,
                    "isolated-candidate-workflow",
                    CancellationToken.None,
                    LiveVerifier)
                .GetAwaiter()
                .GetResult();
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(destination, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
        }
    }

    private sealed class PassingLiveVerifier : IPromotionLeaseOrchestrator
    {
        public int Calls { get; private set; }

        public Task<PromotionLiveVerificationResult> VerifyCapabilitiesAsync(
            string workflowId,
            int? expectedGeneration,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new PromotionLiveVerificationResult(
                true,
                null,
                null,
                "candidate-lease",
                expectedGeneration,
                true,
                1,
                "capabilities-check"));
        }
    }

    private sealed class Fixture : IDisposable
    {
        private const string SourceCommit = "source-commit";
        private readonly string? previousManifestEnvironment;
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "rimliaison-bootstrap-test-" + Guid.NewGuid().ToString("N"));
        public string ManifestPath { get; }
        public string PackagePath { get; }
        public string CandidateRuntimeRoot { get; }
        public string ProductionRuntimeRoot { get; }
        public string ExpectedCandidateRuntimeRoot { get; }
        public string ExpectedFingerprint { get; }
        public IReadOnlyList<GitRepositoryChange> SourceChanges { get; init; } = [];
        public string? SourceHeadCommit { get; init; }
        public string ManifestBefore { get; }
        public FakeCandidateVerifier CandidateVerifier { get; } = new();
        public FakeCanonicalVerifier CanonicalVerifier { get; } = new();
        public FakeRuntimeQuiescenceVerifier RuntimeQuiescenceVerifier { get; } = new();
        public FakeRuntimeDirectoryTransaction RuntimeDirectoryTransaction { get; } = new();
        public bool CanonicalPass { get; init; } = true;
        public bool CancelCandidate { get; init; }
        public TimeSpan CandidateDelay { get; init; }

        public Fixture(string legacyState, bool modernState = true)
        {
            Directory.CreateDirectory(Root);
            string artifactRoot = Path.Combine(Root, "qualified-artifacts");
            string candidatePayloadRoot = Path.Combine(Root, "qualified-runtime");
            string transactionRoot = Path.Combine(artifactRoot, "transaction-components");
            CandidateRuntimeRoot = candidatePayloadRoot;
            ExpectedCandidateRuntimeRoot = candidatePayloadRoot;
            ProductionRuntimeRoot = Path.Combine(Root, "production", "DevBridge2");
            ManifestPath = Path.Combine(Root, "production-toolchain.json");
            PackagePath = Path.Combine(Root, "qualified-package.json");
            Directory.CreateDirectory(artifactRoot);
            Directory.CreateDirectory(Path.Combine(candidatePayloadRoot, "Coordinator"));
            Directory.CreateDirectory(Path.Combine(candidatePayloadRoot, "1.6", "Assemblies"));
            Directory.CreateDirectory(transactionRoot);
            File.WriteAllText(Path.Combine(artifactRoot, "rimliaison.exe"), "candidate-executable");
            File.WriteAllText(Path.Combine(artifactRoot, "rimliaison.dll"), "candidate-assembly");
            File.WriteAllText(Path.Combine(transactionRoot, "mod-test.ps1"), "candidate-consumer");
            File.WriteAllText(Path.Combine(artifactRoot, "unified-package.json"), "{}");
            File.WriteAllText(Path.Combine(candidatePayloadRoot, "DevBridge.cmd"), "candidate-runtime");
            File.WriteAllText(Path.Combine(candidatePayloadRoot, "Coordinator", "DevBridge.Coordinator.exe"), "candidate-coordinator");
            File.WriteAllText(Path.Combine(candidatePayloadRoot, "1.6", "Assemblies", "DevBridge2.dll"), "candidate-mod");
            string coordinatorHash = Hash(Path.Combine(candidatePayloadRoot, "Coordinator", "DevBridge.Coordinator.exe"));
            string modHash = Hash(Path.Combine(candidatePayloadRoot, "1.6", "Assemblies", "DevBridge2.dll"));
            File.WriteAllText(
                Path.Combine(candidatePayloadRoot, ".devbridge-runtime-manifest.json"),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = "devbridge-runtime-manifest/v1",
                    ownerProduct = ToolchainPromotionSchemas.OwnerProduct,
                    productionEligible = false,
                    sourceCommit = SourceCommit,
                    componentRole = "DevBridge runtime",
                    project = ToolchainPromotionSchemas.OwnerProduct,
                    packageId = "lan.devbridge2",
                    runtimeProtocolContract = ToolchainPromotionSchemas.RuntimeProtocolContract,
                    packageSha256 = "candidate-package",
                    files = new[]
                    {
                        new { path = "Coordinator/DevBridge.Coordinator.exe", sha256 = coordinatorHash },
                        new { path = "1.6/Assemblies/DevBridge2.dll", sha256 = modHash }
                    }
                }));
            string runtimeManifestHash = Hash(Path.Combine(candidatePayloadRoot, ".devbridge-runtime-manifest.json"));

            string executableHash = Hash(Path.Combine(artifactRoot, "rimliaison.exe"));
            string assemblyHash = Hash(Path.Combine(artifactRoot, "rimliaison.dll"));
            string consumerHash = Hash(Path.Combine(transactionRoot, "mod-test.ps1"));
            ExpectedFingerprint = ToolchainPromotionService.ComputePromotedFingerprint(
                SourceCommit,
                executableHash,
                assemblyHash,
                coordinatorHash,
                "candidate-package",
                consumerHash,
                ToolchainPromotionSchemas.RuntimeProtocolContract,
                ToolchainPromotionSchemas.OwnerProduct,
                ToolchainPromotionSchemas.RuntimeSubsystem,
                modHash,
                runtimeManifestHash);
            File.WriteAllText(
                Path.Combine(artifactRoot, "unified-package.json"),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = "rimliaison-unified-production-package/v3",
                    productFingerprint = ExpectedFingerprint,
                    ownerProduct = ToolchainPromotionSchemas.OwnerProduct,
                    runtimeSubsystem = ToolchainPromotionSchemas.RuntimeSubsystem,
                    rimBridgeServer = new { boundary = ToolchainPromotionSchemas.RimBridgeServerBoundary },
                    runtimeManifestSha256 = runtimeManifestHash,
                    modSha256 = modHash
                }));

            string qualificationPath = Path.Combine(Root, "qualification.json");
            File.WriteAllText(
                qualificationPath,
                JsonSerializer.Serialize(new
                {
                    sourceCommit = SourceCommit,
                    passes = 1,
                    totalRuns = 1,
                    infrastructureFailures = 0,
                    fixtureFailures = 0,
                    qualifiedArtifactHashes = new
                    {
                        rimLiaisonExecutableSha256 = executableHash,
                        rimLiaisonAssemblySha256 = assemblyHash,
                        devBridgeModSha256 = modHash,
                        devBridgeRuntimeManifestSha256 = runtimeManifestHash,
                        devBridgePackageSha256 = "candidate-package",
                        devBridgeCoordinatorSha256 = coordinatorHash,
                        transactionConsumerSha256 = consumerHash
                    }
                }));

            string oldCliDirectory = Path.Combine(Root, "production", "old-cli");
            Directory.CreateDirectory(oldCliDirectory);
            string oldCli = Path.Combine(oldCliDirectory, "rimliaison.exe");
            string oldAssembly = Path.Combine(oldCliDirectory, "rimliaison.dll");
            string oldConsumer = Path.Combine(oldCliDirectory, "transaction-components", "mod-test.ps1");
            string oldUnified = Path.Combine(oldCliDirectory, "unified-package.json");
            Directory.CreateDirectory(Path.GetDirectoryName(oldConsumer)!);
            File.WriteAllText(oldCli, "old-executable");
            File.WriteAllText(oldAssembly, "old-assembly");
            File.WriteAllText(oldConsumer, "candidate-consumer");
            File.WriteAllText(oldUnified, "{}");
            if (legacyState is "healthy" or "corrupt")
            {
                Directory.CreateDirectory(ProductionRuntimeRoot);
                File.WriteAllText(Path.Combine(ProductionRuntimeRoot, "DevBridge.cmd"), legacyState);
            }
            string oldCliHash = Hash(oldCli);
            if (legacyState == "unrecoverable")
                File.Delete(oldCli);
            File.WriteAllText(
                ManifestPath,
                JsonSerializer.Serialize(new
                {
                    schemaVersion = "rimliaison-production-toolchain/v1",
                    promotedFingerprint = "previous-fingerprint",
                    fingerprint = "previous-fingerprint",
                    ownerProduct = ToolchainPromotionSchemas.OwnerProduct,
                    runtimeSubsystem = ToolchainPromotionSchemas.RuntimeSubsystem,
                    rimLiaisonExecutablePath = oldCli,
                    rimLiaisonExecutableSha256 = oldCliHash,
                    rimLiaisonAssemblyPath = oldAssembly,
                    rimLiaisonAssemblySha256 = Hash(oldAssembly),
                    devBridgeRuntimeRoot = ProductionRuntimeRoot,
                    devBridgePackageSha256 = "old-package",
                    devBridgeCoordinatorSha256 = coordinatorHash,
                    transactionConsumerPath = oldConsumer,
                    transactionConsumerSha256 = Hash(oldConsumer),
                    unifiedManifestPath = oldUnified,
                    unifiedManifestSha256 = Hash(oldUnified),
                    runtimeProtocolContract = ToolchainPromotionSchemas.RuntimeProtocolContract,
                    qualificationArtifactSha256 = "old-proof"
                }));
            string qualificationHash = Hash(qualificationPath);
            File.WriteAllText(
                PackagePath,
                JsonSerializer.Serialize(new
                {
                    schemaVersion = ToolchainPromotionSchemas.LegacyPackage,
                    sourceCommit = SourceCommit,
                    qualificationArtifactPath = qualificationPath,
                    qualificationArtifactSha256 = qualificationHash,
                    artifactRoot,
                    rimLiaisonExecutableRelativePath = "rimliaison.exe",
                    rimLiaisonAssemblyRelativePath = "rimliaison.dll",
                    rimLiaisonExecutableSha256 = executableHash,
                    rimLiaisonAssemblySha256 = assemblyHash,
                    ownerProduct = ToolchainPromotionSchemas.OwnerProduct,
                    runtimeSubsystem = ToolchainPromotionSchemas.RuntimeSubsystem,
                    devBridgeRuntimeRoot = ProductionRuntimeRoot,
                    devBridgeRuntimeArtifactRoot = candidatePayloadRoot,
                    devBridgePackageSha256 = "candidate-package",
                    devBridgeCoordinatorSha256 = coordinatorHash,
                    devBridgeModSha256 = modHash,
                    devBridgeRuntimeManifestSha256 = runtimeManifestHash,
                    transactionConsumerPath = Path.Combine(transactionRoot, "mod-test.ps1"),
                    transactionConsumerRelativePath = "transaction-components/mod-test.ps1",
                    transactionConsumerSha256 = consumerHash,
                    unifiedManifestRelativePath = "unified-package.json",
                    runtimeProtocolContract = ToolchainPromotionSchemas.RuntimeProtocolContract
                }));
            if (modernState)
            {
                var manifest = System.Text.Json.Nodes.JsonNode.Parse(
                    File.ReadAllText(ManifestPath))!.AsObject();
                manifest["productionState"] = "MODERN_PRODUCTION";
                manifest["promotionPackagePath"] = Path.GetFullPath(PackagePath);
                manifest["promotionPackageSha256"] = Hash(PackagePath);
                File.WriteAllText(ManifestPath, manifest.ToJsonString());
            }
            ManifestBefore = File.ReadAllText(ManifestPath);
            previousManifestEnvironment = Environment.GetEnvironmentVariable("RIMLIAISON_PRODUCTION_TOOLCHAIN_MANIFEST");
            Environment.SetEnvironmentVariable("RIMLIAISON_PRODUCTION_TOOLCHAIN_MANIFEST", ManifestPath);
            CandidateVerifier.ExpectedRuntimeRoot = null;
        }

        public ToolchainPromotionResult Promote(bool candidatePass = true, bool bootstrap = false)
        {
            CandidateVerifier.Pass = candidatePass;
            CandidateVerifier.Delay = CandidateDelay;
            CandidateVerifier.Cancel = CancelCandidate;
            CanonicalVerifier.Pass = CanonicalPass;
            return ToolchainPromotionService.PromoteAsync(
                    Root,
                    PackagePath,
                    null,
                    bootstrap: bootstrap,
                    promotionHealthVerifier: CandidateVerifier,
                    canonicalHealthVerifier: CanonicalVerifier,
                    gitRepositoryStateProvider: new FakeGitProvider(
                        SourceHeadCommit ?? SourceCommit,
                        SourceChanges),
                    machinePreflightVerifier: new ReadyMachinePreflight(),
                    runtimeQuiescenceVerifier: RuntimeQuiescenceVerifier,
                    runtimeDirectoryTransaction: RuntimeDirectoryTransaction)
                .GetAwaiter()
                .GetResult();
        }

        public void MutateQualificationAssemblyHash()
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "qualification.json")));
            var node = System.Text.Json.Nodes.JsonNode.Parse(document.RootElement.GetRawText())!.AsObject();
            node["qualifiedArtifactHashes"]!["rimLiaisonAssemblySha256"] = "mismatch";
            string path = Path.Combine(Root, "qualification.json");
            File.WriteAllText(path, node.ToJsonString());
            var package = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(PackagePath))!.AsObject();
            package["qualificationArtifactSha256"] = Hash(path);
            File.WriteAllText(PackagePath, package.ToJsonString());
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("RIMLIAISON_PRODUCTION_TOOLCHAIN_MANIFEST", previousManifestEnvironment);
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }

        private static string Hash(string path)
        {
            using FileStream stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
    }

    private sealed class FakeGitProvider(
        string commit,
        IReadOnlyList<GitRepositoryChange> changes) : IGitRepositoryStateProvider
    {
        public Task<GitRepositoryStateResult> ReadAsync(string rootPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GitRepositoryStateResult(true, new GitRepositoryStateSnapshot(
                rootPath,
                "fixture",
                null,
                commit,
                null,
                0,
                0,
                changes.Count > 0,
                changes)));
    }

    private sealed class FakeRuntimeQuiescenceVerifier : IPromotionRuntimeQuiescenceVerifier
    {
        public bool Pass { get; set; } = true;
        public string Status { get; set; } = "stopped";
        public string? Error { get; set; }
        public int Calls { get; private set; }
        public int ShutdownCalls { get; private set; }
        public Action<string>? OnVerify { get; set; }

        public Task<PromotionRuntimeQuiescenceResult> VerifyAsync(
            string runtimeRoot,
            CancellationToken cancellationToken)
        {
            Calls++;
            OnVerify?.Invoke(runtimeRoot);
            if (Status != "already-absent")
                ShutdownCalls++;
            string? error = Error ?? (Pass ? null : "runtime quiescence failed");
            PromotionRuntimeQuiescenceEvidence evidence = new(
                Status,
                runtimeRoot,
                Path.Combine(runtimeRoot, "DevBridge.cmd"),
                null,
                null,
                null,
                error);
            return Task.FromResult(new PromotionRuntimeQuiescenceResult(
                Pass,
                error ?? "runtime quiescence passed",
                error,
                evidence));
        }
    }

    private sealed class FakeRuntimeDirectoryTransaction : IPromotionRuntimeDirectoryTransaction
    {
        public bool ThrowOnCommit { get; set; }
        public int CommitCalls { get; private set; }

        public void Commit(string stagedRoot, string targetRoot, out string? backupRoot)
        {
            CommitCalls++;
            if (ThrowOnCommit)
            {
                backupRoot = null;
                throw new IOException("injected runtime swap sharing violation");
            }
            ToolchainPromotionService.CommitRuntimeDirectoryForTransaction(
                stagedRoot,
                targetRoot,
                out backupRoot);
        }

        public void Restore(string targetRoot, string? backupRoot) =>
            ToolchainPromotionService.RestoreRuntimeDirectoryForTransaction(targetRoot, backupRoot);
    }

    private sealed class FakeCandidateVerifier : IPromotionCandidateHealthVerifier
    {
        public bool Pass { get; set; }
        public bool Cancel { get; set; }
        public TimeSpan Delay { get; set; }
        public string? ExpectedRuntimeRoot { get; set; }
        public PromotionCandidateHealthBinding? Binding { get; private set; }

        public async Task<PromotionCandidateHealthResult> VerifyAsync(
            PromotionCandidateHealthBinding binding,
            string workflowId,
            CancellationToken cancellationToken)
        {
            Binding = binding;
            if (Delay > TimeSpan.Zero)
                await Task.Delay(Delay, cancellationToken);
            if (Cancel)
                throw new OperationCanceledException(cancellationToken);
            bool bound = ExpectedRuntimeRoot is null ||
                string.Equals(
                    Path.GetFullPath(binding.CandidateRuntimeRoot),
                    Path.GetFullPath(ExpectedRuntimeRoot),
                    StringComparison.OrdinalIgnoreCase);
            bool pass = Pass && bound;
            return new(
                pass,
                "fixture-candidate-health",
                pass ? null : "fixture candidate health failed",
                new PromotionCandidateHealthEvidence(
                    "candidate-pre-commit",
                    pass ? "passed" : "failed",
                    binding.CandidateFingerprint,
                    binding.CandidateRuntimeRoot,
                    binding.CandidateExecutable,
                    binding.CandidateSourceCommit,
                    binding.DevBridgePackageSha256,
                    binding.DevBridgeCoordinatorSha256,
                    binding.TransactionConsumerSha256,
                    binding.RuntimeProtocolContract,
                    pass ? null : "fixture candidate health failed")
                {
                    DevBridgeModSha256 = binding.DevBridgeModSha256,
                    DevBridgeRuntimeManifestSha256 = binding.DevBridgeRuntimeManifestSha256
                });
        }
    }

    private sealed class ReadyMachinePreflight : IPromotionMachinePreflightVerifier
    {
        public ProductionMachinePreflightResult Verify(string sourceRoot, string productionRuntimeRoot) =>
            new(
                "rimliaison-production-machine-preflight/v1",
                "ready",
                null,
                null,
                null,
                null,
                null,
                null,
                productionRuntimeRoot,
                false);
    }

    private sealed class FakeCanonicalVerifier : IPromotionCanonicalHealthVerifier
    {
        public bool Pass { get; set; }
        public string? InstalledExecutable { get; private set; }
        public string? ResolvedFingerprint { get; private set; }

        public Task<PromotionCanonicalHealthResult> VerifyAsync(
            string sourceRoot,
            string installedExecutable,
            string expectedFingerprint,
            CancellationToken cancellationToken)
        {
            InstalledExecutable = installedExecutable;
            string manifestPath = Environment.GetEnvironmentVariable(
                "RIMLIAISON_PRODUCTION_TOOLCHAIN_MANIFEST")!;
            using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            ResolvedFingerprint = manifest.RootElement.GetProperty("promotedFingerprint").GetString();
            return Task.FromResult(new PromotionCanonicalHealthResult(
                Pass,
                "fixture-canonical-health",
                Pass ? null : "fixture post-commit doctor failed"));
        }
    }
}
