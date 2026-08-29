using RimLiaison.Catalog;

namespace RimLiaison.Stack;

internal sealed record StackInitResult(
    int ExitCode,
    IReadOnlyDictionary<string, object?> Output);

internal static class StackInitializer
{
    private const string ManifestRelativePath = ".rimdev/stack.json";
    private const string AgentsRelativePath = "AGENTS.md";

    public static StackInitResult Run(CliRequest request)
    {
        string root = request.StackManifest.RepositoryRoot;
        var files = new List<Dictionary<string, string>>();
        bool conflict = false;

        string rimDevPath = Path.Combine(root, ".rimdev");
        string manifestPath = Path.Combine(rimDevPath, "stack.json");
        if (File.Exists(rimDevPath))
        {
            files.Add(FileStatus(ManifestRelativePath, "conflicting"));
            conflict = true;
        }
        else
        {
            string status;
            try
            {
                Directory.CreateDirectory(rimDevPath);
                status = WriteManifest(request, manifestPath, root);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                NotSupportedException)
            {
                status = "conflicting";
            }

            files.Add(FileStatus(ManifestRelativePath, status));
            conflict |= status == "conflicting";
        }

        if (!request.InitManifestOnly)
        {
            string agentsPath = Path.Combine(root, "AGENTS.md");
            string agentsStatus = WriteAgents(agentsPath, request.InitForce);
            files.Add(FileStatus(AgentsRelativePath, agentsStatus));
            conflict |= agentsStatus == "conflicting";
        }

        var output = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = conflict ? "conflict" : "ok",
            ["files"] = files
        };
        if (!conflict)
        {
            output["nextAction"] = "rimliaison doctor --json";
        }
        else
        {
            output["errorCode"] = request.StackManifest.ErrorCode ?? "STACK_INIT_CONFLICT";
            output["nextAction"] = request.StackManifest.Manifest is null &&
                request.StackManifest.Found
                ? "rimliaison init --json --manifest-only --force"
                : "rimliaison doctor --json";
        }

        return new(
            conflict ? CliExitCodes.ConservativeSelection : CliExitCodes.Success,
            output);
    }

    private static string WriteManifest(
        CliRequest request,
        string path,
        string root)
    {
        bool exists = File.Exists(path);
        if (exists && !request.InitForce && request.StackManifest.Manifest is null)
        {
            return "conflicting";
        }

        try
        {
            RimDevStackManifest manifest = BuildManifest(request, root);
            if (exists && !request.InitForce &&
                request.StackManifest.Manifest is not null &&
                ManifestEquals(request.StackManifest.Manifest, manifest))
            {
                return "existing";
            }

            WriteText(
                path,
                StackManifestResolver.Serialize(manifest),
                exists || request.InitForce);
            return exists ? "updated" : "created";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return "conflicting";
        }
        catch (IOException)
        {
            return "conflicting";
        }
        catch (UnauthorizedAccessException)
        {
            return "conflicting";
        }
    }

    private static string WriteAgents(string path, bool force)
    {
        if (File.Exists(path) && !force)
        {
            return "existing";
        }

        try
        {
            WriteText(path, CanonicalAgentsTemplate(), force);
            return force && File.Exists(path) ? "updated" : "created";
        }
        catch (IOException)
        {
            return File.Exists(path) ? "existing" : "conflicting";
        }
        catch (UnauthorizedAccessException)
        {
            return "conflicting";
        }
    }

    private static RimDevStackManifest BuildManifest(CliRequest request, string root)
    {
        RimDevStackManifest? existing = request.StackManifest.Manifest;
        bool mergeExisting = existing is not null && !request.InitForce;
        string catalog = existing?.Catalog ?? "TestCatalog/rimtest.catalog.json";
        if (!mergeExisting && request.CatalogExplicit)
        {
            catalog = RelativeCatalogPath(request.CatalogPath, root);
        }

        string? devBridgeProject = mergeExisting
            ? existing!.DevBridgeProject ?? request.DevBridgeProject
            : request.DevBridgeProjectExplicit
                ? request.DevBridgeProject
                : existing?.DevBridgeProject ?? request.DevBridgeProject;
        string? fallbackSuite = mergeExisting
            ? existing!.FallbackSuite ?? request.FallbackSuite
            : request.FallbackSuiteExplicit
                ? request.FallbackSuite
                : existing?.FallbackSuite ?? request.FallbackSuite;
        CatalogDocument? existingCatalog = null;
        if (mergeExisting && !string.IsNullOrWhiteSpace(existing!.FallbackSuite))
        {
            existingCatalog = LoadValidCatalog(Path.Combine(root, catalog));
        }

        if (mergeExisting &&
            !string.IsNullOrWhiteSpace(existing!.FallbackSuite) &&
            existingCatalog is not null &&
            !IsUsableFallbackSuite(existingCatalog, existing.FallbackSuite))
        {
            string? replacement = request.FallbackSuiteExplicit ||
                !string.Equals(request.FallbackSuite, existing.FallbackSuite, StringComparison.Ordinal)
                ? request.FallbackSuite
                : DiscoverFallbackSuite(Path.Combine(root, catalog));
            fallbackSuite = replacement ?? existing.FallbackSuite;
        }

        fallbackSuite ??= DiscoverFallbackSuite(Path.Combine(root, catalog));

        return new RimDevStackManifest
        {
            Project = existing?.Project ?? ProjectName(root),
            DevBridgeProject = devBridgeProject,
            Catalog = catalog,
            FallbackSuite = fallbackSuite,
            RimBridge = existing?.RimBridge ?? "via-devbridge",
            Workload = existing?.Workload,
            ProjectType = existing?.ProjectType,
            PackageId = existing?.PackageId,
            SourceProject = existing?.SourceProject,
            Configuration = existing?.Configuration,
            ExpectedAssembly = existing?.ExpectedAssembly,
            DeploymentTarget = existing?.DeploymentTarget,
            TestRecipe = existing?.TestRecipe,
            RuntimePackage = existing?.RuntimePackage,
            RuntimeFolder = existing?.RuntimeFolder,
            Dependencies = existing?.Dependencies
        };
    }

    private static string? DiscoverFallbackSuite(string path)
    {
        CatalogDocument? catalog = LoadValidCatalog(path);
        if (catalog is null)
        {
            return null;
        }

        CatalogSuite? smoke = (catalog.Suites ?? [])
            .FirstOrDefault(suite => suite is not null &&
                string.Equals(suite.Id, "smoke", StringComparison.Ordinal) &&
                CatalogNavigator.ResolvedTestIds(catalog, suite.Id).Count > 0);
        if (smoke is not null)
        {
            return smoke.Id;
        }

        return (catalog.Suites ?? [])
            .Where(suite => suite is not null)
            .OrderBy(suite => suite.Id, StringComparer.Ordinal)
            .FirstOrDefault(suite =>
                CatalogNavigator.ResolvedTestIds(catalog, suite.Id).Count > 0)
            ?.Id;
    }

    private static bool IsUsableFallbackSuite(CatalogDocument catalog, string suiteId)
    {
        return CatalogNavigator.FindSuite(catalog, suiteId) is CatalogSuite suite &&
            CatalogNavigator.ResolvedTestIds(catalog, suite.Id).Count > 0;
    }

    private static CatalogDocument? LoadValidCatalog(string path)
    {
        CatalogLoadResult loaded = CatalogLoader.Load(path);
        if (loaded.Catalog is null)
        {
            return null;
        }

        return CatalogValidator.Validate(loaded.Catalog).IsValid
            ? loaded.Catalog
            : null;
    }

    private static bool ManifestEquals(
        RimDevStackManifest left,
        RimDevStackManifest right) =>
        string.Equals(left.SchemaVersion, right.SchemaVersion, StringComparison.Ordinal) &&
        string.Equals(left.Project, right.Project, StringComparison.Ordinal) &&
        string.Equals(left.DevBridgeProject, right.DevBridgeProject, StringComparison.Ordinal) &&
        string.Equals(left.Catalog, right.Catalog, StringComparison.Ordinal) &&
        string.Equals(left.FallbackSuite, right.FallbackSuite, StringComparison.Ordinal) &&
        string.Equals(left.RimBridge, right.RimBridge, StringComparison.Ordinal) &&
        string.Equals(left.Workload, right.Workload, StringComparison.Ordinal) &&
        string.Equals(left.ProjectType, right.ProjectType, StringComparison.Ordinal) &&
        string.Equals(left.PackageId, right.PackageId, StringComparison.Ordinal) &&
        string.Equals(left.SourceProject, right.SourceProject, StringComparison.Ordinal) &&
        string.Equals(left.Configuration, right.Configuration, StringComparison.Ordinal) &&
        string.Equals(left.ExpectedAssembly, right.ExpectedAssembly, StringComparison.Ordinal) &&
        string.Equals(left.DeploymentTarget, right.DeploymentTarget, StringComparison.Ordinal) &&
        string.Equals(left.TestRecipe, right.TestRecipe, StringComparison.Ordinal) &&
        string.Equals(left.RuntimeFolder, right.RuntimeFolder, StringComparison.Ordinal) &&
        string.Equals(left.RuntimePackage?.GetRawText(), right.RuntimePackage?.GetRawText(), StringComparison.Ordinal) &&
        (left.Dependencies ?? []).SequenceEqual(right.Dependencies ?? [], StringComparer.Ordinal);

    private static string RelativeCatalogPath(string path, string root)
    {
        string fullPath = Path.GetFullPath(path);
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) +
            Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The catalog must be inside the target repository.");
        }

        return Path.GetRelativePath(root, fullPath).Replace('\\', '/');
    }

    private static void WriteText(string path, string content, bool overwrite)
    {
        if (!overwrite)
        {
            using FileStream stream = new(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read);
            using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
            writer.Write(content);
            return;
        }

        File.WriteAllText(path, content, new System.Text.UTF8Encoding(false));
    }

    private static string CanonicalAgentsTemplate()
    {
        using Stream? stream = typeof(StackInitializer).Assembly.GetManifestResourceStream(
            "RimLiaison.CanonicalAgents.md");
        if (stream is null)
        {
            throw new InvalidOperationException("The canonical AGENTS.md template is unavailable.");
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static Dictionary<string, string> FileStatus(string path, string status) =>
        new(StringComparer.Ordinal)
        {
            ["path"] = path,
            ["status"] = status
        };

    private static string ProjectName(string root)
    {
        string trimmed = Path.TrimEndingDirectorySeparator(root);
        string name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? "RimWorldMod" : name;
    }
}
