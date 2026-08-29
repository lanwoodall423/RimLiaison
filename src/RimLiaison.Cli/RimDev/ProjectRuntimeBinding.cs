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
    public string Health { get; init; } = Succeeded
        ? (RepairOccurred ? ProjectBindingHealthStates.Repaired : ProjectBindingHealthStates.Healthy)
        : ProjectBindingHealthStates.Unknown;

    public bool Repairable { get; init; }

    public string? OriginalRuntimeRoot { get; init; }

    public string? RepairedRuntimeRoot { get; init; }

    public string? TimestampUtc { get; init; }

    public string? WorkflowId { get; init; }

    public WorkspaceIntegrityEntry ToIntegrityEntry() => new(
        ProjectId,
        SourceRoot,
        RuntimeRoot,
        Health,
        Repairable,
        ErrorCode,
        OriginalRuntimeRoot,
        RepairedRuntimeRoot,
        ResolutionMethod,
        WorkspaceEntryStatus,
        TimestampUtc,
        WorkflowId);
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
        health = Health,
        repairable = Repairable,
        originalRuntimeRoot = OriginalRuntimeRoot,
        repairedRuntimeRoot = RepairedRuntimeRoot,
        repairAttempted = RepairAttempted,
        repairOccurred = RepairOccurred,
        timestampUtc = TimestampUtc,
        workflowId = WorkflowId,
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

        ProjectIdentity identity = ProjectIdentityResolver.Resolve(manifest, sourceRoot);
        string projectId = identity.CanonicalProjectId;
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
        if (SamePath(sourceRoot, canonicalModsRoot) || IsWithin(sourceRoot, canonicalModsRoot))
        {
            return Failure(
                projectId,
                sourceRoot,
                "PROJECT_SOURCE_ROOT_IN_MODS",
                "The project source repository cannot be located under the canonical RimWorld Mods directory.",
                rimWorldRoot,
                nextAction: "move the source checkout under the managed Repos root");
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
        string? identityConflict = FindIdentityConflict(workspace, manifest, sourceRoot);
        if (identityConflict is not null)
        {
            return Failure(
                projectId,
                sourceRoot,
                "PROJECT_IDENTITY_CONFLICT",
                identityConflict,
                rimWorldRoot,
                nextAction: "give each production project a unique project and package identity");
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
        bool needsRepair = !HasPersistedRegistration(workspace, sourceRoot);
        bool staleSource = needsRepair && HasStaleRegistration(workspace, sourceRoot, manifest);
        bool staleRuntime = string.Equals(
            candidateResult.ResolutionMethod,
            "stale-runtime-package-id",
            StringComparison.Ordinal);
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
                evidenceId)
            {
                Health = staleSource
                    ? ProjectBindingHealthStates.StaleSourceRootRepairable
                    : staleRuntime
                        ? ProjectBindingHealthStates.StaleRuntimeRootRepairable
                        : needsRepair
                            ? ProjectBindingHealthStates.MissingRegistrationRepairable
                            : ProjectBindingHealthStates.Healthy,
                Repairable = needsRepair || staleRuntime,
                TimestampUtc = DateTimeOffset.UtcNow.ToString("O"),
                OriginalRuntimeRoot = candidateResult.OriginalRuntimeRoot
            };
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
            evidenceId)
        {
            Health = enrollment.Changed
                ? ProjectBindingHealthStates.Repaired
                : ProjectBindingHealthStates.Healthy,
            Repairable = enrollment.Changed,
            TimestampUtc = DateTimeOffset.UtcNow.ToString("O"),
            OriginalRuntimeRoot = candidateResult.OriginalRuntimeRoot,
            RepairedRuntimeRoot = enrollment.Changed ? runtimeRoot : null
        };
    }
    public static WorkspaceIntegrityAuditResult Audit(
        string startDirectory,
        bool repair = true,
        string? workflowId = null)
    {
        RimDevWorkspaceDiscovery workspace = RimDevWorkspaceDiscoverer.Discover(null, startDirectory);
        if (!workspace.Succeeded)
        {
            return new(
                false,
                "BLOCKED",
                [],
                workspace.ErrorCode ?? "PROJECT_WORKSPACE_UNAVAILABLE",
                workspace.Error ?? "The managed workspace configuration could not be resolved.",
                "repair the managed machine-local .rimdev/workspace.json");
        }

        var projects = new List<WorkspaceIntegrityEntry>();
        StackManifestResolution current = StackManifestResolver.Discover(startDirectory);
        if (current.Manifest is null &&
            ClaimsProduction(current.RepositoryRoot) &&
            !workspace.Repositories.Any(repository => SamePath(repository.Path, current.RepositoryRoot)))
        {
            ProjectRuntimeBindingResult failure = Failure(
                Path.GetFileName(current.RepositoryRoot),
                current.RepositoryRoot,
                current.ErrorCode ?? "PROJECT_METADATA_INVALID",
                current.Error ?? "The production project manifest is invalid.");
            projects.Add(failure.ToIntegrityEntry());
        }

        foreach (RimDevRepository repository in workspace.Repositories)
        {
            string sourceRoot = repository.Path;
            try
            {
                sourceRoot = Path.GetFullPath(Path.Combine(workspace.RootPath, repository.Path));
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
            {
            }

            if (!repository.Manifest.IsValid)
            {
                if (!Directory.Exists(sourceRoot))
                {
                    projects.Add(new WorkspaceIntegrityEntry(
                        repository.Name,
                        sourceRoot,
                        null,
                        ProjectBindingHealthStates.Unknown,
                        false,
                        "PROJECT_SOURCE_ROOT_MISSING",
                        null,
                        null,
                        "workspace-audit",
                        "missing",
                        DateTimeOffset.UtcNow.ToString("O")));
                    continue;
                }

                if (!ClaimsProduction(sourceRoot))
                {
                    continue;
                }
                projects.Add(new WorkspaceIntegrityEntry(
                    repository.Name,
                    sourceRoot,
                    null,
                    ProjectBindingHealthStates.Unknown,
                    false,
                    repository.Manifest.ErrorCode ?? "PROJECT_METADATA_INVALID",
                    null,
                    null,
                    "workspace-audit",
                    "unknown"));
                continue;
            }

            if (!string.Equals(repository.Manifest.Workload, "production", StringComparison.Ordinal))
            {
                continue;
            }

            if (!Directory.Exists(sourceRoot))
            {
                projects.Add(new WorkspaceIntegrityEntry(
                    repository.Manifest.DevBridgeProject ?? repository.Manifest.Project!,
                    sourceRoot,
                    null,
                    ProjectBindingHealthStates.Unknown,
                    false,
                    "PROJECT_SOURCE_ROOT_MISSING",
                    null,
                    null,
                    "workspace-audit",
                    "missing"));
                continue;
            }

            StackManifestResolution resolution = StackManifestResolver.Discover(sourceRoot);
            ProjectRuntimeBindingResult binding = resolution.Manifest is null
                ? Failure(
                    repository.Name,
                    sourceRoot,
                    resolution.ErrorCode ?? "PROJECT_METADATA_INVALID",
                    resolution.Error ?? "The production project manifest is invalid.")
                : Resolve(sourceRoot, resolution.Manifest, repair);
            if (!string.IsNullOrWhiteSpace(workflowId))
            {
                binding = binding with { WorkflowId = workflowId };
            }

            projects.Add(binding.ToIntegrityEntry());
        }

        bool blocked = projects.Any(project =>
            project.Health is not (ProjectBindingHealthStates.Healthy or ProjectBindingHealthStates.Repaired));
        return new(true, blocked ? "BLOCKED" : "READY", projects);
    }

    private static string? FindIdentityConflict(
        RimDevWorkspaceDiscovery workspace,
        RimDevStackManifest manifest,
        string sourceRoot)
    {
        ProjectIdentity identity = ProjectIdentityResolver.Resolve(manifest, sourceRoot);
        foreach (RimDevRepository repository in workspace.Repositories)
        {
            if (SamePath(repository.Path, sourceRoot) ||
                !repository.Manifest.IsValid ||
                !string.Equals(repository.Manifest.Workload, "production", StringComparison.Ordinal))
            {
                continue;
            }

            RimDevStackManifest? other = StackManifestResolver.Discover(repository.Path).Manifest;
            if (other is null)
            {
                continue;
            }

            ProjectIdentity otherIdentity = ProjectIdentityResolver.Resolve(other, repository.Path);
            if (identity.PackageId is not null &&
                string.Equals(identity.PackageId, otherIdentity.PackageId, StringComparison.OrdinalIgnoreCase))
            {
                return $"The package identity '{identity.PackageId}' is already claimed by project '{repository.Name}'.";
            }

            if (identity.Aliases.Intersect(otherIdentity.Aliases, StringComparer.OrdinalIgnoreCase).Any())
            {
                return $"The canonical project identity is already claimed by project '{repository.Name}'.";
            }
        }

        return null;
    }

    private static ResolutionCandidateResult FindCandidate(
        RimDevWorkspaceDiscovery workspace,
        RimDevStackManifest manifest,
        string sourceRoot,
        string modsRoot)
    {
        var candidates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var explicitPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var staleExplicitPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

        string[] installedPackagePaths = EnumeratePackageMatches(modsRoot, manifest.PackageId).ToArray();
        foreach (string path in installedPackagePaths)
        {
            candidates.TryAdd(path, "installed-package-id");
        }
        if (explicitPaths.Count(path => !Directory.Exists(path)) == 1 &&
            installedPackagePaths.Length == 1)
        {
            string stalePath = explicitPaths.Single(path => !Directory.Exists(path));
            candidates.Remove(stalePath);
            candidates[installedPackagePaths[0]] = "stale-runtime-package-id";
            staleExplicitPaths.Add(stalePath);
        }

        if (candidates.Count == 0)
        {
            string? projectFolder = SafeChildPath(
                modsRoot,
                ProjectIdentityResolver.Resolve(manifest, sourceRoot).RuntimeFolder);
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
                if (staleExplicitPaths.Contains(path))
                {
                    continue;
                }

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

        string? originalRuntimeRoot = staleExplicitPaths.SingleOrDefault();
        return new(
            true,
            runtimeRoot,
            candidates.Values.Single(),
            WorkspaceStatus(workspace, sourceRoot),
            null,
            null,
            null,
            originalRuntimeRoot);
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
        ProjectIdentity identity = ProjectIdentityResolver.Resolve(manifest, string.Empty);
        return identity.Aliases.Contains(leaf, StringComparer.OrdinalIgnoreCase) ||
            string.Equals(identity.RuntimeFolder, leaf, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IdentityMatches(string key, RimDevStackManifest manifest)
    {
        ProjectIdentity identity = ProjectIdentityResolver.Resolve(manifest, string.Empty);
        return ProjectIdentityResolver.Matches(identity, key);
    }

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

    private static bool HasPersistedRegistration(
        RimDevWorkspaceDiscovery workspace,
        string sourceRoot) =>
        workspace.Configuration?.Repositories?.Any(repository =>
        {
            try
            {
                return SamePath(
                    Path.Combine(workspace.RootPath, repository.Path),
                    sourceRoot);
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
            {
                return false;
            }
        }) == true;
    private static bool HasStaleRegistration(
        RimDevWorkspaceDiscovery workspace,
        string sourceRoot,
        RimDevStackManifest manifest)
    {
        foreach (RimDevWorkspaceRepository repository in workspace.Configuration?.Repositories ?? [])
        {
            try
            {
                string candidate = Path.GetFullPath(Path.Combine(workspace.RootPath, repository.Path));
                string leaf = Path.GetFileName(candidate);
                ProjectIdentity identity = ProjectIdentityResolver.Resolve(manifest, sourceRoot);
                bool identityMatch = identity.Aliases.Contains(leaf, StringComparer.OrdinalIgnoreCase) ||
                    string.Equals(identity.RuntimeFolder, leaf, StringComparison.OrdinalIgnoreCase);
                if (identityMatch && !SamePath(candidate, sourceRoot))
                {
                    return true;
                }
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
            {
            }
        }

        return false;
    }
    private static bool ClaimsProduction(string sourceRoot)
    {
        string manifestPath = Path.Combine(sourceRoot, ".rimdev", "stack.json");
        try
        {
            if (!File.Exists(manifestPath) || new FileInfo(manifestPath).Length > 128 * 1024)
            {
                return false;
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            return document.RootElement.TryGetProperty("workload", out JsonElement workload) &&
                string.Equals(workload.GetString(), "production", StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }
    private static string EvidenceId(string project, string source, string runtime, string rimWorld)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", project, source, runtime, rimWorld)));
        return "rrb-" + Convert.ToHexString(hash[..12]).ToLowerInvariant();
    }
    private static string WorkspaceStatus(RimDevWorkspaceDiscovery workspace, string sourceRoot) =>
        HasPersistedRegistration(workspace, sourceRoot)
            ? "present"
            : "missing";

    private static ResolutionCandidateResult CandidateFailure(
        string code,
        string error,
        string method) => new(
            false,
            null,
            method,
            "unknown",
            code,
            error,
            "repair the machine-local runtime identity");
    private static ProjectRuntimeBindingResult Failure(
        string project,
        string source,
        string code,
        string error,
        string? rimWorld = null,
        string? method = null,
        string workspaceStatus = "unknown",
        bool repairAttempted = false,
        string? nextAction = null)
    {
        (string health, bool repairable) = code switch
        {
            "PROJECT_RIMWORLD_ROOT_MISSING" => (ProjectBindingHealthStates.RimWorldRootMissing, false),
            "PROJECT_RUNTIME_ROOT_CONFLICT" when error.Contains("identity", StringComparison.OrdinalIgnoreCase) =>
                (ProjectBindingHealthStates.ProjectIdentityConflict, false),
            "PROJECT_RUNTIME_ROOT_CONFLICT" => (ProjectBindingHealthStates.RuntimeRootConflict, false),
            "PROJECT_RUNTIME_ROOT_AMBIGUOUS" => (ProjectBindingHealthStates.Ambiguous, false),
            "PROJECT_RUNTIME_ROOT_INVALID" when error.Contains("source repository", StringComparison.OrdinalIgnoreCase) =>
                (ProjectBindingHealthStates.SourceEqualsRuntime, false),
            "PROJECT_SOURCE_ROOT_IN_MODS" => (ProjectBindingHealthStates.SourceUnderMods, false),
            "PROJECT_RUNTIME_ROOT_OUTSIDE_MODS" => (ProjectBindingHealthStates.RuntimeOutsideMods, false),
            "PROJECT_IDENTITY_CONFLICT" => (ProjectBindingHealthStates.ProjectIdentityConflict, false),
            _ when code.StartsWith("PROJECT_METADATA_", StringComparison.Ordinal) =>
                (ProjectBindingHealthStates.ProjectIdentityConflict, false),
            _ => (ProjectBindingHealthStates.Unknown, false)
        };
        return new(
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
            nextAction ?? "repair the project binding and retry once")
        {
            Health = health,
            Repairable = repairable,
            TimestampUtc = DateTimeOffset.UtcNow.ToString("O")
        };
    }

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
        string? NextAction,
        string? OriginalRuntimeRoot = null);

    private sealed record EnrollmentResult(
        bool Succeeded,
        bool Changed,
        string? Error,
        string EntryStatus);
}
