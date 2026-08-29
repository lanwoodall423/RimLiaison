using System.Xml;
using System.Xml.Linq;

using RimLiaison.Stack;
namespace RimLiaison.Observability;

public sealed record ObservabilityProjectIdentity(
    string ModId,
    string ModName,
    string Source);

public static class ObservabilityProjectIdentityResolver
{
    private const int MaximumMetadataBytes = 256_000;

    public static ObservabilityProjectIdentity Resolve(
        string repositoryRoot,
        string? explicitProject = null)
    {
        string root = Path.GetFullPath(repositoryRoot);
        string? packageId = TryReadAboutValue(root, "packageId");
        string? modName = TryReadAboutValue(root, "name");
        StackManifestResolution stack = StackManifestResolver.Discover(root);
        if (stack.Manifest is { Workload: "production" })
        {
            ProjectIdentity identity = ProjectIdentityResolver.Resolve(stack.Manifest, root);
            return new(
                identity.CanonicalProjectId,
                modName ?? identity.DisplayName,
                "stack-manifest");
        }

        if (!string.IsNullOrWhiteSpace(packageId))
        {
            return new(packageId, modName ?? packageId, "mod-package");
        }

        if (!string.IsNullOrWhiteSpace(explicitProject))
        {
            string project = explicitProject.Trim();
            return new(project, modName ?? project, "stack-manifest");
        }

        string? remote = TryReadOriginIdentity(root);
        if (!string.IsNullOrWhiteSpace(remote))
        {
            return new("git:" + remote, modName ?? DisplayName(remote), "git-remote");
        }

        string? commonGitDirectory = TryFindCommonGitDirectory(root);
        if (!string.IsNullOrWhiteSpace(commonGitDirectory))
        {
            return new(
                "git-common:" + NormalizePath(commonGitDirectory),
                modName ?? DisplayName(Path.GetFileName(commonGitDirectory)),
                "git-common-directory");
        }

        string fallback = Path.GetFileName(Path.TrimEndingDirectorySeparator(root)).Trim();
        if (string.IsNullOrWhiteSpace(fallback))
        {
            fallback = "RimWorldMod";
        }

        return new(fallback, modName ?? fallback, "directory");
    }
    public static bool IsRimLiaisonRepository(string repositoryRoot)
    {
        string root;
        try
        {
            root = Path.GetFullPath(repositoryRoot);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or
            NotSupportedException)
        {
            return false;
        }

        string? remote = TryReadOriginIdentity(root);
        if (remote?.EndsWith("/rimliaison", StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        return File.Exists(Path.Combine(root, "RimLiaison.sln")) &&
            File.Exists(Path.Combine(root, "src", "RimLiaison.Cli", "RimLiaison.Cli.csproj"));
    }

    public static bool TryNormalizeKnownTemporaryIdentity(
        string? modId,
        out string canonicalModId)
    {
        canonicalModId = string.Empty;
        if (string.IsNullOrWhiteSpace(modId))
        {
            return false;
        }
        string value = modId.Trim();
        string? prefix = value.StartsWith(
                "RimLiaison-tests-",
                StringComparison.OrdinalIgnoreCase)
            ? "RimLiaison-tests-"
            : value.StartsWith(
                "RimLiaison-worktree-",
                StringComparison.OrdinalIgnoreCase)
                ? "RimLiaison-worktree-"
                : null;
        if (prefix is null ||
            value.Length - prefix.Length < 4 ||
            !value[prefix.Length..].All(
                character => char.IsLetterOrDigit(character) || character is '-' or '_'))
        {
            return false;
        }
        canonicalModId = "RimLiaison";
        return true;
    }

    private static string? TryReadAboutValue(string root, string localName)
    {
        string aboutPath = Path.Combine(root, "About", "About.xml");
        try
        {
            if (!File.Exists(aboutPath) || new FileInfo(aboutPath).Length > MaximumMetadataBytes)
            {
                return null;
            }

            XDocument document = XDocument.Load(aboutPath, LoadOptions.None);
            string? value = document
                .Descendants()
                .FirstOrDefault(element =>
                    string.Equals(element.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase))?
                .Value
                .Trim();
            return string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl)
                ? null
                : value;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            XmlException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string? TryReadOriginIdentity(string root)
    {
        string? gitDirectory = TryFindGitDirectory(root);
        if (gitDirectory is null)
        {
            return null;
        }

        string commonDirectory = ResolveCommonGitDirectory(gitDirectory);
        string configPath = Path.Combine(commonDirectory, "config");
        try
        {
            if (!File.Exists(configPath) || new FileInfo(configPath).Length > MaximumMetadataBytes)
            {
                return null;
            }

            bool inOrigin = false;
            foreach (string rawLine in File.ReadLines(configPath))
            {
                string line = rawLine.Trim();
                if (line.Length > 0 && line[0] == '[')
                {
                    inOrigin = line.Equals("[remote \"origin\"]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inOrigin || !line.StartsWith("url", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int separator = line.IndexOf('=');
                if (separator < 0)
                {
                    continue;
                }

                string? identity = NormalizeRemote(line[(separator + 1)..].Trim());
                if (!string.IsNullOrWhiteSpace(identity))
                {
                    return identity;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    private static string? NormalizeRemote(string value)
    {
        value = value.Trim();
        if (value.Length == 0 || value.Any(char.IsControl))
        {
            return null;
        }

        string hostPath;
        if (value.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
        {
            int separator = value.IndexOf(':', 4);
            if (separator < 0)
            {
                return null;
            }

            hostPath = value[4..].Replace(':', '/');
        }
        else if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
                 !string.IsNullOrWhiteSpace(uri.Host))
        {
            hostPath = uri.Host + uri.AbsolutePath;
        }
        else
        {
            return "local/" + NormalizePath(value);
        }

        hostPath = hostPath.Replace('\\', '/').Trim('/');
        if (hostPath.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            hostPath = hostPath[..^4].TrimEnd('/');
        }

        return hostPath.Length == 0 ? null : hostPath.ToLowerInvariant();
    }

    private static string? TryFindCommonGitDirectory(string root)
    {
        string? gitDirectory = TryFindGitDirectory(root);
        return gitDirectory is null ? null : ResolveCommonGitDirectory(gitDirectory);
    }

    private static string? TryFindGitDirectory(string root)
    {
        string gitPath = Path.Combine(root, ".git");
        try
        {
            if (Directory.Exists(gitPath))
            {
                return Path.GetFullPath(gitPath);
            }

            if (!File.Exists(gitPath) || new FileInfo(gitPath).Length > 4_096)
            {
                return null;
            }

            string? line = File.ReadLines(gitPath)
                .Select(value => value.Trim())
                .FirstOrDefault(value => value.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase));
            if (line is null)
            {
                return null;
            }

            string path = line["gitdir:".Length..].Trim();
            return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or
            UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static string ResolveCommonGitDirectory(string gitDirectory)
    {
        string commondirPath = Path.Combine(gitDirectory, "commondir");
        try
        {
            if (File.Exists(commondirPath))
            {
                string common = File.ReadAllText(commondirPath).Trim();
                if (common.Length > 0)
                {
                    return Path.GetFullPath(
                        Path.IsPathRooted(common)
                            ? common
                            : Path.Combine(gitDirectory, common));
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException)
        {
        }

        DirectoryInfo? current = new(gitDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "config")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return gitDirectory;
    }

    private static string NormalizePath(string value) =>
        value.Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/')
            .Trim('/')
            .ToLowerInvariant();

    private static string DisplayName(string value)
    {
        string display = value.Replace('\\', '/').Trim('/');
        int separator = display.LastIndexOf('/');
        display = separator >= 0 ? display[(separator + 1)..] : display;
        return display.Length == 0 ? "RimWorldMod" : display;
    }
}

public static class ObservabilityEntityIdentityResolver
{
    private static readonly string[] LegacyToolNames =
    [
        "rimliaison",
        "rimbench",
        "rimtest",
        "rimcontext",
        "rimcontent",
        "rimerror",
        "devbridge2"
    ];

    public static ObservabilityEntityIdentity ForPersisted(
        string? entityType,
        string? canonicalEntityId,
        string modId,
        string modName,
        string? workloadKind = null,
        string? qualificationProfile = null)
    {
        string type = NormalizeEntityType(entityType);
        if (IsFixtureWorkload(workloadKind, qualificationProfile))
        {
            return ObservabilityEntityIdentity.ForFixture(
                "qualification",
                "Qualification fixture");
        }

        if (!string.IsNullOrWhiteSpace(canonicalEntityId))
        {
            string resolvedType = ResolvePersistedEntityType(type, canonicalEntityId);
            return CreatePersistedIdentity(
                resolvedType,
                canonicalEntityId,
                modName);
        }

        if (type == ObservabilityEntityTypes.Tool &&
            TryToolAlias(modId, out string typedToolId))
        {
            return ObservabilityEntityIdentity.ForTool(
                typedToolId,
                ToolDisplayName(typedToolId));
        }

        if (type == ObservabilityEntityTypes.Mod)
        {
            return ObservabilityEntityIdentity.ForMod(modId, modName);
        }

        if (type == ObservabilityEntityTypes.Tool)
        {
            return ObservabilityEntityIdentity.ForUnknown(modId, modName);
        }

        if (type != ObservabilityEntityTypes.Unknown)
        {
            return ObservabilityEntityIdentity.Create(type, modId, modName);
        }

        if (TryToolAlias(modId, out string legacyToolId))
        {
            return ObservabilityEntityIdentity.ForTool(
                legacyToolId,
                ToolDisplayName(legacyToolId));
        }

        if (modId.Trim().StartsWith("mod.", StringComparison.OrdinalIgnoreCase))
        {
            return ObservabilityEntityIdentity.ForMod(modId, modName);
        }

        return ObservabilityEntityIdentity.ForUnknown(modId, modName);
    }
    public static bool Matches(
        AgentSnapshot agent,
        string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        if (string.Equals(agent.AgentId, candidate, StringComparison.Ordinal) ||
            string.Equals(agent.ModId, candidate, StringComparison.Ordinal))
        {
            return true;
        }

        ObservabilityEntityIdentity identity = ForPersisted(
            agent.EntityType,
            candidate,
            agent.ModId,
            agent.ModName,
            agent.WorkloadKind,
            agent.QualificationProfile);
        return string.Equals(
            agent.CanonicalEntityId,
            identity.CanonicalEntityId,
            StringComparison.OrdinalIgnoreCase);
    }


    public static ObservabilityEntityIdentity ForProducer(
        string modId,
        string modName)
    {
        ObservabilityEntityIdentity identity = ForPersisted(
            null,
            null,
            modId,
            modName);
        return identity.EntityType == ObservabilityEntityTypes.Unknown
            ? ObservabilityEntityIdentity.ForMod(modId, modName)
            : identity;
    }

    public static ObservabilityEntityIdentity ForMod(ObservabilityProjectIdentity project) =>
        ObservabilityEntityIdentity.ForMod(project.ModId, project.ModName);

    private static ObservabilityEntityIdentity CreatePersistedIdentity(
        string entityType,
        string canonicalEntityId,
        string displayName)
    {
        if (entityType == ObservabilityEntityTypes.Tool)
        {
            string candidate = canonicalEntityId;
            int separator = candidate.IndexOf(':');
            if (separator >= 0)
            {
                candidate = candidate[(separator + 1)..];
            }

            if (TryToolAlias(candidate, out string toolId))
            {
                return ObservabilityEntityIdentity.ForTool(
                    toolId,
                    ToolDisplayName(toolId));
            }
        }

        ObservabilityEntityIdentity identity = ObservabilityEntityIdentity.Create(
            entityType,
            canonicalEntityId,
            displayName);
        if (identity.EntityType == ObservabilityEntityTypes.Tool &&
            identity.CanonicalEntityId == "tool:rimliaison")
        {
            return ObservabilityEntityIdentity.ForTool("rimliaison", "RimLiaison");
        }

        return identity;
    }

    private static string ResolvePersistedEntityType(
        string entityType,
        string canonicalEntityId)
    {
        if (entityType != ObservabilityEntityTypes.Unknown)
        {
            return entityType;
        }

        int separator = canonicalEntityId.IndexOf(':');
        if (separator > 0)
        {
            string prefix = canonicalEntityId[..separator].ToLowerInvariant();
            if (IsKnownEntityType(prefix))
            {
                return prefix;
            }
        }

        return ObservabilityEntityTypes.Unknown;
    }

    private static string NormalizeEntityType(string? entityType)
    {
        string value = entityType?.Trim().ToLowerInvariant() ?? string.Empty;
        return IsKnownEntityType(value)
            ? value
            : ObservabilityEntityTypes.Unknown;
    }

    private static bool IsKnownEntityType(string value) =>
        value is ObservabilityEntityTypes.Mod or
            ObservabilityEntityTypes.Tool or
            ObservabilityEntityTypes.Infrastructure or
            ObservabilityEntityTypes.Fixture or
            ObservabilityEntityTypes.Test or
            ObservabilityEntityTypes.Agent or
            ObservabilityEntityTypes.User or
            ObservabilityEntityTypes.Operator or
            ObservabilityEntityTypes.Process or
            ObservabilityEntityTypes.Session or
            ObservabilityEntityTypes.Run or
            ObservabilityEntityTypes.Activity or
            ObservabilityEntityTypes.Event or
            ObservabilityEntityTypes.Runtime or
            ObservabilityEntityTypes.Unknown;

    private static bool IsFixtureWorkload(
        string? workloadKind,
        string? qualificationProfile) =>
        !string.IsNullOrWhiteSpace(qualificationProfile) ||
        workloadKind?.Trim().ToLowerInvariant() is
            "qualification" or "fixture" or "test";

    private static bool TryToolAlias(string value, out string toolId)
    {
        string normalized = value.Trim().ToLowerInvariant();
        if (normalized.StartsWith("[tool]", StringComparison.Ordinal))
        {
            normalized = normalized["[tool]".Length..].Trim();
        }

        if (normalized.StartsWith("rimliaison-tests-", StringComparison.Ordinal) ||
            normalized.StartsWith("rimliaison-worktree-", StringComparison.Ordinal))
        {
            toolId = "rimliaison";
            return true;
        }

        foreach (string name in LegacyToolNames)
        {
            if (normalized == name)
            {
                toolId = name;
                return true;
            }
        }

        toolId = string.Empty;
        return false;
    }

    private static string ToolDisplayName(string toolId) =>
        toolId == "rimliaison" ? "RimLiaison" : toolId;
}
