using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RimLiaison.DevBridge;

public sealed record ReadyToTestCandidatePackageEntry(
    string SourcePath,
    string TargetPath,
    string Sha256,
    long Size);

public sealed record VerifiedReadyToTestCandidate(
    string ManifestPath,
    ReadyToTestCandidateManifest Manifest,
    IReadOnlyList<ReadyToTestCandidatePackageEntry> Package);

public static class ReadyToTestCandidateVerifier
{
    private static readonly string[] ForbiddenRoots =
    [
        ".git", "source", "src", "bin", "obj", "tests", "testresults", "test-results",
        "devcheckruntime", ".rimdev", ".rimctx", ".rimerror"
    ];

    public static bool TryVerify(
        string manifestPath,
        out VerifiedReadyToTestCandidate? verified,
        out string errorCode,
        out string error)
    {
        verified = null;
        errorCode = "RIMTEST_CANDIDATE_INVALID";
        error = "The READY_TO_TEST candidate manifest is invalid.";
        try
        {
            string fullManifest = Path.GetFullPath(manifestPath);
            if (!File.Exists(fullManifest) ||
                !string.Equals(Path.GetFileName(fullManifest), "candidate.json", StringComparison.OrdinalIgnoreCase))
            {
                errorCode = "RIMTEST_CANDIDATE_MANIFEST_MISSING";
                error = "The candidate manifest path must identify an existing candidate.json.";
                return false;
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(fullManifest));
            if (!document.RootElement.TryGetProperty("schemaVersion", out JsonElement schema) ||
                schema.ValueKind != JsonValueKind.String ||
                !string.Equals(schema.GetString(), ReadyToTestCandidateSchemas.Current, StringComparison.Ordinal))
            {
                errorCode = "RIMTEST_CANDIDATE_SCHEMA_UNSUPPORTED";
                error = "Only rimliaison-ready-to-test-candidate/v1 is supported.";
                return false;
            }

            ReadyToTestCandidateManifest? manifest = JsonSerializer.Deserialize<ReadyToTestCandidateManifest>(
                document.RootElement.GetRawText());
            bool rehydrated = string.Equals(
                manifest?.Provenance,
                "rehydrated-frozen-candidate",
                StringComparison.Ordinal);
            if (manifest is null ||
                string.IsNullOrWhiteSpace(manifest.CandidateId) ||
                string.IsNullOrWhiteSpace(manifest.Project) ||
                string.IsNullOrWhiteSpace(manifest.CanonicalProjectId) ||
                string.IsNullOrWhiteSpace(manifest.SourceRepositoryRoot) ||
                string.IsNullOrWhiteSpace(manifest.SourceFingerprint) ||
                string.IsNullOrWhiteSpace(manifest.StagedRuntimeRoot) ||
                string.IsNullOrWhiteSpace(manifest.PackageSha256) ||
                string.IsNullOrWhiteSpace(manifest.BuildEvidenceIdentity) ||
                string.IsNullOrWhiteSpace(manifest.StaticValidationIdentity) ||
                !string.Equals(manifest.State, "READY_TO_TEST", StringComparison.Ordinal) ||
                (!rehydrated && !string.Equals(manifest.BuildStatus, "PASS", StringComparison.Ordinal)) ||
                (rehydrated && !string.Equals(manifest.BuildStatus, "NOT_PERFORMED", StringComparison.Ordinal)) ||
                !string.Equals(manifest.StaticValidationStatus, "PASS", StringComparison.Ordinal))
            {
                error = "The candidate manifest is missing required READY_TO_TEST identity or status fields.";
                return false;
            }
            if (rehydrated &&
                (manifest.BuildPerformed != false ||
                 manifest.ArtifactIdentityVerified != true ||
                 !Regex.IsMatch(manifest.ArtifactSha256 ?? string.Empty, "^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant) ||
                 !Regex.IsMatch(manifest.SourceCommit ?? string.Empty, "^[0-9a-fA-F]{40}$", RegexOptions.CultureInvariant) ||
                 string.IsNullOrWhiteSpace(manifest.RehydrationEvidenceId) ||
                 string.IsNullOrWhiteSpace(manifest.RehydrationProof) ||
                 !string.Equals(
                     manifest.RehydrationProof,
                     ComputeRehydrationProof(
                         manifest.CandidateId,
                         manifest.PackageSha256,
                         manifest.ArtifactSha256!,
                         manifest.SourceCommit!,
                         manifest.SourceFingerprint),
                     StringComparison.OrdinalIgnoreCase)))
            {
                errorCode = "FROZEN_CANDIDATE_REHYDRATION_INVALID";
                error = "The rehydrated frozen candidate provenance proof is invalid.";
                return false;
            }

            if (!Regex.IsMatch(manifest.CandidateId, "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant) ||
                !Regex.IsMatch(manifest.PackageSha256, "^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant))
            {
                error = "The candidate identity or package hash is malformed.";
                return false;
            }

            string manifestDirectory = Path.GetDirectoryName(fullManifest)!;
            string runtimeRoot = Path.GetFullPath(manifest.StagedRuntimeRoot);
            string expectedRuntimeRoot = Path.Combine(manifestDirectory, "runtime");
            if (!string.Equals(runtimeRoot, expectedRuntimeRoot, StringComparison.OrdinalIgnoreCase) ||
                !Directory.Exists(runtimeRoot) ||
                !Directory.Exists(Path.GetFullPath(manifest.SourceRepositoryRoot)))
            {
                errorCode = "RIMTEST_CANDIDATE_PATH_INVALID";
                error = "Candidate source and staged runtime paths are unavailable or outside the candidate layout.";
                return false;
            }

            List<ReadyToTestCandidatePackageEntry> package = [];
            foreach (string file in Directory.EnumerateFiles(runtimeRoot, "*", SearchOption.AllDirectories))
            {
                string relative = NormalizeRelative(Path.GetRelativePath(runtimeRoot, file));
                if (!IsSafeRelative(relative) || IsForbidden(relative))
                {
                    errorCode = "RIMTEST_CANDIDATE_FORBIDDEN_PAYLOAD";
                    error = "The staged candidate contains forbidden source, build, test, or tooling content.";
                    return false;
                }
                FileInfo info = new(file);
                package.Add(new ReadyToTestCandidatePackageEntry(
                    file,
                    relative,
                    HashFile(file),
                    info.Length));
            }

            if (package.Count == 0 || !package.Any(entry => entry.TargetPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
            {
                errorCode = "RIMTEST_CANDIDATE_PAYLOAD_EMPTY";
                error = "The staged candidate does not contain a runtime assembly.";
                return false;
            }

            string computedHash = ComputePackageHash(package);
            if (!string.Equals(computedHash, manifest.PackageSha256, StringComparison.OrdinalIgnoreCase))
            {
                errorCode = "RIMTEST_CANDIDATE_PACKAGE_MUTATED";
                error = "The staged candidate package hash does not match candidate.json.";
                return false;
            }
            if (rehydrated &&
                !package.Any(entry => string.Equals(
                    entry.Sha256,
                    manifest.ArtifactSha256,
                    StringComparison.OrdinalIgnoreCase)))
            {
                errorCode = "FROZEN_CANDIDATE_ARTIFACT_MISSING";
                error = "The rehydrated candidate does not contain the verified frozen artifact.";
                return false;
            }

            verified = new VerifiedReadyToTestCandidate(fullManifest, manifest, package);
            return true;
        }
        catch (JsonException exception)
        {
            error = Bound(exception.Message);
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = Bound(exception.Message);
            return false;
        }
    }
    public static string ComputePackageHash(IEnumerable<ReadyToTestCandidatePackageEntry> package) =>
        HashText(string.Join('\n', package
            .OrderBy(entry => entry.TargetPath, StringComparer.OrdinalIgnoreCase)
            .Select(entry => NormalizeRelative(entry.TargetPath) + '\0' + entry.Sha256 + '\0' + entry.Size)));

    public static string ComputeRehydrationProof(
        string candidateId,
        string packageSha256,
        string artifactSha256,
        string sourceCommit,
        string sourceFingerprint) =>
        HashText(string.Join('\0', candidateId, packageSha256, artifactSha256, sourceCommit, sourceFingerprint));

    private static bool IsForbidden(string relative)
    {
        string[] parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(part => ForbiddenRoots.Contains(part, StringComparer.OrdinalIgnoreCase));
    }

    private static bool IsSafeRelative(string value) =>
        !Path.IsPathRooted(value) && !value.Contains(':') &&
        !value.Split('/').Contains("..", StringComparer.Ordinal);

    private static string NormalizeRelative(string value) => value.Replace('\\', '/').TrimStart('/');
    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string HashText(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Bound(string value) => value.Length <= 4096 ? value : value[..4096];
}
