using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using RimLiaison.Recovery;

namespace RimLiaison.DevBridge;

/// <summary>
/// The descriptor is an input to DevBridge2, but its project identity comes
/// from the repository and the catalog.  This reconciler repairs only the
/// narrow, missing/stale prerequisite that RimLiaison can prove safely.
/// DevBridge2 remains the authoritative validator and transaction owner.
/// </summary>
public sealed record DevBridgeDevelopmentDescriptor(
    string SchemaVersion,
    string Project,
    string SourceProject,
    string Configuration,
    string ExpectedAssembly,
    string DeploymentTarget,
    string TestRecipe,
    JsonElement? RuntimePackage = null,
    string? DeploymentRole = null,
    string? CanonicalProjectId = null,
    string? MetadataOwner = null,
    string? MetadataSource = null,
    string? ContractProducer = null,
    string? MaterializedContractPath = null);

public sealed record DevBridgeDescriptorReconciliationResult(
    PrerequisiteRecoveryState State,
    string DescriptorPath,
    DevBridgeDevelopmentDescriptor? Descriptor,
    string? ErrorCode = null,
    string? Error = null,
    bool Changed = false,
    string? BackupPath = null,
    int Attempts = 0,
    string? Action = null)
{
    public bool CanProceed => Descriptor is not null &&
        State is PrerequisiteRecoveryState.Ready or
            PrerequisiteRecoveryState.Recovered;
}

public static class DevBridgeDevelopmentDescriptorReconciler
{
    private const int MaximumDescriptorBytes = 128 * 1024;
    private const int MaximumProjectCandidates = 256;
    private static readonly Regex Token = new(
        "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static DevBridgeDescriptorReconciliationResult Reconcile(
        string project,
        string repositoryRoot,
        string descriptorPath,
        DevBridgeModDevelopmentAdapterOptions options)
    {
        if (string.IsNullOrWhiteSpace(project) ||
            string.IsNullOrWhiteSpace(repositoryRoot) ||
            string.IsNullOrWhiteSpace(descriptorPath))
        {
            return Failure(
                descriptorPath,
                PrerequisiteRecoveryState.RecoveryRequired,
                "DEVBRIDGE_DESCRIPTOR_INPUT_INVALID",
                "Descriptor reconciliation requires a project, repository root, and descriptor path.");
        }

        string fullDescriptorPath;
        string fullRepositoryRoot;
        try
        {
            fullDescriptorPath = Path.GetFullPath(descriptorPath);
            fullRepositoryRoot = Path.GetFullPath(repositoryRoot);
        }
        catch (Exception exception) when (exception is ArgumentException or
            IOException or NotSupportedException)
        {
            return Failure(
                descriptorPath,
                PrerequisiteRecoveryState.RecoveryRequired,
                "DEVBRIDGE_DESCRIPTOR_INPUT_INVALID",
                Bound(exception.Message));
        }

        bool descriptorExists = File.Exists(fullDescriptorPath);
        if (descriptorExists &&
            Directory.Exists(Path.Combine(fullRepositoryRoot, ".git")) &&
            IsWithin(fullDescriptorPath, Path.Combine(fullRepositoryRoot, "DevelopmentProjects")) &&
            !IsExplicitNonProductionDescriptor(fullDescriptorPath))
        {
            return Failure(
                fullDescriptorPath,
                PrerequisiteRecoveryState.RecoveryRequired,
                "EXTERNAL_PRODUCTION_DESCRIPTOR_IN_TOOLING",
                "Descriptors stored in a tooling repository must be explicitly classified as non-production fixtures.",
                action: "move-project-metadata-to-owning-repository");
        }

        bool repositoryExists = Directory.Exists(fullRepositoryRoot);
        bool coordinatorExists = Directory.Exists(options.RootPath);
        if (!descriptorExists && !repositoryExists && !coordinatorExists)
        {
            // Adapters used by contract tests and older callers may provide
            // symbolic roots.  There is no safe filesystem fact from which
            // to derive a descriptor in that mode, so preserve the existing
            // pass-through behavior and let DevBridge2 validate the request.
            return new DevBridgeDescriptorReconciliationResult(
                PrerequisiteRecoveryState.Ready,
                fullDescriptorPath,
                null,
                Action: "descriptor-recovery-not-attempted");
        }

        string? readError = null;
        if (descriptorExists &&
            TryReadDescriptor(
                fullDescriptorPath,
                project,
                fullRepositoryRoot,
                options.RootPath,
                out DevBridgeDevelopmentDescriptor? current,
                out DescriptorFields? currentFields,
                out bool sourceIsStale,
                out readError))
        {
            if (!sourceIsStale && current is not null)
            {
                return new DevBridgeDescriptorReconciliationResult(
                    PrerequisiteRecoveryState.Ready,
                    fullDescriptorPath,
                    current,
                    Attempts: 0,
                    Action: "descriptor-valid");
            }
        }
        else
        {
            currentFields = ReadPartialFields(fullDescriptorPath);
            readError ??= "The existing descriptor is malformed or incomplete.";
        }

        if (!IsWithin(fullDescriptorPath, options.RootPath))
        {
            return Failure(
                fullDescriptorPath,
                PrerequisiteRecoveryState.RecoveryRequired,
                "DEVBRIDGE_DESCRIPTOR_PATH_UNSAFE",
                "RimLiaison will not reconstruct a descriptor outside the configured DevBridge root.");
        }

        DescriptorCandidate? candidate;
        try
        {
            candidate = SelectProjectCandidate(
                project,
                fullRepositoryRoot,
                options.RootPath,
                options.ChangedPaths);
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or NotSupportedException)
        {
            return Failure(
                fullDescriptorPath,
                PrerequisiteRecoveryState.RecoveryFailed,
                "DEVBRIDGE_DESCRIPTOR_SOURCE_DISCOVERY_FAILED",
                Bound(exception.Message),
                action: "inspect-project-metadata");
        }

        if (candidate is null)
        {
            return Failure(
                fullDescriptorPath,
                PrerequisiteRecoveryState.RecoveryRequired,
                "DEVBRIDGE_DESCRIPTOR_SOURCE_AMBIGUOUS",
                "No single canonical project file matched the affected project and changed paths.",
                action: "identify-project-source");
        }

        string configuration = SelectConfiguration(options.Configuration, currentFields?.Configuration);
        string? testRecipe = SelectToken(options.TestRecipe, currentFields?.TestRecipe);
        if (testRecipe is null)
        {
            return Failure(
                fullDescriptorPath,
                PrerequisiteRecoveryState.RecoveryRequired,
                "DEVBRIDGE_DESCRIPTOR_RECIPE_UNAVAILABLE",
                "The affected catalog did not provide a development recipe and the existing descriptor had no valid recipe.",
                action: "identify-development-recipe");
        }

        string expectedAssembly = candidate.AssemblyName + ".dll";
        string? deploymentTarget = SelectDeploymentTarget(
            options,
            currentFields?.DeploymentTarget,
            fullRepositoryRoot,
            expectedAssembly);
        if (deploymentTarget is null)
        {
            return Failure(
                fullDescriptorPath,
                PrerequisiteRecoveryState.RecoveryRequired,
                "DEVBRIDGE_DESCRIPTOR_DEPLOYMENT_TARGET_UNAVAILABLE",
                "No unambiguous deployment target was found in the repository metadata or existing descriptor.",
                action: "identify-deployment-target");
        }

        var descriptor = new DevBridgeDevelopmentDescriptor(
            DevBridgeModDevelopmentSchemas.Current,
            project,
            candidate.SourceProject,
            configuration,
            expectedAssembly,
            deploymentTarget,
            testRecipe,
            currentFields?.RuntimePackage,
            currentFields?.DeploymentRole,
            currentFields?.CanonicalProjectId,
            currentFields?.MetadataOwner,
            currentFields?.MetadataSource,
            currentFields?.ContractProducer,
            currentFields?.MaterializedContractPath);
        if (!ValidateDescriptor(
                descriptor,
                project,
                fullRepositoryRoot,
                options.RootPath,
                options.DeploymentRoot,
                out string? validationError))
        {
            return Failure(
                fullDescriptorPath,
                PrerequisiteRecoveryState.RecoveryRequired,
                "DEVBRIDGE_DESCRIPTOR_RECONSTRUCTION_INVALID",
                validationError,
                action: "inspect-project-metadata");
        }

        string? backupPath = null;
        try
        {
            string? parent = Directory.GetParent(fullDescriptorPath)?.FullName;
            if (string.IsNullOrWhiteSpace(parent) ||
                !IsWithin(parent, options.RootPath))
            {
                return Failure(
                    fullDescriptorPath,
                    PrerequisiteRecoveryState.RecoveryRequired,
                    "DEVBRIDGE_DESCRIPTOR_PATH_UNSAFE",
                    "The descriptor directory is outside the configured DevBridge root.");
            }

            Directory.CreateDirectory(parent);
            if (descriptorExists && options.PreserveDescriptorBackup)
            {
                // Recovery copies are generated owner state, not sibling
                // project configuration. DevBridge2 already owns and ignores
                // its artifacts directory, which keeps recovery safe without
                // making DevelopmentProjects appear meaningfully dirty.
                string backupDirectory = Path.Combine(
                    options.RootPath,
                    "artifacts",
                    "descriptor-recovery");
                Directory.CreateDirectory(backupDirectory);
                backupPath = Path.Combine(
                    backupDirectory,
                    Path.GetFileName(fullDescriptorPath) +
                    ".recovery-backup-" + Guid.NewGuid().ToString("N") + ".json");
                File.Copy(fullDescriptorPath, backupPath, overwrite: false);
            }

            string temporaryPath = fullDescriptorPath +
                ".recovery-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(
                    temporaryPath,
                    SerializeDescriptor(descriptor),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                File.Move(temporaryPath, fullDescriptorPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or NotSupportedException)
        {
            return Failure(
                fullDescriptorPath,
                PrerequisiteRecoveryState.RecoveryFailed,
                "DEVBRIDGE_DESCRIPTOR_RECONCILIATION_FAILED",
                Bound(exception.Message),
                action: "repair-development-descriptor");
        }

        if (!TryReadDescriptor(
                fullDescriptorPath,
                project,
                fullRepositoryRoot,
                options.RootPath,
                out DevBridgeDevelopmentDescriptor? verified,
                out _,
                out bool verifiedSourceStale,
                out string? verificationError) ||
            verified is null ||
            verifiedSourceStale)
        {
            return Failure(
                fullDescriptorPath,
                PrerequisiteRecoveryState.RecoveryFailed,
                "DEVBRIDGE_DESCRIPTOR_RECONCILIATION_INVALID",
                verificationError ?? "The reconciled descriptor could not be validated after the atomic write.",
                action: "inspect-development-descriptor");
        }

        return new DevBridgeDescriptorReconciliationResult(
            PrerequisiteRecoveryState.Recovered,
            fullDescriptorPath,
            verified,
            Changed: true,
            BackupPath: backupPath,
            Attempts: 1,
            Action: descriptorExists ? "descriptor-reconciled" : "descriptor-created");
    }

    private static bool TryReadDescriptor(
        string descriptorPath,
        string project,
        string repositoryRoot,
        string coordinatorRoot,
        out DevBridgeDevelopmentDescriptor? descriptor,
        out DescriptorFields? fields,
        out bool sourceIsStale,
        out string? error)
    {
        descriptor = null;
        fields = null;
        sourceIsStale = false;
        error = null;
        try
        {
            FileInfo info = new(descriptorPath);
            if (!info.Exists)
            {
                error = "The descriptor is missing.";
                return false;
            }

            if (info.Length > MaximumDescriptorBytes)
            {
                error = "The descriptor exceeds the bounded descriptor size.";
                return false;
            }

            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllText(descriptorPath),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16
                });
            fields = ReadFields(document.RootElement);
            if (!TryCreateDescriptor(project, fields, out descriptor, out error))
            {
                return false;
            }

            string? sourcePath = ResolveSourcePath(
                    descriptor!.SourceProject,
                    repositoryRoot,
                    coordinatorRoot);
            sourceIsStale = sourcePath is null ||
                !string.Equals(
                    descriptor.ExpectedAssembly,
                    ReadExpectedAssembly(sourcePath),
                    StringComparison.OrdinalIgnoreCase);
            return true;
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            error = Bound(exception.Message);
            fields ??= ReadPartialFields(descriptorPath);
            return false;
        }
    }

    private static DescriptorFields? ReadPartialFields(string descriptorPath)
    {
        try
        {
            if (!File.Exists(descriptorPath) ||
                new FileInfo(descriptorPath).Length > MaximumDescriptorBytes)
            {
                return null;
            }

            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllText(descriptorPath),
                new JsonDocumentOptions { MaxDepth = 16 });
            return ReadFields(document.RootElement);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static DescriptorFields ReadFields(JsonElement root)
    {
        JsonElement? runtimePackage = root.TryGetProperty("runtimePackage", out JsonElement value)
            ? value.Clone()
            : null;
        return new DescriptorFields(
            GetString(root, "schemaVersion"),
            GetString(root, "project"),
            GetString(root, "sourceProject"),
            GetString(root, "configuration"),
            GetString(root, "expectedAssembly"),
            GetString(root, "deploymentTarget"),
            GetString(root, "testRecipe"),
            runtimePackage,
            GetString(root, "deploymentRole"),
            GetString(root, "canonicalProjectId"),
            GetString(root, "metadataOwner"),
            GetString(root, "metadataSource"),
            GetString(root, "contractProducer"),
            GetString(root, "materializedContractPath"));
    }

    private static bool TryCreateDescriptor(
        string project,
        DescriptorFields fields,
        out DevBridgeDevelopmentDescriptor? descriptor,
        out string? error)
    {
        descriptor = null;
        error = null;
        if (!string.Equals(
                fields.SchemaVersion,
                DevBridgeModDevelopmentSchemas.Current,
                StringComparison.Ordinal) ||
            !string.Equals(fields.Project, project, StringComparison.Ordinal) ||
            !IsConfiguration(fields.Configuration) ||
            !IsSafeRelativePath(fields.SourceProject, requireExtension: ".csproj") ||
            !IsSafeAssembly(fields.ExpectedAssembly) ||
            !IsSafeRelativePath(fields.DeploymentTarget, requireExtension: null) ||
            !IsSafeToken(fields.TestRecipe) ||
            (!string.IsNullOrWhiteSpace(fields.DeploymentRole) &&
                fields.DeploymentRole is not ("mod" or "tooling-only")))
        {
            error = "The existing descriptor is malformed, stale, or contains an unsafe path.";
            return false;
        }

        descriptor = new DevBridgeDevelopmentDescriptor(
            fields.SchemaVersion!,
            fields.Project!,
            fields.SourceProject!,
            fields.Configuration!,
            fields.ExpectedAssembly!,
            fields.DeploymentTarget!,
            fields.TestRecipe!,
            fields.RuntimePackage,
            fields.DeploymentRole,
            fields.CanonicalProjectId,
            fields.MetadataOwner,
            fields.MetadataSource,
            fields.ContractProducer,
            fields.MaterializedContractPath);
        return true;
    }

    private static bool ValidateDescriptor(
        DevBridgeDevelopmentDescriptor descriptor,
        string project,
        string repositoryRoot,
        string coordinatorRoot,
        string? deploymentRoot,
        out string? error)
    {
        error = null;
        if (!string.Equals(descriptor.SchemaVersion, DevBridgeModDevelopmentSchemas.Current, StringComparison.Ordinal) ||
            !string.Equals(descriptor.Project, project, StringComparison.Ordinal) ||
            !IsConfiguration(descriptor.Configuration) ||
            !IsSafeRelativePath(descriptor.SourceProject, ".csproj") ||
            !IsSafeAssembly(descriptor.ExpectedAssembly) ||
            !IsSafeRelativePath(descriptor.DeploymentTarget, null) ||
            !IsSafeToken(descriptor.TestRecipe) ||
            (!string.IsNullOrWhiteSpace(descriptor.DeploymentRole) &&
                descriptor.DeploymentRole is not ("mod" or "tooling-only")))
        {
            error = "The reconstructed descriptor contains invalid contract fields.";
            return false;
        }

        if (ResolveSourcePath(
                descriptor.SourceProject,
                repositoryRoot,
                coordinatorRoot) is null)
        {
            error = "The reconstructed source project does not exist under the canonical development roots.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(deploymentRoot) &&
            Path.IsPathRooted(deploymentRoot))
        {
            string target = Path.GetFullPath(Path.Combine(
                deploymentRoot,
                descriptor.DeploymentTarget.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsWithin(target, deploymentRoot))
            {
                error = "The reconstructed deployment target escapes the configured deployment root.";
                return false;
            }
        }

        return true;
    }

    private static DescriptorCandidate? SelectProjectCandidate(
        string project,
        string repositoryRoot,
        string coordinatorRoot,
        IReadOnlyList<string>? changedPaths)
    {
        var roots = new List<(string Root, bool Repository)>();
        if (Directory.Exists(repositoryRoot))
        {
            roots.Add((repositoryRoot, true));
        }

        if (Directory.Exists(coordinatorRoot) &&
            !roots.Any(item => item.Root.Equals(coordinatorRoot, StringComparison.OrdinalIgnoreCase)))
        {
            roots.Add((coordinatorRoot, false));
        }

        var candidates = new List<DescriptorCandidate>();
        foreach ((string root, bool isRepository) in roots)
        {
            foreach (string file in EnumerateProjectFiles(root))
            {
                DescriptorCandidate? candidate = TryReadProjectCandidate(
                    file,
                    root,
                    isRepository,
                    project,
                    repositoryRoot,
                    changedPaths);
                if (candidate is not null)
                {
                    candidates.Add(candidate);
                }
            }

            // Repository metadata is authoritative.  Do not let a tooling
            // project in the coordinator root win over a unique source
            // project in the affected repository.
            if (isRepository && candidates.Count > 0)
            {
                break;
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        int bestScore = candidates.Max(static candidate => candidate.Score);
        DescriptorCandidate[] best = candidates
            .Where(candidate => candidate.Score == bestScore)
            .OrderBy(candidate => candidate.SourceProject, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return best.Length == 1 ? best[0] : null;
    }

    private static IEnumerable<string> EnumerateProjectFiles(string root)
    {
        int count = 0;
        foreach (string file in Directory.EnumerateFiles(
                     root,
                     "*.csproj",
                     SearchOption.AllDirectories))
        {
            if (ShouldSkipPath(file))
            {
                continue;
            }

            yield return file;
            count++;
            if (count >= MaximumProjectCandidates)
            {
                yield break;
            }
        }
    }

    private static DescriptorCandidate? TryReadProjectCandidate(
        string file,
        string root,
        bool isRepository,
        string project,
        string repositoryRoot,
        IReadOnlyList<string>? changedPaths)
    {
        try
        {
            XDocument document = XDocument.Load(file, LoadOptions.None);
            string assemblyName = document
                .Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "AssemblyName")
                ?.Value
                ?.Trim() ?? Path.GetFileNameWithoutExtension(file);
            if (!IsSafeToken(assemblyName))
            {
                assemblyName = Path.GetFileNameWithoutExtension(file);
            }
            if (!IsSafeAssembly(assemblyName + ".dll"))
            {
                return null;
            }

            string sourceProject = Path.GetRelativePath(root, file).Replace('\\', '/');
            int score = isRepository ? 10 : 0;
            string fileStem = Path.GetFileNameWithoutExtension(file);
            string directoryName = Directory.GetParent(file)?.Name ?? string.Empty;
            if (string.Equals(fileStem, project, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(directoryName, project, StringComparison.OrdinalIgnoreCase))
            {
                score += 100;
            }

            foreach (string? changedPath in changedPaths ?? [])
            {
                if (string.IsNullOrWhiteSpace(changedPath))
                {
                    continue;
                }

                string candidatePath = Path.GetFullPath(file);
                string fullChangedPath;
                try
                {
                    fullChangedPath = Path.IsPathRooted(changedPath)
                        ? Path.GetFullPath(changedPath)
                        : Path.GetFullPath(Path.Combine(repositoryRoot, changedPath));
                }
                catch (Exception)
                {
                    continue;
                }

                string? candidateDirectory = Directory.GetParent(candidatePath)?.FullName;
                if (candidatePath.Equals(fullChangedPath, StringComparison.OrdinalIgnoreCase) ||
                    candidateDirectory is not null &&
                    IsWithin(fullChangedPath, candidateDirectory))
                {
                    score += 50;
                }
            }

            return new DescriptorCandidate(sourceProject, assemblyName, score);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? SelectDeploymentTarget(
        DevBridgeModDevelopmentAdapterOptions options,
        string? existing,
        string repositoryRoot,
        string expectedAssembly)
    {
        if (IsSafeRelativePath(options.DeploymentTarget, null))
        {
            return options.DeploymentTarget;
        }

        if (IsSafeRelativePath(existing, null))
        {
            return existing;
        }

        var candidates = new List<string>();
        if (Directory.Exists(repositoryRoot))
        {
            foreach (string directory in Directory.EnumerateDirectories(
                         repositoryRoot,
                         "Assemblies",
                         SearchOption.AllDirectories))
            {
                if (ShouldSkipPath(directory))
                {
                    continue;
                }

                string? version = Directory.GetParent(directory)?.Name;
                if (string.IsNullOrWhiteSpace(version) ||
                    !version.Any(char.IsDigit))
                {
                    continue;
                }

                string relative = Path.GetRelativePath(repositoryRoot, directory)
                    .Replace('\\', '/');
                candidates.Add(relative + "/" + expectedAssembly);
            }
        }

        if (candidates.Count > 1)
        {
            HashSet<string> supportedVersions = ReadSupportedVersions(repositoryRoot);
            string[] filtered = candidates
                .Where(candidate =>
                {
                    string[] parts = candidate.Split('/');
                    return parts.Length >= 2 && supportedVersions.Contains(parts[^2]);
                })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (filtered.Length > 0)
            {
                candidates = filtered.ToList();
            }
        }

        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        if (!string.IsNullOrWhiteSpace(options.DeploymentRoot) &&
            Directory.Exists(options.DeploymentRoot))
        {
            string[] deployed = Directory
                .EnumerateFiles(
                    options.DeploymentRoot,
                    expectedAssembly,
                    SearchOption.AllDirectories)
                .Where(file => !ShouldSkipPath(file))
                .Take(2)
                .ToArray();
            if (deployed.Length == 1)
            {
                return Path.GetRelativePath(options.DeploymentRoot, deployed[0])
                    .Replace('\\', '/');
            }
        }

        return null;
    }

    private static HashSet<string> ReadSupportedVersions(string repositoryRoot)
    {
        var versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (string about in Directory.EnumerateFiles(
                         repositoryRoot,
                         "About.xml",
                         SearchOption.AllDirectories).Take(8))
            {
                XDocument document = XDocument.Load(about, LoadOptions.None);
                foreach (XElement element in document
                             .Descendants()
                             .Where(element => element.Name.LocalName == "li"))
                {
                    string value = element.Value.Trim();
                    if (value.Length is > 0 and <= 32)
                    {
                        versions.Add(value);
                    }
                }
            }
        }
        catch (Exception)
        {
            // The deployment directory remains the stronger signal; an
            // unreadable About.xml simply leaves candidates ambiguous.
        }

        return versions;
    }

    private static string SelectConfiguration(string? preferred, string? existing) =>
        IsConfiguration(preferred)
            ? preferred!
            : IsConfiguration(existing)
                ? existing!
                : "Release";

    private static string? SelectToken(string? preferred, string? existing) =>
        IsSafeToken(preferred)
            ? preferred
            : IsSafeToken(existing)
                ? existing
                : null;

    private static bool IsConfiguration(string? value) =>
        string.Equals(value, "Debug", StringComparison.Ordinal) ||
        string.Equals(value, "Release", StringComparison.Ordinal);

    private static bool IsSafeToken(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Token.IsMatch(value);

    private static bool IsSafeAssembly(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
        !value.Contains('/') &&
        !value.Contains('\\') &&
        IsSafeToken(value[..^4]);

    private static bool IsSafeRelativePath(string? value, string? requireExtension)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            Path.IsPathRooted(value) ||
            value.Contains(':') ||
            value.Contains('\0'))
        {
            return false;
        }

        string normalized = value.Replace('\\', '/');
        if (normalized.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            return false;
        }

        return requireExtension is null ||
            normalized.EndsWith(requireExtension, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveSourcePath(
        string sourceProject,
        string repositoryRoot,
        string coordinatorRoot)
    {
        foreach (string root in new[] { repositoryRoot, coordinatorRoot })
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            string candidate = Path.GetFullPath(Path.Combine(
                root,
                sourceProject.Replace('/', Path.DirectorySeparatorChar)));
            if (IsWithin(candidate, root) && File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? ReadExpectedAssembly(string sourceProject)
    {
        try
        {
            XDocument document = XDocument.Load(sourceProject, LoadOptions.None);
            string assemblyName = document
                .Descendants()
                .FirstOrDefault(element => element.Name.LocalName == "AssemblyName")
                ?.Value
                ?.Trim() ?? Path.GetFileNameWithoutExtension(sourceProject);
            if (!IsSafeToken(assemblyName))
            {
                assemblyName = Path.GetFileNameWithoutExtension(sourceProject);
            }

            return IsSafeToken(assemblyName) ? assemblyName + ".dll" : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
    private static string SerializeDescriptor(
        DevBridgeDevelopmentDescriptor descriptor)
    {
        var fields = new Dictionary<string, object?>
        {
            ["schemaVersion"] = descriptor.SchemaVersion,
            ["project"] = descriptor.Project,
            ["sourceProject"] = descriptor.SourceProject,
            ["configuration"] = descriptor.Configuration,
            ["expectedAssembly"] = descriptor.ExpectedAssembly,
            ["deploymentTarget"] = descriptor.DeploymentTarget,
            ["testRecipe"] = descriptor.TestRecipe
        };
        AddIfPresent(fields, "canonicalProjectId", descriptor.CanonicalProjectId);
        AddIfPresent(fields, "metadataOwner", descriptor.MetadataOwner);
        AddIfPresent(fields, "metadataSource", descriptor.MetadataSource);
        AddIfPresent(fields, "contractProducer", descriptor.ContractProducer);
        AddIfPresent(fields, "materializedContractPath", descriptor.MaterializedContractPath);
        if (descriptor.RuntimePackage is JsonElement runtimePackage)
        {
            fields["runtimePackage"] = runtimePackage;
        }
        if (!string.IsNullOrWhiteSpace(descriptor.DeploymentRole))
        {
            fields["deploymentRole"] = descriptor.DeploymentRole;
        }

        return JsonSerializer.Serialize(
            fields,
            new JsonSerializerOptions { WriteIndented = true });
    }

    private static void AddIfPresent(Dictionary<string, object?> fields, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            fields[name] = value;
        }
    }

    private static bool IsExplicitNonProductionDescriptor(string descriptorPath)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllText(descriptorPath),
                new JsonDocumentOptions { MaxDepth = 16 });
            JsonElement root = document.RootElement;
            string? entityType = GetString(root, "entityType");
            return entityType is "fixture" or "test" or "internal" or "example" &&
                root.TryGetProperty("productionEligible", out JsonElement eligible) &&
                eligible.ValueKind == JsonValueKind.False;
        }
        catch (Exception) when (File.Exists(descriptorPath))
        {
            return false;
        }
    }

    private static string? GetString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DevBridgeDescriptorReconciliationResult Failure(
        string descriptorPath,
        PrerequisiteRecoveryState state,
        string code,
        string? error,
        string? action = null) =>
        new(
            state,
            SafeFullPath(descriptorPath),
            null,
            code,
            Bound(error),
            Attempts: state == PrerequisiteRecoveryState.Ready ? 0 : 1,
            Action: action);

    private static string SafeFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return path;
        }
    }

    private static bool ShouldSkipPath(string path)
    {
        string normalized = path.Replace('\\', '/');
        return normalized
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals(".rimctx", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals(".rimdev", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                segment.StartsWith(".devbridge-", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsWithin(string candidate, string root)
    {
        string fullCandidate = Path.GetFullPath(candidate);
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return fullCandidate.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) ||
            fullCandidate.StartsWith(
                fullRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private static string? Bound(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        return trimmed.Length <= 1024 ? trimmed : trimmed[..1024];
    }

    private sealed record DescriptorFields(
        string? SchemaVersion,
        string? Project,
        string? SourceProject,
        string? Configuration,
        string? ExpectedAssembly,
        string? DeploymentTarget,
        string? TestRecipe,
        JsonElement? RuntimePackage,
        string? DeploymentRole,
        string? CanonicalProjectId,
        string? MetadataOwner,
        string? MetadataSource,
        string? ContractProducer,
        string? MaterializedContractPath);

    private sealed record DescriptorCandidate(
        string SourceProject,
        string AssemblyName,
        int Score);
}
