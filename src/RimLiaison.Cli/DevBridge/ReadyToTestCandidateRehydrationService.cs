using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RimLiaison.Execution;
using RimLiaison.Git;
using RimLiaison.Stack;
namespace RimLiaison.DevBridge;

public sealed record ReadyToTestCandidateRehydrationOptions
{
    public required string CandidateId { get; init; }
    public required string PackageSha256 { get; init; }
    public required string ArtifactPath { get; init; }
    public required string ArtifactSha256 { get; init; }
    public required string SourceCommit { get; init; }
    public string? CandidateStoreRoot { get; init; }

    public string ResolveCandidateStoreRoot()
    {
        string? configured = CandidateStoreRoot ?? Environment.GetEnvironmentVariable("RIMLIAISON_CANDIDATE_STORE");
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured);
        string? stateRoot = Environment.GetEnvironmentVariable("RIMDEV_STATE_ROOT");
        if (!string.IsNullOrWhiteSpace(stateRoot))
            return Path.GetFullPath(Path.Combine(stateRoot, "candidates"));
        return Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RimLiaison", "rimdev-state", "candidates"));
    }
}

public sealed class ReadyToTestCandidateRehydrationService
{
    private const string Provenance = "rehydrated-frozen-candidate";

    public async Task<ReadyToTestCandidateResult> RehydrateAsync(
        string? project,
        string repositoryRoot,
        ReadyToTestCandidateRehydrationOptions options,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!IsSha256(options.PackageSha256) || !IsSha256(options.ArtifactSha256) ||
                !IsCommit(options.SourceCommit) || string.IsNullOrWhiteSpace(options.CandidateId))
            {
                return Failure("FROZEN_CANDIDATE_REHYDRATION_INVALID", "Frozen candidate identity inputs are malformed.");
            }

            string sourceRoot = Path.GetFullPath(repositoryRoot);
            StackManifestResolution stack = StackManifestResolver.Discover(sourceRoot);
            if (!stack.Found || stack.Manifest is null)
                return Failure(stack.ErrorCode ?? "PROJECT_METADATA_MISSING", stack.Error ?? "The project stack manifest is unavailable.");
            ProjectIdentity identity = ProjectIdentityResolver.Resolve(stack.Manifest, sourceRoot);
            if (!string.IsNullOrWhiteSpace(project) && !ProjectIdentityResolver.Matches(identity, project))
                return Failure("PROJECT_METADATA_OWNER_MISMATCH", "The requested project does not match the repository stack identity.");
            if (string.IsNullOrWhiteSpace(stack.Manifest.DeploymentTarget) || stack.Manifest.RuntimePackage is null)
                return Failure("PROJECT_METADATA_MISSING", "The project deployment target or runtime package metadata is missing.");

            GitRepositoryStateResult git = await new SystemGitRepositoryStateProvider()
                .ReadAsync(sourceRoot, cancellationToken).ConfigureAwait(false);
            if (!git.Resolved || git.State is null || !string.Equals(git.State.HeadSha, options.SourceCommit, StringComparison.OrdinalIgnoreCase))
                return Failure("FROZEN_CANDIDATE_REHYDRATION_IDENTITY_MISMATCH", "The frozen candidate source commit does not match the repository HEAD.");

            string[] sourcePaths = Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(sourceRoot, path).Replace('\\', '/'))
                .Where(path => !RepositoryChangeClassificationPolicy.Classify(path).IsGenerated)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (!WorktreeFingerprint.TryCompute(sourceRoot, sourcePaths, out string sourceFingerprint, out string? fingerprintError))
                return Failure("RIMTEST_SOURCE_FINGERPRINT_UNAVAILABLE", fingerprintError ?? "The source fingerprint could not be established.");

            string artifactPath = Path.GetFullPath(options.ArtifactPath);
            if (!File.Exists(artifactPath) || !string.Equals(HashFile(artifactPath), options.ArtifactSha256, StringComparison.OrdinalIgnoreCase))
                return Failure("FROZEN_CANDIDATE_REHYDRATION_IDENTITY_MISMATCH", "The frozen artifact does not match the declared artifact SHA256.");

            string candidateStore = options.ResolveCandidateStoreRoot();
            string finalBase = Path.Combine(candidateStore, SafeSegment(identity.CanonicalProjectId), options.CandidateId);
            string finalManifestPath = Path.Combine(finalBase, "candidate.json");
            if (File.Exists(finalManifestPath))
            {
                if (ReadyToTestCandidateVerifier.TryVerify(finalManifestPath, out VerifiedReadyToTestCandidate? existing, out _, out _) &&
                    existing is not null && IsExact(existing.Manifest, identity, sourceRoot, sourceFingerprint, options))
                    return new ReadyToTestCandidateResult(true, null, null, finalManifestPath, existing.Manifest, null);
                return Failure("FROZEN_CANDIDATE_REHYDRATION_CONFLICT", "The durable candidate identity already exists with different content.");
            }

            string temporaryBase = Path.Combine(candidateStore, SafeSegment(identity.CanonicalProjectId), ".rehydrate-" + Guid.NewGuid().ToString("N"));
            string runtimeRoot = Path.Combine(temporaryBase, "runtime");
            try
            {
                Directory.CreateDirectory(runtimeRoot);
                List<ReadyToTestCandidatePreparationService.PackageEntry> entries =
                    ReadyToTestCandidatePreparationService.BuildPackage(
                        sourceRoot,
                        runtimeRoot,
                        artifactPath,
                        stack.Manifest.RuntimePackage,
                        stack.Manifest.DeploymentTarget);
                string packageHash = ReadyToTestCandidatePreparationService.ComputePackageHash(entries);
                string candidateId = ReadyToTestCandidatePreparationService.HashText(
                    identity.CanonicalProjectId + "\0" + sourceRoot + "\0" + sourceFingerprint + "\0" + packageHash)[..32];
                if (!string.Equals(candidateId, options.CandidateId, StringComparison.Ordinal) ||
                    !string.Equals(packageHash, options.PackageSha256, StringComparison.OrdinalIgnoreCase))
                    return Failure("FROZEN_CANDIDATE_REHYDRATION_IDENTITY_MISMATCH", "The frozen candidate identity does not match the source and artifact package.");

                string proof = ReadyToTestCandidateVerifier.ComputeRehydrationProof(
                    options.CandidateId, options.PackageSha256, options.ArtifactSha256, options.SourceCommit, sourceFingerprint);
                ReadyToTestCandidateManifest manifest = new()
                {
                    CandidateId = options.CandidateId,
                    Project = stack.Manifest.Project,
                    CanonicalProjectId = identity.CanonicalProjectId,
                    PackageId = stack.Manifest.PackageId,
                    SourceRepositoryRoot = sourceRoot,
                    SourceFingerprint = sourceFingerprint,
                    StagedRuntimeRoot = runtimeRoot,
                    PackageSha256 = options.PackageSha256,
                    RequiredRuntimeDependencies = stack.Manifest.Dependencies ?? [],
                    BuildEvidenceIdentity = "rehydrated-frozen-candidate:" + options.CandidateId,
                    BuildStatus = "NOT_PERFORMED",
                    StaticValidationIdentity = "runtime-package:" + options.PackageSha256,
                    StaticValidationStatus = "PASS",
                    CreatedUtc = DateTimeOffset.UtcNow.ToString("O"),
                    State = "READY_TO_TEST",
                    Provenance = Provenance,
                    BuildPerformed = false,
                    ArtifactIdentityVerified = true,
                    ArtifactSha256 = options.ArtifactSha256,
                    SourceCommit = options.SourceCommit,
                    RehydrationEvidenceId = "rehydration:" + ReadyToTestCandidatePreparationService.HashText(
                        identity.CanonicalProjectId + "\0" + options.CandidateId + "\0" + options.SourceCommit),
                    RehydrationProof = proof
                };
                string temporaryManifest = Path.Combine(temporaryBase, "candidate.json");
                WriteAtomic(temporaryManifest, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
                if (!ReadyToTestCandidateVerifier.TryVerify(temporaryManifest, out VerifiedReadyToTestCandidate? verified, out string verifyCode, out string verifyError) ||
                    verified is null)
                    return Failure(verifyCode, verifyError);

                Directory.CreateDirectory(Path.GetDirectoryName(finalBase)!);
                try
                {
                    Directory.Move(temporaryBase, finalBase);
                    ReadyToTestCandidateManifest finalManifest = manifest with
                    {
                        StagedRuntimeRoot = Path.Combine(finalBase, "runtime")
                    };
                    WriteAtomic(finalManifestPath, JsonSerializer.Serialize(finalManifest, new JsonSerializerOptions { WriteIndented = true }));
                    return new ReadyToTestCandidateResult(true, null, null, finalManifestPath, finalManifest, null);
                }
                catch (IOException)
                {
                    if (File.Exists(finalManifestPath) && ReadyToTestCandidateVerifier.TryVerify(finalManifestPath, out VerifiedReadyToTestCandidate? raced, out _, out _) &&
                        raced is not null && IsExact(raced.Manifest, identity, sourceRoot, sourceFingerprint, options))
                        return new ReadyToTestCandidateResult(true, null, null, finalManifestPath, raced.Manifest, null);
                    return Failure("FROZEN_CANDIDATE_REHYDRATION_CONFLICT", "The durable candidate identity was concurrently claimed by different content.");
                }
            }
            finally
            {
                if (Directory.Exists(temporaryBase))
                    Directory.Delete(temporaryBase, true);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure("RIMTEST_CANCELLED", "Candidate rehydration was cancelled.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            return Failure("FROZEN_CANDIDATE_REHYDRATION_FAILED", exception.Message);
        }
    }

    private static ReadyToTestCandidateResult Failure(string code, string error) =>
        ReadyToTestCandidateResult.Failure(code, error);

    private static bool IsExact(
        ReadyToTestCandidateManifest manifest,
        ProjectIdentity identity,
        string sourceRoot,
        string sourceFingerprint,
        ReadyToTestCandidateRehydrationOptions options) =>
        string.Equals(manifest.CandidateId, options.CandidateId, StringComparison.Ordinal) &&
        string.Equals(manifest.CanonicalProjectId, identity.CanonicalProjectId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(manifest.SourceRepositoryRoot, sourceRoot, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(manifest.SourceFingerprint, sourceFingerprint, StringComparison.Ordinal) &&
        string.Equals(manifest.PackageSha256, options.PackageSha256, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(manifest.ArtifactSha256, options.ArtifactSha256, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(manifest.SourceCommit, options.SourceCommit, StringComparison.OrdinalIgnoreCase);

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static bool IsCommit(string value) => value.Length == 40 && value.All(Uri.IsHexDigit);
    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string SafeSegment(string value) => string.Concat(value.Select(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_' ? character : '_'));

    private static void WriteAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, content, new UTF8Encoding(false));
        File.Move(temporary, path, true);
    }
}
