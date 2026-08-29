using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RimLiaison.Stack;

namespace RimLiaison.RimDev;

internal sealed record ProjectRuntimeBindingResult(
    bool Succeeded,
    string ProjectId,
    string SourceRoot,
    string? RuntimeRoot,
    string? RimWorldRoot,
    string? ResolutionMethod,
    string WorkspaceEntryStatus,
    bool RepairAttempted,
    bool RepairOccurred,
    string? EvidenceId,
    string? ErrorCode = null,
    string? Error = null,
    string? NextAction = null)
{
    public object ToEvidence() => new
    {
        schemaVersion = "rimliaison-project-binding/v1",
        status = Succeeded ? "ready" : "blocked",
        projectId = ProjectId,
        sourceRoot = SourceRoot,
        runtimeRoot = RuntimeRoot,
        rimWorldRoot = RimWorldRoot,
        resolutionMethod = ResolutionMethod,
        workspaceEntryStatus = WorkspaceEntryStatus,
        repairAttempted = RepairAttempted,
        repairOccurred = RepairOccurred,
        evidenceId = EvidenceId,
        errorCode = ErrorCode,
        error = Error,
        nextAction = NextAction
    };
}

internal static class ProjectRuntimeBindingResolver
{
    private static readonly object EnrollmentGate = new();
    private const int LockAttempts = 80;
    private const int LockDelayMilliseconds = 25;

    public static ProjectRuntimeBindingResult Resolve(
        string repositoryRoot,
        RimDevStackManifest manifest,
        bool enroll = true)
    {
        string sourceRoot;
        try
        {
            sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return Failure(manifest.Project, repositoryRoot, "PROJECT_SOURCE_ROOT_INVALID", "The project source root is invalid.");
        }

        string projectId = manifest.DevBridgeProject ?? manifest.Project;
        RimDevWorkspaceDiscovery workspace = RimDevWorkspaceDiscoverer.Discover(null, sourceRoot);
        if (!workspace.Succeeded)
        {
            return Failure(
                projectId,
                sourceRoot,
                workspace.ErrorCode ?? "PROJECT_WORKSPACE_UNAVAILABLE",
                workspace.Error ?? "The managed workspace configuration could not be resolved.",
                nextAction: "repair the managed .rimdev/workspace.json");
        }

        if (!string.Equals(manifest.Workload, "production", StringComparison.Ordinal))
        {
            return Failure(projectId, sourceRoot, "PROJECT_METADATA_WORKLOAD_INVALID", "Automatic runtime enrollment is only valid for production projects.");
        }

        string? rimWorldRoot = ResolveFullPath(
            workspace.Configuration?.RimWorldRoot ?? Environment.GetEnvironmentVariable("RIMWORLD_ROOT"),
            workspace.RootPath);
        if (string.IsNullOrWhiteSpace(rimWorldRoot) || !Directory.Exists(rimWorldRoot))
        {
            return Failure(
                projectId,
                sourceRoot,
                "PROJECT_RIMWORLD_ROOT_MISSING",
                "The canonical RimWorld installation root is unknown or does not exist.",
                rimWorldRoot,
                nextAction: "configure rimWorldRoot in the managed machine-local .rimdev/workspace.json");
        }

        string? configuredModsRoot = workspace.Configuration?.ActiveModsRoot ??
            Environment.GetEnvironmentVariable("RIMWORLD_MODS_ROOT");
        string modsRoot = ResolveFullPath(configuredModsRoot, workspace.RootPath) ??
            Path.Combine(rimWorldRoot, "Mods");
        string canonicalModsRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(Path.Combine(rimWorldRoot, "Mods")));
        if (!SamePath(modsRoot, canonicalModsRoot))
        {
            return Failure(
                projectId,
                sourceRoot,
                "PROJECT_RUNTIME_ROOT_OUTSIDE_MODS",
                "The active Mods root is not the canonical RimWorld Mods directory.",
                rimWorldRoot,
                nextAction: "set activeModsRoot to the canonical RimWorld Mods directory");
        }

        string? metadataError = ProjectMetadataValidator.Validate(manifest, sourceRoot);
        if (metadataError is not null)
        {
            return Failure(
                projectId,
                sourceRoot,
                metadataError,
                "The production project metadata is not sufficient for runtime enrollment.",
                rimWorldRoot,
                nextAction: "repair the project-owned .rimdev/stack.json");
        }

        ResolutionCandidateResult candidateResult = FindCandidate(
            workspace,
            manifest,
            sourceRoot,
            canonicalModsRoot);
        if (!candidateResult.Succeeded)
        {
            return Failure(
                projectId,
                sourceRoot,
                candidateResult.ErrorCode!,
                candidateResult.Error!,
                rimWorldRoot,
                candidateResult.ResolutionMethod,
                nextAction: candidateResult.NextAction);
        }

        string runtimeRoot = candidateResult.RuntimeRoot!;
        if (!IsWithin(runtimeRoot, canonicalModsRoot) ||
            SamePath(runtimeRoot, canonicalModsRoot))
        {
            return Failure(
                projectId,
                sourceRoot,
                "PROJECT_RUNTIME_ROOT_OUTSIDE_MODS",
                "The resolved runtime destination is outside the canonical RimWorld Mods boundary.",
                rimWorldRoot,
                candidateResult.ResolutionMethod,
                nextAction: "repair the machine-local runtime mapping");
        }

        if (SamePath(runtimeRoot, sourceRoot) || IsWithin(runtimeRoot, sourceRoot))
        {
            return Failure(
                projectId,
                sourceRoot,
                "PROJECT_RUNTIME_ROOT_INVALID",
                "The runtime destination cannot equal or be inside the source repository.",
                rimWorldRoot,
                candidateResult.ResolutionMethod,
                nextAction: "repair the machine-local runtime mapping");
        }

        string evidenceId = EvidenceId(projectId, sourceRoot, runtimeRoot, rimWorldRoot);
        if (!enroll)
        {
            return new(
                true,
                projectId,
                sourceRoot,
                runtimeRoot,
                rimWorldRoot,
                candidateResult.ResolutionMethod,
                candidateResult.WorkspaceEntryStatus,
                false,
                false,
                evidenceId);
        }

        EnrollmentResult enrollment = Enroll(
            workspace.RootPath,
            sourceRoot,
            manifest,
            Path.GetFileName(runtimeRoot),
            rimWorldRoot,
            canonicalModsRoot);
        if (!enrollment.Succeeded)
        {
            return Failure(
                projectId,
                sourceRoot,
                "PROJECT_WORKSPACE_ENROLLMENT_FAILED",
                enrollment.Error!,
                rimWorldRoot,
                candidateResult.ResolutionMethod,
                enrollment.EntryStatus,
                repairAttempted: true,
                nextAction: "repair the managed machine-local workspace registration");
        }

        return new(
            true,
            projectId,
            sourceRoot,
            runtimeRoot,
            rimWorldRoot,
            candidateResult.ResolutionMethod,
            enrollment.EntryStatus,
            enrollment.Changed,
            enrollment.Changed,
            evidenceId);
    }

    private static ResolutionCandidateResult FindCandidate(
        RimDevWorkspaceDiscovery workspace,
        RimDevStackManifest manifest,
        string sourceRoot,
        string modsRoot)
    {
        string projectId = manifest.DevBridgeProject ?? manifest.Project;
        var candidates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var explicitPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, string> mapping in workspace.Configuration?.PackageMappings ??
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
        {
            if (!IdentityMatches(mapping.Key, manifest))
            {
                continue;
            }

            string? path = SafeChildPath(modsRoot, mapping.Value);
            if (path is null)
            {
                return CandidateFailure(
                    "PROJECT_RUNTIME_ROOT_OUTSIDE_MODS",
                    "A machine-local runtime mapping escapes the canonical Mods boundary.",
                    "mapping");
            }

            explicitPaths.Add(path);
            candidates[path] = "workspace-package-mapping";
        }

        if (!string.IsNullOrWhiteSpace(manifest.RuntimeFolder))
        {
            string? path = SafeChildPath(modsRoot, manifest.RuntimeFolder);
            if (path is null)
            {
                return CandidateFailure(
                    "PROJECT_RUNTIME_ROOT_OUTSIDE_MODS",
                    "The project runtime-folder identity is outside the canonical Mods boundary.",
                    "project-runtime-folder");
            }

            explicitPaths.Add(path);
            candidates[path] = "project-runtime-folder";
        }

        foreach (string path in EnumeratePackageMatches(modsRoot, manifest.PackageId))
        {
            candidates.TryAdd(path, "installed-package-id");
        }

        if (candidates.Count == 0)
        {
            string? projectFolder = SafeChildPath(modsRoot, manifest.Project);
            if (projectFolder is null)
            {
                return CandidateFailure(
                    "PROJECT_RUNTIME_ROOT_AMBIGUOUS",
                    "The project has no safe portable runtime-folder identity.",
                    "none");
            }

            candidates[projectFolder] = "project-identity";
        }

        if (explicitPaths.Count > 0)
        {
            foreach (string path in explicitPaths)
            {
                if (!candidates.ContainsKey(path))
                {
                    return CandidateFailure(
                        "PROJECT_RUNTIME_ROOT_CONFLICT",
                        "The configured runtime mapping does not identify a single destination.",
                        "workspace-package-mapping");
                }

                if (Directory.Exists(path) &&
                    !PackageMatches(path, manifest.PackageId))
                {
                    return CandidateFailure(
                        "PROJECT_RUNTIME_ROOT_CONFLICT",
                        "The configured runtime destination contains another mod identity.",
                        "workspace-package-mapping");
                }
            }
        }

        if (candidates.Count != 1)
        {
            return CandidateFailure(
                "PROJECT_RUNTIME_ROOT_AMBIGUOUS",
                "More than one valid runtime destination matches the project identity.",
                "package-id-scan");
        }

        string runtimeRoot = candidates.Keys.Single();
        string? ownershipConflict = FindOwnershipConflict(workspace, manifest, sourceRoot, runtimeRoot, modsRoot);
        if (ownershipConflict is not null)
        {
            return CandidateFailure(
                "PROJECT_RUNTIME_ROOT_CONFLICT",
                ownershipConflict,
                candidates.Values.Single());
        }

        return new(true, runtimeRoot, candidates.Values.Single(), WorkspaceStatus(workspace, sourceRoot), null, null, null);
    }

    private static string? FindOwnershipConflict(
        RimDevWorkspaceDiscovery workspace,
        RimDevStackManifest manifest,
        string sourceRoot,
        string runtimeRoot,
        string modsRoot)
    {
        foreach (RimDevRepository repository in workspace.Repositories)
        {
            if (SamePath(repository.Path, sourceRoot) || !repository.Manifest.IsValid ||
                !string.Equals(repository.Manifest.Workload, "production", StringComparison.Ordinal))
            {
                continue;
            }

            RimDevStackManifest? otherManifest =
                StackManifestResolver.Discover(repository.Path).Manifest;
            string? claimed = otherManifest is null
                ? null
                : FindConfiguredClaim(otherManifest, workspace.Configuration, modsRoot);
            if (claimed is not null && SamePath(claimed, runtimeRoot))
            {
                return $"The runtime destination is already claimed by project '{repository.Name}'.";
            }
        }

        return null;
    }

    private static string? FindConfiguredClaim(
        RimDevStackManifest manifest,
        RimDevWorkspaceConfiguration? configuration,
        string modsRoot)
    {
        foreach (KeyValuePair<string, string> mapping in configuration?.PackageMappings ??
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
        {
            if (IdentityMatches(mapping.Key, manifest))
            {
                return SafeChildPath(modsRoot, mapping.Value);
            }
        }

        return string.IsNullOrWhiteSpace(manifest.RuntimeFolder)
            ? SafeChildPath(modsRoot, manifest.Project)
            : SafeChildPath(modsRoot, manifest.RuntimeFolder);
    }

    private static IEnumerable<string> EnumeratePackageMatches(string modsRoot, string? packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId) || !Directory.Exists(modsRoot))
        {
            yield break;
        }

        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(modsRoot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            yield break;
        }

        foreach (string directory in directories.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (PackageMatches(directory, packageId))
            {
                yield return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            }
        }
    }

    private static bool PackageMatches(string directory, string? packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return false;
        }

        string aboutPath = Path.Combine(directory, "About", "About.xml");
        if (!File.Exists(aboutPath))
        {
            return false;
        }

        try
        {
            using var reader = System.Xml.XmlReader.Create(aboutPath, new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit });
            while (reader.Read())
            {
                if (reader.NodeType == System.Xml.XmlNodeType.Element &&
                    string.Equals(reader.LocalName, "packageId", StringComparison.Ordinal))
                {
                    return string.Equals(reader.ReadElementContentAsString().Trim(), packageId, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        catch (Exception)
        {
            return false;
        }

        return false;
    }

    private static EnrollmentResult Enroll(
        string workspaceRoot,
        string sourceRoot,
        RimDevStackManifest manifest,
        string runtimeFolder,
        string rimWorldRoot,
        string modsRoot)
    {
        string workspacePath = Path.Combine(workspaceRoot, ".rimdev", "workspace.json");
        string lockPath = workspacePath + ".lock";
        lock (EnrollmentGate)
        {
            FileStream? lockStream = null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(workspacePath)!);
                for (int attempt = 0; attempt < LockAttempts && lockStream is null; attempt++)
                {
                    try
                    {
                        lockStream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    }
                    catch (IOException) when (attempt + 1 < LockAttempts)
                    {
                        Thread.Sleep(LockDelayMilliseconds);
                    }
                }

                if (lockStream is null)
                {
                    return new(false, false, "The machine-local workspace is locked by another agent.", "locked");
                }

                JsonObject document = ReadWorkspaceDocument(workspacePath);
                JsonArray repositories = document["repositories"] as JsonArray ?? [];
                document["repositories"] = repositories;
                string relativeSource = Path.GetRelativePath(workspaceRoot, sourceRoot).Replace('\\', '/');
                if (Path.IsPathRooted(relativeSource) || relativeSource.StartsWith("../", StringComparison.Ordinal))
                {
                    return new(false, false, "The source repository is outside the managed workspace.", "failed");
                }

                bool changed = false;
                JsonObject? currentEntry = repositories
                    .OfType<JsonObject>()
                    .FirstOrDefault(entry => string.Equals(
                        entry["path"]?.GetValue<string>(), relativeSource, StringComparison.OrdinalIgnoreCase));
                if (currentEntry is null)
                {
                    currentEntry = repositories
                        .OfType<JsonObject>()
                        .FirstOrDefault(entry => StaleEntryMatches(
                            entry["path"]?.GetValue<string>(),
                            manifest));
                    if (currentEntry is not null)
                    {
                        currentEntry["path"] = relativeSource;
                        changed = true;
                    }
                }

                if (currentEntry is null)
                {
                    currentEntry = new JsonObject
                    {
                        ["path"] = relativeSource,
                        ["dependsOn"] = new JsonArray(),
                        ["deploymentTarget"] = manifest.DeploymentTarget,
                        ["buildProject"] = manifest.SourceProject,
                        ["configuration"] = manifest.Configuration
                    };
                    foreach (string dependency in manifest.Dependencies ?? [])
                    {
                        ((JsonArray)currentEntry["dependsOn"]!).Add(dependency);
                    }

                    repositories.Add(currentEntry);
                    changed = true;
                }

                JsonObject mappings = document["packageMappings"] as JsonObject ?? [];
                document["packageMappings"] = mappings;
                string mappingKey = manifest.DevBridgeProject ?? manifest.Project;
                if (!string.Equals(mappings[mappingKey]?.GetValue<string>(), runtimeFolder, StringComparison.Ordinal))
                {
                    mappings[mappingKey] = runtimeFolder;
                    changed = true;
                }

                if (string.IsNullOrWhiteSpace(document["rimWorldRoot"]?.GetValue<string>()))
                {
                    document["rimWorldRoot"] = rimWorldRoot;
                    changed = true;
                }

                if (string.IsNullOrWhiteSpace(document["activeModsRoot"]?.GetValue<string>()))
                {
                    document["activeModsRoot"] = modsRoot;
                    changed = true;
                }

                if (changed)
                {
                    WriteWorkspaceDocument(workspacePath, document);
                }

                return new(true, changed, null, changed ? "updated" : "unchanged");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                return new(false, false, "The machine-local workspace registration could not be written atomically.", "failed");
            }
            finally
            {
                lockStream?.Dispose();
                TryDeleteLock(lockPath);
            }
        }
    }

    private static JsonObject ReadWorkspaceDocument(string path)
    {
        if (!File.Exists(path))
        {
            return new JsonObject { ["schemaVersion"] = RimDevSchemas.Workspace, ["repositories"] = new JsonArray() };
        }

        JsonNode? node = JsonNode.Parse(File.ReadAllText(path));
        if (node is not JsonObject document ||
            !string.Equals(document["schemaVersion"]?.GetValue<string>(), RimDevSchemas.Workspace, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("workspace schema is unsupported");
        }

        return document;
    }

    private static void WriteWorkspaceDocument(string path, JsonObject document)
    {
        string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporaryPath, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        try
        {
            if (File.Exists(path))
            {
                File.Replace(temporaryPath, path, null);
            }
            else
            {
                File.Move(temporaryPath, path);
            }
        }
        finally
        {
            TryDeleteLock(temporaryPath);
        }
    }

    private static bool StaleEntryMatches(string? path, RimDevStackManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string leaf = Path.GetFileName(path.Replace('/', Path.DirectorySeparatorChar));
        return new[] { manifest.Project, manifest.DevBridgeProject, manifest.RuntimeFolder }
            .Where(value => value is not null)
            .Select(value => NormalizeIdentity(value!))
            .Contains(NormalizeIdentity(leaf), StringComparer.Ordinal);
    }

    private static bool IdentityMatches(string key, RimDevStackManifest manifest)
    {
        string normalized = NormalizeIdentity(key);
        return new[] { manifest.Project, manifest.DevBridgeProject, manifest.PackageId }
            .Where(value => value is not null)
            .Select(value => NormalizeIdentity(value!))
            .Any(value => string.Equals(value, normalized, StringComparison.Ordinal));
    }

    private static string NormalizeIdentity(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string? SafeChildPath(string root, string? child)
    {
        if (string.IsNullOrWhiteSpace(child) || Path.IsPathRooted(child) || child.Contains(':'))
        {
            return null;
        }

        string[] segments = child.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 1 || segments[0] is "." or "..")
        {
            return null;
        }

        try
        {
            string result = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(root, segments[0])));
            return IsWithin(result, root) && !SamePath(result, root) ? result : null;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool IsWithin(string candidate, string root) =>
        SamePath(candidate, root) ||
        Path.GetFullPath(candidate).StartsWith(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static bool SamePath(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

    private static string? ResolveFullPath(string? value, string? basePath = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            string path = Path.IsPathRooted(value)
                ? value
                : Path.Combine(basePath ?? Environment.CurrentDirectory, value);
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    private static string WorkspaceStatus(RimDevWorkspaceDiscovery workspace, string sourceRoot) =>
        workspace.Repositories.Any(repository => SamePath(repository.Path, sourceRoot))
            ? "present"
            : "missing";

    private static string EvidenceId(string project, string source, string runtime, string rimWorld)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", project, source, runtime, rimWorld)));
        return "rrb-" + Convert.ToHexString(hash[..12]).ToLowerInvariant();
    }

    private static ResolutionCandidateResult CandidateFailure(
        string code,
        string error,
        string method) => new(false, null, method, "unknown", code, error, "repair the machine-local runtime identity");

    private static ProjectRuntimeBindingResult Failure(
        string project,
        string source,
        string code,
        string error,
        string? rimWorld = null,
        string? method = null,
        string workspaceStatus = "unknown",
        bool repairAttempted = false,
        string? nextAction = null) => new(
            false,
            project,
            source,
            null,
            rimWorld,
            method,
            workspaceStatus,
            repairAttempted,
            false,
            null,
            code,
            error,
            nextAction ?? "repair the project binding and retry once");

    private static void TryDeleteLock(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception) when (path.Length > 0)
        {
        }
    }

    private sealed record ResolutionCandidateResult(
        bool Succeeded,
        string? RuntimeRoot,
        string? ResolutionMethod,
        string WorkspaceEntryStatus,
        string? ErrorCode,
        string? Error,
        string? NextAction);

    private sealed record EnrollmentResult(
        bool Succeeded,
        bool Changed,
        string? Error,
        string EntryStatus);
}
