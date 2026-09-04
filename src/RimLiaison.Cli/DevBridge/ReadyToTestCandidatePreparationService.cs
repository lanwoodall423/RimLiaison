using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using RimLiaison.Execution;
using RimLiaison.Git;
using RimLiaison.Stack;
namespace RimLiaison.DevBridge;

public static class ReadyToTestCandidateSchemas
{
    public const string Current = "rimliaison-ready-to-test-candidate/v1";
}

public sealed record ReadyToTestCandidateManifest
{
    [JsonPropertyName("schemaVersion")] public string SchemaVersion { get; init; } = ReadyToTestCandidateSchemas.Current;
    [JsonPropertyName("candidateId")] public required string CandidateId { get; init; }
    [JsonPropertyName("project")] public required string Project { get; init; }
    [JsonPropertyName("canonicalProjectId")] public required string CanonicalProjectId { get; init; }
    [JsonPropertyName("packageId")] public string? PackageId { get; init; }
    [JsonPropertyName("sourceRepositoryRoot")] public required string SourceRepositoryRoot { get; init; }
    [JsonPropertyName("sourceFingerprint")] public required string SourceFingerprint { get; init; }
    [JsonPropertyName("stagedRuntimeRoot")] public required string StagedRuntimeRoot { get; init; }
    [JsonPropertyName("packageSha256")] public required string PackageSha256 { get; init; }
    [JsonPropertyName("requiredRuntimeDependencies")] public IReadOnlyList<string> RequiredRuntimeDependencies { get; init; } = [];
    [JsonPropertyName("buildEvidenceIdentity")] public required string BuildEvidenceIdentity { get; init; }
    [JsonPropertyName("buildStatus")] public required string BuildStatus { get; init; }
    [JsonPropertyName("staticValidationIdentity")] public required string StaticValidationIdentity { get; init; }
    [JsonPropertyName("staticValidationStatus")] public required string StaticValidationStatus { get; init; }
    [JsonPropertyName("createdUtc")] public required string CreatedUtc { get; init; }
    [JsonPropertyName("state")] public string State { get; init; } = "READY_TO_TEST";
}

public sealed record ReadyToTestCandidateResult(
    bool Success,
    string? ErrorCode,
    string? Error,
    string? ManifestPath,
    ReadyToTestCandidateManifest? Manifest,
    DevBridgeBuildDiagnostics? Build)
{
    public static ReadyToTestCandidateResult Failure(string code, string error, DevBridgeBuildDiagnostics? build = null) =>
        new(false, code, error, null, null, build);
}

public sealed record ReadyToTestCandidatePreparationOptions
{
    public string? CandidateStagingRoot { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(20);
    public int MaxStdoutBytes { get; init; } = 1024 * 1024;
    public int MaxStderrBytes { get; init; } = 64 * 1024;

    public string ResolveCandidateStagingRoot() =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(CandidateStagingRoot)
            ? Path.Combine(Path.GetTempPath(), "RimLiaison", "candidates")
            : CandidateStagingRoot!);
}

/// <summary>
/// Builds and stages a testable mod payload without entering the managed runtime boundary.
/// This service deliberately has no lease, coordinator, RimWorld, ModsConfig, deployment, or
/// generation dependency. The next managed-runtime milestone may consume its manifest.
/// </summary>
public sealed class ReadyToTestCandidatePreparationService
{
    private readonly IDevBridgeProcessTransport transport;
    private readonly ReadyToTestCandidatePreparationOptions options;

    public ReadyToTestCandidatePreparationService(
        IDevBridgeProcessTransport transport,
        ReadyToTestCandidatePreparationOptions? options = null)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.options = options ?? new ReadyToTestCandidatePreparationOptions();
    }

    public Task<ReadyToTestCandidateResult> PrepareAsync(
        string project,
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        string root = Path.GetFullPath(repositoryRoot);
        string[] paths = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Where(path => !RepositoryChangeClassificationPolicy.Classify(path).IsGenerated)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!WorktreeFingerprint.TryCompute(root, paths, out string fingerprint, out string? error))
        {
            return Task.FromResult(ReadyToTestCandidateResult.Failure(
                "RIMTEST_SOURCE_FINGERPRINT_UNAVAILABLE",
                error ?? "The candidate source fingerprint could not be established."));
        }
        return PrepareAsync(project, root, fingerprint, cancellationToken);
    }

    public async Task<ReadyToTestCandidateResult> PrepareAsync(
        string project,
        string repositoryRoot,
        string sourceFingerprint,
        CancellationToken cancellationToken = default)
    {
        string transactionId = HashText(project + "\0" + sourceFingerprint)[..32];
        if (string.IsNullOrWhiteSpace(project) ||
            string.IsNullOrWhiteSpace(repositoryRoot) ||
            string.IsNullOrWhiteSpace(sourceFingerprint))
        {
            return ReadyToTestCandidateResult.Failure(
                "RIMTEST_DEVBRIDGE_PROJECT_MISSING",
                "Candidate preparation requires project, repository root, and source fingerprint.");
        }

        string sourceRoot;
        try
        {
            sourceRoot = Path.GetFullPath(repositoryRoot);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return ReadyToTestCandidateResult.Failure(
                "RIMTEST_REPOSITORY_ROOT_INVALID", "The repository root is invalid.");
        }

        ProjectOwnedDescriptorMaterialization? materialization = null;
        string candidateBase = string.Empty;
        try
        {
            // The temporary contract is still derived from the owner repository. Its runtime
            // argument is only a candidate path; it is never a canonical deployment destination.
            string candidateRoot = options.ResolveCandidateStagingRoot();
            materialization = ProjectOwnedDescriptorMaterializer.Materialize(
                project,
                sourceRoot,
                Path.Combine(candidateRoot, "pending", transactionId, "runtime"),
                out string? metadataCode,
                out string? metadataError);
            if (materialization is null)
            {
                return ReadyToTestCandidateResult.Failure(
                    metadataCode ?? "PROJECT_METADATA_MISSING",
                    metadataError ?? "The project-owned project metadata could not be resolved.");
            }

            DevBridgeDevelopmentDescriptor descriptor = materialization.Descriptor;
            if (string.Equals(descriptor.DeploymentRole, "tooling-only", StringComparison.OrdinalIgnoreCase))
            {
                return ReadyToTestCandidateResult.Failure(
                    "DEVBRIDGE_TOOLING_ONLY_NOT_DEPLOYABLE",
                    "Tooling-only projects are not eligible for a testable mod candidate.");
            }

            string safeProject = SafeSegment(descriptor.CanonicalProjectId ?? descriptor.Project);
            string candidateId = transactionId;
            candidateBase = Path.Combine(candidateRoot, safeProject, candidateId);
            string buildRoot = Path.Combine(candidateBase, "build");
            string runtimeRoot = Path.Combine(candidateBase, "runtime");
            string intermediateRoot = Path.Combine(candidateBase, "obj");
            Directory.CreateDirectory(buildRoot);
            Directory.CreateDirectory(intermediateRoot);
            if (Directory.Exists(runtimeRoot))
                Directory.Delete(runtimeRoot, recursive: true);
            Directory.CreateDirectory(runtimeRoot);

            string buildProject = SafeCombine(sourceRoot, descriptor.SourceProject);
            string buildWorkingDirectory = Path.GetDirectoryName(buildProject) ?? sourceRoot;
            string[] buildArguments =
            [
                "build", buildProject,
                "--configuration", descriptor.Configuration,
                "--output", buildRoot,
                "--nologo",
                "-p:BaseIntermediateOutputPath=" + Path.Combine(intermediateRoot, "base") + Path.DirectorySeparatorChar,
                "-p:IntermediateOutputPath=" + Path.Combine(intermediateRoot, "intermediate") + Path.DirectorySeparatorChar,
                "-p:MSBuildProjectExtensionsPath=" + Path.Combine(intermediateRoot, "extensions") + Path.DirectorySeparatorChar,
                "-p:OutputPath=" + buildRoot + Path.DirectorySeparatorChar
            ];
            DevBridgeProcessResult build = await transport.ExecuteAsync(
                new DevBridgeProcessRequest(
                    "dotnet",
                    buildWorkingDirectory,
                    buildArguments,
                    options.Timeout,
                    options.MaxStdoutBytes,
                    options.MaxStderrBytes,
                    EnvironmentVariables: null,
                    OperationKey: "candidate-build:" + descriptor.CanonicalProjectId),
                cancellationToken).ConfigureAwait(false);
            DevBridgeBuildDiagnostics diagnostics = BuildDiagnostics(
                build,
                descriptor.Project,
                sourceRoot,
                buildProject,
                buildRoot,
                candidateId,
                sourceFingerprint,
                descriptor.Configuration,
                build.ExitCode is 0 && build.StartError is null && !build.TimedOut && !build.Cancelled
                    ? null
                    : "READY_TO_TEST candidate build failed");
            if (build.Cancelled)
                return ReadyToTestCandidateResult.Failure("RIMTEST_CANCELLED", "Candidate preparation was cancelled.", diagnostics);
            if (build.TimedOut)
                return ReadyToTestCandidateResult.Failure("DEVELOPMENT_BUILD_TIMEOUT", "The candidate build exceeded its bounded timeout.", diagnostics);
            if (build.ExitCode is not 0 || build.StartError is not null)
                return ReadyToTestCandidateResult.Failure("DEVELOPMENT_BUILD_FAILED", "The declared project build failed.", diagnostics);

            string expectedArtifact = SafeCombine(buildRoot, descriptor.ExpectedAssembly);
            if (!File.Exists(expectedArtifact))
                return ReadyToTestCandidateResult.Failure("DEVELOPMENT_ARTIFACT_MISSING", "The build did not produce the expected assembly.", diagnostics);

            List<PackageEntry> package = BuildPackage(
                sourceRoot,
                runtimeRoot,
                expectedArtifact,
                descriptor.RuntimePackage,
                descriptor.DeploymentTarget);
            string packageHash = ComputePackageHash(package);
            string candidateIdWithPackage = HashText(
                descriptor.CanonicalProjectId + "\0" + sourceRoot + "\0" + sourceFingerprint + "\0" + packageHash)[..32];
            if (!string.Equals(candidateId, candidateIdWithPackage, StringComparison.Ordinal))
            {
                string finalBase = Path.Combine(candidateRoot, safeProject, candidateIdWithPackage);
                if (!string.Equals(finalBase, candidateBase, StringComparison.OrdinalIgnoreCase))
                {
                    if (Directory.Exists(finalBase)) Directory.Delete(finalBase, recursive: true);
                    Directory.Move(candidateBase, finalBase);
                    candidateBase = finalBase;
                    runtimeRoot = Path.Combine(candidateBase, "runtime");
                }
                candidateId = candidateIdWithPackage;
            }

            string staticIdentity = "runtime-package:" + packageHash;
            ReadyToTestCandidateManifest manifest = new()
            {
                CandidateId = candidateId,
                Project = descriptor.Project,
                CanonicalProjectId = descriptor.CanonicalProjectId ?? descriptor.Project,
                PackageId = ReadPackageId(sourceRoot),
                SourceRepositoryRoot = sourceRoot,
                SourceFingerprint = sourceFingerprint,
                StagedRuntimeRoot = runtimeRoot,
                PackageSha256 = packageHash,
                RequiredRuntimeDependencies = ReadDependencies(sourceRoot),
                BuildEvidenceIdentity = "candidate-build:" + candidateId,
                BuildStatus = "PASS",
                StaticValidationIdentity = staticIdentity,
                StaticValidationStatus = "PASS",
                CreatedUtc = DateTimeOffset.UtcNow.ToString("O")
            };
            string manifestPath = Path.Combine(candidateBase, "candidate.json");
            WriteAtomic(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            return new ReadyToTestCandidateResult(true, null, null, manifestPath, manifest, diagnostics);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ReadyToTestCandidateResult.Failure("RIMTEST_CANCELLED", "Candidate preparation was cancelled.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            return ReadyToTestCandidateResult.Failure("RIMLIAISON_CANDIDATE_PREPARATION_FAILED", Bound(exception.Message));
        }
        finally
        {
            if (materialization is not null)
                ProjectOwnedDescriptorMaterializer.Delete(materialization);
        }
    }

    private static List<PackageEntry> BuildPackage(
        string sourceRoot,
        string runtimeRoot,
        string expectedArtifact,
        JsonElement? runtimePackage,
        string assemblyTarget)
    {
        List<PackageEntry> entries = [];
        if (runtimePackage is not JsonElement package || package.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The project runtime package metadata is missing.");
        string packageSource = GetString(package, "sourceRoot") ?? ".";
        string packageRoot = Path.GetFullPath(Path.Combine(sourceRoot, packageSource == "." ? string.Empty : SafeRelative(packageSource)));
        string[] includes = GetStringArray(package, "include");
        string[] excludes = GetStringArray(package, "exclude");
        if (includes.Length == 0)
            throw new InvalidDataException("runtimePackage.include must contain at least one pattern.");

        // These are always source/build/tooling state, even if a project accidentally includes a
        // broad glob. Candidate payloads must be runtime-only and cannot copy themselves.
        string[] mandatoryExcludes =
        [
            ".git/**", "**/.git/**", "Source/**", "**/Source/**", "src/**", "**/src/**",
            "bin/**", "**/bin/**", "obj/**", "**/obj/**",
            "tests/**", "Tests/**", "**/tests/**", "**/Tests/**",
            "TestResults/**", "test-results/**", "**/TestResults/**", "**/test-results/**",
            "DevCheckRuntime/**", "**/DevCheckRuntime/**",
            ".rimdev/**", "**/.rimdev/**", ".rimctx/**", "**/.rimctx/**",
            ".rimerror/**", "**/.rimerror/**", "**/*.user", "**/*.suo"
        ];
        excludes = mandatoryExcludes.Concat(excludes).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (string file in Directory.EnumerateFiles(packageRoot, "*", SearchOption.AllDirectories))
        {
            string relative = NormalizeRelative(Path.GetRelativePath(packageRoot, file));
            if (!includes.Any(pattern => Wildcard(pattern).IsMatch(relative)) ||
                excludes.Any(pattern => Wildcard(pattern).IsMatch(relative)))
                continue;
            string staged = SafeCombine(runtimeRoot, relative);
            CopyAtomic(file, staged);
            entries.Add(new PackageEntry(file, relative, HashFile(staged)!, new FileInfo(staged).Length));
        }

        string target = NormalizeRelative(assemblyTarget);
        entries.RemoveAll(entry => string.Equals(entry.TargetPath, target, StringComparison.OrdinalIgnoreCase));
        string stagedAssembly = SafeCombine(runtimeRoot, target);
        CopyAtomic(expectedArtifact, stagedAssembly);
        entries.Add(new PackageEntry(expectedArtifact, target, HashFile(stagedAssembly)!, new FileInfo(stagedAssembly).Length));
        return entries.OrderBy(entry => entry.TargetPath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? ReadPackageId(string sourceRoot)
    {
        string about = Path.Combine(sourceRoot, "About", "About.xml");
        if (!File.Exists(about)) return null;
        string text = File.ReadAllText(about);
        Match match = Regex.Match(text, "<packageId>\\s*([^<]+?)\\s*</packageId>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static IReadOnlyList<string> ReadDependencies(string sourceRoot)
    {
        StackManifestResolution resolution = StackManifestResolver.Discover(sourceRoot);
        return resolution.Manifest?.Dependencies ?? [];
    }

    private static Regex Wildcard(string pattern) => new(
        "^" + Regex.Escape(pattern.Replace('\\', '/')).Replace(@"\*\*", ".*").Replace(@"\*", "[^/]*").Replace(@"\?", "[^/]") + "$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static DevBridgeBuildDiagnostics BuildDiagnostics(
        DevBridgeProcessResult process,
        string project,
        string sourceRoot,
        string sourceProject,
        string staging,
        string candidateId,
        string sourceFingerprint,
        string configuration,
        string? failureMessage)
    {
        string buildTarget = NormalizeRelative(Path.GetRelativePath(sourceRoot, sourceProject));
        return new(
            "dotnet build",
            process.ExitCode,
            Bound(process.Stdout),
            sourceProject,
            staging,
            process.TimedOut,
            null,
            DiagnosticOutput: Bound(process.Stderr),
            ErrorOutput: Bound(process.Stderr),
            Configuration: configuration,
            Cancelled: process.Cancelled,
            WorkingDirectory: Path.GetDirectoryName(sourceProject),
            SourceFingerprint: sourceFingerprint,
            FailureMessage: failureMessage,
            TransactionId: candidateId,
            ErrorCode: failureMessage is null ? null : "DEVELOPMENT_BUILD_FAILED",
            OutputTruncated: process.StdoutTruncated,
            CausalDiagnostic: Bound(process.Stderr),
            CausalDiagnosticTruncated: process.StderrTruncated,
            Orchestrator: "RimLiaison",
            FailureSurface: "project-build",
            LikelyOwner: project,
            OwnershipConfidence: "explicit",
            OwnershipBasis: "project-owned descriptor source project",
            BuildOwnerType: "PROJECT_BUILD",
            BuildOwnerProject: project,
            BuildTarget: buildTarget,
            BuildSourceRoot: sourceRoot,
            BuildCommandIdentity: "dotnet build",
            BuildEvidenceId: "candidate-build:" + candidateId,
            BuildDurationMilliseconds: process.Evidence?.DurationMilliseconds);
    }

    private static string SafeSegment(string value) =>
        Regex.Replace(string.IsNullOrWhiteSpace(value) ? "candidate" : value, "[^A-Za-z0-9._-]", "_");

    private static string SafeCombine(string root, string relative) =>
        IsSafeRelative(relative)
            ? Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)))
            : throw new InvalidDataException("An unsafe candidate package path was supplied.");

    private static string SafeRelative(string value) =>
        IsSafeRelative(value) ? value : throw new InvalidDataException("An unsafe runtime package source was supplied.");

    private static bool IsSafeRelative(string value) =>
        !Path.IsPathRooted(value) && !value.Contains(':') &&
        !value.Replace('\\', '/').Split('/').Contains("..");

    private static string NormalizeRelative(string value) => value.Replace('\\', '/').TrimStart('/');
    private static string? HashFile(string path) => File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant() : null;
    private static string HashText(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string ComputePackageHash(IEnumerable<PackageEntry> entries) => HashText(string.Join('\n', entries.OrderBy(entry => entry.TargetPath, StringComparer.OrdinalIgnoreCase).Select(entry => NormalizeRelative(entry.TargetPath) + '\0' + entry.Sha256 + '\0' + entry.Size)));
    private static string? GetString(JsonElement root, string name) => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string[] GetStringArray(JsonElement root, string name) => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray() : [];
    private static string Bound(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Length <= 4096 ? value : value[..4096];

    private static void CopyAtomic(string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string temporary = target + ".tmp-" + Guid.NewGuid().ToString("N");
        File.Copy(source, temporary, true);
        File.Move(temporary, target, true);
    }

    private static void WriteAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, content, new UTF8Encoding(false));
        File.Move(temporary, path, true);
    }

    private sealed record PackageEntry(string SourcePath, string TargetPath, string Sha256, long Size);
}
