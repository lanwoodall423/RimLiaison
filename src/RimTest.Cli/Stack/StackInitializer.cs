namespace RimTest.Stack;

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
        if (File.Exists(rimDevPath))
        {
            files.Add(FileStatus(ManifestRelativePath, "conflicting"));
            conflict = true;
        }
        else
        {
            Directory.CreateDirectory(rimDevPath);
            string manifestPath = Path.Combine(rimDevPath, "stack.json");
            string status = WriteManifest(request, manifestPath, root);
            files.Add(FileStatus(ManifestRelativePath, status));
            conflict |= status == "conflicting";
        }

        string agentsPath = Path.Combine(root, "AGENTS.md");
        string agentsStatus = WriteAgents(agentsPath, request.InitForce);
        files.Add(FileStatus(AgentsRelativePath, agentsStatus));
        conflict |= agentsStatus == "conflicting";

        var output = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = conflict ? "conflict" : "ok",
            ["files"] = files
        };
        if (!conflict)
        {
            output["nextAction"] = "rimtest doctor --json";
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
        if (File.Exists(path) && !request.InitForce)
        {
            return request.StackManifest.ErrorCode is null ? "existing" : "conflicting";
        }

        try
        {
            RimDevStackManifest manifest = BuildManifest(request, root);
            WriteText(path, StackManifestResolver.Serialize(manifest), request.InitForce);
            return request.InitForce && request.StackManifest.ManifestPath is not null
                ? "updated"
                : "created";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return "conflicting";
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
        string catalog = existing?.Catalog ?? "TestCatalog/rimtest.catalog.json";
        if (request.CatalogExplicit)
        {
            catalog = RelativeCatalogPath(request.CatalogPath, root);
        }

        return new RimDevStackManifest
        {
            Project = existing?.Project ?? ProjectName(root),
            DevBridgeProject = request.DevBridgeProjectExplicit
                ? request.DevBridgeProject
                : existing?.DevBridgeProject,
            Catalog = catalog,
            FallbackSuite = request.FallbackSuiteExplicit
                ? request.FallbackSuite
                : existing?.FallbackSuite,
            RimBridge = existing?.RimBridge ?? "via-devbridge"
        };
    }

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
            "RimTest.CanonicalAgents.md");
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
