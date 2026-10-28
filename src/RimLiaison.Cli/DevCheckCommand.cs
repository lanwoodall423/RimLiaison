using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using RimContext.Core.Configuration;
using RimContext.Core.Discovery;
using RimContext.Core.Impact;
using RimContext.Core.Storage;

namespace RimLiaison;

internal static class DevCheckCommand
{
    private const string HelperPackageId = "direct.validation.runtime.signal";
    private const string BehaviorProtocol = "direct-validation-behavior/v1";
    private const int MaximumDiagnosticLength = 8_192;
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan UnityRuntimeTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan WaylithRuntimeTimeout = TimeSpan.FromSeconds(120);

    // Waylith 1.7 cold startup has been observed to exceed 45 seconds.
    // A completed content-load signal exits the loop immediately in either engine.
    internal static TimeSpan RuntimeTimeoutForGame(RimWorldGameLayout game) =>
        game.IsWaylith ? WaylithRuntimeTimeout : UnityRuntimeTimeout;

    public static async Task<int> RunAsync(
        CliRequest request,
        TextWriter stdout,
        CancellationToken cancellationToken)
    {
        var state = new State();
        long started = Stopwatch.GetTimestamp();
        int exitCode = CliExitCodes.InternalError;
        try
        {
            state.Workspace = ResolveWorkspace(request.Id);
            state.Mod = ReadIdentity(state.Workspace);
            IReadOnlyList<DiscoveredInput> inputs = DiscoverInputs(state.Workspace);
            state.ChangedFiles.AddRange(inputs.Select(input => input.RelativePath));
            state.SourceFiles = inputs.Where(input => input.Kind == DiscoveredFileKinds.Source).ToArray();
            state.ProjectFiles = inputs.Where(input => input.Kind == DiscoveredFileKinds.Project).ToArray();
            state.XmlFiles = inputs.Where(input => input.Kind == DiscoveredFileKinds.Xml).ToArray();
            string? rimWorldRootForBuild = TryResolveRimWorldRoot();
            IReadOnlyDictionary<string, string> buildEnvironment = BuildEnvironment(rimWorldRootForBuild);
            state.ProjectSelection = SelectProjects(
                state.Workspace,
                state.ProjectFiles.Select(project => project.AbsolutePath).ToArray());
            if (state.ProjectSelection.IsAmbiguous)
                throw new InvalidDataException(state.ProjectSelection.Error!);

            state.BuildProject = state.ProjectSelection.BuildRoots.Count == 1
                ? state.ProjectSelection.BuildRoots[0]
                : null;
            if (state.BuildProject is null)
            {
                state.BuildStatus = "BUILD_NOT_APPLICABLE";
            }
            else
            {
                ProcessResult build = await RunProcessAsync(
                        "dotnet",
                        ["build", state.BuildProject, "--configuration", "Release", "--nologo"],
                        state.Workspace,
                        BuildTimeout,
                        state,
                        cancellationToken,
                        buildEnvironment)
                    .ConfigureAwait(false);
                state.BuildStatus = build.ExitCode == 0 && !build.TimedOut ? "BUILD_PASS" : "BUILD_FAIL";
                if (state.BuildStatus == "BUILD_FAIL")
                {
                    AddBuildDiagnostics(state, build);
                    if (build.TimedOut)
                        state.BuildErrors.Add("C# build timed out.");
                }
                else
                {
                    state.TargetAssemblySources = state.ProjectSelection.ProductionProjects
                        .Select(project => ResolveTargetAssembly(state.Workspace, project))
                        .Where(path => path is not null)
                        .Select(path => path!)
                        .ToArray();
                    if (state.TargetAssemblySources.Count != state.ProjectSelection.ProductionProjects.Count)
                    {
                        state.StaticStatus = "STATIC_FAIL";
                        foreach (string project in state.ProjectSelection.ProductionProjects)
                        {
                            if (ResolveTargetAssembly(state.Workspace, project) is null)
                                state.StaticErrors.Add(
                                    "C# build passed but no deployable assembly was found for " +
                                    Path.GetRelativePath(state.Workspace, project) + ".");
                        }
                    }
                }
            }

            if (state.BuildStatus != "BUILD_FAIL" && state.StaticStatus != "STATIC_FAIL")
            {
                ValidateXml(state);
                state.StaticStatus = "STATIC_PASS";
                state.RimContext = BuildRimContext(state);
                RimWorldGameLayout game = ResolveRimWorldGameLayout();
                state.Game = game;
                state.HelperPackagePath = await BuildHelperAsync(
                        game,
                        state,
                        cancellationToken)
                    .ConfigureAwait(false);
                state.Runtime = await RunRuntimeAsync(
                        game,
                        state,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            exitCode = state.BuildStatus == "BUILD_FAIL" ||
                state.StaticStatus == "STATIC_FAIL" ||
                state.Runtime.Status != "RUNTIME_LOAD_PASS"
                ? CliExitCodes.TestFailure
                : CliExitCodes.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            state.InfrastructureErrors.Add("dev-check was cancelled.");
            state.Runtime = RuntimeResult.NotRun("CANCELLED");
            exitCode = CliExitCodes.Cancelled;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or XmlException or InvalidOperationException)
        {
            if (state.StaticStatus == "STATIC_PASS")
            {
                state.InfrastructureErrors.Add(Bound(exception.Message));
                state.Runtime = RuntimeResult.NotRun("INFRASTRUCTURE_FAILURE");
            }
            else
            {
                state.StaticStatus = "STATIC_FAIL";
                state.StaticErrors.Add(Bound(exception.Message));
                state.Runtime = RuntimeResult.NotRun("STATIC_VALIDATION_FAILED");
            }
            exitCode = CliExitCodes.TestFailure;
        }
        catch (Exception exception)
        {
            state.InfrastructureErrors.Add(Bound(exception.ToString()));
            state.Runtime = RuntimeResult.NotRun("INFRASTRUCTURE_FAILURE");
            exitCode = CliExitCodes.InternalError;
        }
        finally
        {
            Cleanup(state);
        }
        return Finish(state, stdout, started, exitCode);
    }

    private static int Finish(State state, TextWriter stdout, long started, int exitCode)
    {
        state.ElapsedMs = ElapsedMilliseconds(started);
        state.InfrastructureStatus = state.InfrastructureErrors.Count == 0
            ? "INFRASTRUCTURE_PASS"
            : "INFRASTRUCTURE_FAIL";
        if (state.InfrastructureStatus == "INFRASTRUCTURE_FAIL" && state.Runtime.Status == "RUNTIME_LOAD_PASS")
            state.Runtime = state.Runtime with { Status = "RUNTIME_LOAD_FAIL", Reason = "INFRASTRUCTURE_FAILURE" };
        if (state.CleanupStatus != "CLEANUP_PASS" && exitCode == CliExitCodes.Success)
            exitCode = CliExitCodes.TestFailure;
        var result = new
        {
            schemaVersion = "rimtest-dev-check/v1",
            mod = state.Mod,
            workspace = state.Workspace,
            game = state.Game is null ? null : new
            {
                version = state.Game.ContentVersion,
                engine = state.Game.IsWaylith ? "Waylith" : "Unity",
                executable = Path.GetFileName(state.Game.ExecutablePath)
            },
            changedFiles = state.ChangedFiles,
            projects = new
            {
                discovered = state.ProjectFiles.Select(project => project.RelativePath).ToArray(),
                production = state.ProjectSelection?.ProductionProjects
                    .Select(project => Path.GetRelativePath(state.Workspace, project)).ToArray() ?? [],
                excluded = state.ProjectSelection?.ExcludedProjects
                    .Select(project => Path.GetRelativePath(state.Workspace, project)).ToArray() ?? [],
                buildRoots = state.ProjectSelection?.BuildRoots
                    .Select(project => Path.GetRelativePath(state.Workspace, project)).ToArray() ?? []
            },
            build = new
            {
                status = state.BuildStatus,
                project = state.BuildProject,
                errors = state.BuildErrors
            },
            @static = new
            {
                status = state.StaticStatus,
                rimContext = state.RimContext,
                errors = state.StaticErrors
            },
            runtime = state.Runtime,
            infrastructure = new { status = state.InfrastructureStatus, errors = state.InfrastructureErrors },
            cleanup = new { status = state.CleanupStatus, errors = state.CleanupErrors },
            performance = new
            {
                elapsedMs = state.ElapsedMs,
                elapsedMsByStage = state.StageTimes,
                externalProcesses = state.Processes.Count,
                processNames = state.Processes
            }
        };
        stdout.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false }));
        return exitCode;
    }

    private static string ResolveWorkspace(string? requested)
    {
        string path = string.IsNullOrWhiteSpace(requested) ? Directory.GetCurrentDirectory() : requested;
        string full = Path.GetFullPath(path);
        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException("Target mod workspace does not exist: " + full);
        return full;
    }

    private static ModIdentity ReadIdentity(string workspace)
    {
        string aboutPath = Path.Combine(workspace, "About", "About.xml");
        if (!File.Exists(aboutPath))
            throw new InvalidDataException("About/About.xml is required.");
        XDocument document = XDocument.Load(aboutPath, LoadOptions.PreserveWhitespace);
        string name = RootElementText(document, "name");
        string packageId = RootElementText(document, "packageId");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(packageId))
            throw new InvalidDataException("About.xml requires name and packageId.");
        if (!packageId.All(static c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-'))
            throw new InvalidDataException("About.xml packageId contains unsafe characters.");
        return new ModIdentity(name, packageId);
    }

    private static string RootElementText(XDocument document, string name) =>
        document.Root is { Name.LocalName: "ModMetaData" }
            ? document.Root.Elements().FirstOrDefault(element => element.Name.LocalName == name)?.Value.Trim() ?? string.Empty
            : string.Empty;
    private static string ElementText(XDocument document, string name) =>
        document.Descendants().FirstOrDefault(element => element.Name.LocalName == name)?.Value.Trim() ?? string.Empty;


    private static IReadOnlyList<DiscoveredInput> DiscoverInputs(string workspace)
    {
        var inputs = new List<DiscoveredInput>();
        foreach (string path in EnumerateFiles(workspace, new[] { ".xml", ".cs", ".csproj" }))
        {
            string relative = Path.GetRelativePath(workspace, path).Replace(Path.DirectorySeparatorChar, '/');
            string extension = Path.GetExtension(path);
            string kind = extension.Equals(".xml", StringComparison.OrdinalIgnoreCase)
                ? DiscoveredFileKinds.Xml
                : extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)
                    ? DiscoveredFileKinds.Source
                    : DiscoveredFileKinds.Project;
            inputs.Add(new DiscoveredInput(path, relative, kind));
        }
        return inputs.OrderBy(input => input.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IEnumerable<string> EnumerateFiles(string root, IReadOnlyCollection<string> extensions)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            foreach (string child in Directory.EnumerateDirectories(directory))
            {
                string name = Path.GetFileName(child);
                if (!name.Equals(".rimctx", StringComparison.OrdinalIgnoreCase) &&
                    !name.Equals("bin", StringComparison.OrdinalIgnoreCase) &&
                    !name.Equals("obj", StringComparison.OrdinalIgnoreCase))
                    pending.Push(child);
            }
            foreach (string file in Directory.EnumerateFiles(directory))
            {
                if (extensions.Contains(Path.GetExtension(file)))
                    yield return file;
            }
        }
    }

    internal static ProjectSelectionResult SelectProjects(
        string workspace,
        IReadOnlyList<string> projectPaths)
    {
        string[] projects = projectPaths
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (projects.Length == 0)
            return new([], [], [], false, null);

        var references = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string project in projects)
        {
            var projectReferences = new List<string>();
            XDocument document;
            try
            {
                document = XDocument.Load(project);
            }
            catch (Exception exception) when (exception is IOException or XmlException)
            {
                throw new InvalidDataException(
                    "Unable to read project " + Path.GetRelativePath(workspace, project) + ": " + exception.Message,
                    exception);
            }
            foreach (XElement reference in document.Descendants().Where(
                         element => element.Name.LocalName == "ProjectReference"))
            {
                string? include = reference.Attribute("Include")?.Value;
                if (string.IsNullOrWhiteSpace(include))
                    throw new InvalidDataException(
                        "Project reference in " + Path.GetRelativePath(workspace, project) + " has no Include.");
                string referencedProject = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project)!, include));
                if (!File.Exists(referencedProject))
                    throw new InvalidDataException(
                        "Project " + Path.GetRelativePath(workspace, project) +
                        " references missing project " + Path.GetRelativePath(workspace, referencedProject) + ".");
                if (projects.Contains(referencedProject, StringComparer.OrdinalIgnoreCase))
                    projectReferences.Add(referencedProject);
            }
            references[project] = projectReferences
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        string[] excluded = projects
            .Where(project => IsObviousSupportProject(workspace, project))
            .ToArray();
        var candidates = projects
            .Except(excluded, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var referencedCandidates = candidates
            .SelectMany(project => references[project])
            .Where(candidates.Contains)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] roots = candidates
            .Where(project => !referencedCandidates.Contains(project))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (roots.Length != 1)
        {
            string reason = roots.Length == 0
                ? "No unambiguous production project root was found."
                : "Multiple unreferenced production projects were found: " +
                  string.Join(", ", roots.Select(path => Path.GetRelativePath(workspace, path)));
            return new([], candidates, excluded, true, reason);
        }

        var production = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(string project)
        {
            if (!visited.Add(project))
                return;
            production.Add(project);
            foreach (string reference in references[project])
                Visit(reference);
        }
        Visit(roots[0]);
        return new(roots, production, excluded, false, null);
    }

    private static bool IsObviousSupportProject(string workspace, string project)
    {
        string relative = Path.GetRelativePath(workspace, project);
        string[] segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string[] supportSegments =
        [
            "test", "tests", "devtool", "devtools", "tool", "tools", "tooling",
            "sample", "samples", "benchmark", "benchmarks", "example", "examples"
        ];
        if (segments[..^1].Any(segment =>
                supportSegments.Contains(segment, StringComparer.OrdinalIgnoreCase)))
            return true;
        string file = Path.GetFileNameWithoutExtension(project);
        return supportSegments.Any(segment =>
            file.EndsWith("." + segment, StringComparison.OrdinalIgnoreCase) ||
            file.EndsWith(segment, StringComparison.OrdinalIgnoreCase));
    }
    private static void AddBuildDiagnostics(State state, ProcessResult build)
    {
        string output = string.Join(
            Environment.NewLine,
            new[] { build.Stdout, build.Stderr }.Where(value => !string.IsNullOrWhiteSpace(value)));
        string[] lines = output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        foreach (string line in lines.Where(IsBuildErrorLine))
        {
            string owner = state.ProjectSelection?.ProductionProjects
                .FirstOrDefault(project =>
                {
                    string relativeProject = Path.GetRelativePath(state.Workspace, project).Replace('\\', '/');
                    string relativeDirectory = Path.GetDirectoryName(relativeProject)?.Replace('\\', '/') ?? "";
                    string normalizedLine = line.Replace('\\', '/');
                    return line.Contains(Path.GetFileName(project), StringComparison.OrdinalIgnoreCase) ||
                        line.Contains(Path.GetDirectoryName(project)!, StringComparison.OrdinalIgnoreCase) ||
                        normalizedLine.Contains(relativeProject, StringComparison.OrdinalIgnoreCase) ||
                        !string.IsNullOrWhiteSpace(relativeDirectory) &&
                        normalizedLine.Contains(relativeDirectory + "/", StringComparison.OrdinalIgnoreCase);
                })
                is { } project
                    ? Path.GetRelativePath(state.Workspace, project)
                    : Path.GetRelativePath(state.Workspace, state.BuildProject!);
            state.BuildErrors.Add(owner + ": " + Bound(line.Trim()));
        }
        if (state.BuildErrors.Count == 0 && !string.IsNullOrWhiteSpace(output))
            state.BuildErrors.Add(Bound(output));
    }

    private static bool IsBuildErrorLine(string line) =>
        line.Contains(": error ", StringComparison.OrdinalIgnoreCase) ||
        line.Contains(" error :", StringComparison.OrdinalIgnoreCase) ||
        line.TrimStart().StartsWith("error ", StringComparison.OrdinalIgnoreCase);

    private static string? TryResolveRimWorldRoot()
    {
        try
        {
            return ResolveRimWorldGameLayout().Root;
        }
        catch (Exception exception) when (exception is DirectoryNotFoundException or FileNotFoundException or InvalidDataException)
        {
            return null;
        }
    }

    private static IReadOnlyDictionary<string, string> BuildEnvironment(string? rimWorldRoot)
    {
        if (string.IsNullOrWhiteSpace(rimWorldRoot))
            return new Dictionary<string, string>();
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["RIMWORLD_ROOT"] = rimWorldRoot
        };
        string? harmony = Environment.GetEnvironmentVariable("DEFERRED_REALITY_HARMONY_PATH");
        if (string.IsNullOrWhiteSpace(harmony) || !File.Exists(harmony))
            harmony = FindWorkshopHarmony(rimWorldRoot);
        if (!string.IsNullOrWhiteSpace(harmony))
            environment["DEFERRED_REALITY_HARMONY_PATH"] = harmony;
        return environment;
    }

    private static string? FindWorkshopHarmony(string rimWorldRoot)
    {
        DirectoryInfo? steamApps = Directory.GetParent(rimWorldRoot)?.Parent;
        string workshop = steamApps is null
            ? string.Empty
            : Path.Combine(steamApps.FullName, "workshop", "content", "294100");
        if (!Directory.Exists(workshop))
            return null;
        try
        {
            return Directory.EnumerateFiles(workshop, "0Harmony.dll", SearchOption.AllDirectories)
                .Where(path => path.Replace('\\', '/').Contains("/Current/Assemblies/", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void ValidateXml(State state)
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            foreach (DiscoveredInput input in state.XmlFiles)
            {
                var document = new XmlDocument { XmlResolver = null };
                document.Load(input.AbsolutePath);
                if (IsDefSourcePath(input.RelativePath))
                {
                    foreach (TypedDefExpectation expectation in ExtractTypedDefExpectations(document, input.RelativePath))
                    {
                        if (!state.ChangedTypedDefs.Contains(expectation))
                            state.ChangedTypedDefs.Add(expectation);
                        if (expectation.DefType == "ThingDef" &&
                            !state.ChangedDefs.Contains(expectation.DefName, StringComparer.Ordinal))
                            state.ChangedDefs.Add(expectation.DefName);
                        state.TargetErrorTokens.Add(expectation.DefName);
                    }
                    XmlNodeList? thingNodes = document.SelectNodes("/Defs/ThingDef");
                    if (thingNodes is null)
                        continue;
                    foreach (XmlNode thing in thingNodes)
                    {
                        foreach (string xpath in new[] { ".//defaultProjectile", ".//texPath" })
                        {
                            XmlNodeList? nodes = thing.SelectNodes(xpath);
                            if (nodes is null)
                                continue;
                            foreach (XmlNode node in nodes)
                            {
                                string value = node.InnerText.Trim();
                                if (!string.IsNullOrWhiteSpace(value))
                                    state.TargetErrorTokens.Add(value);
                            }
                        }
                    }
                }
                else if (IsPatchSourcePath(input.RelativePath))
                {
                    IReadOnlyList<BiomeGenStepExpectation> expectations =
                        ExtractBiomeGenStepExpectations(document, input.RelativePath);
                    foreach (BiomeGenStepExpectation expectation in expectations)
                    {
                        if (!state.ExpectedBiomeGenSteps.Contains(expectation))
                            state.ExpectedBiomeGenSteps.Add(expectation);
                        state.TargetErrorTokens.Add(expectation.GenStepDefName);
                    }
                }
            }
        }
        finally
        {
            state.StageTimes["xml"] = ElapsedMilliseconds(started);
        }
    }

    // Versioned RimWorld mods keep definitions in 1.6/Defs or 1.7/Defs.
    // Reject similarly named paths such as samples/Defs or 1.x/Defs.
    internal static bool IsDefSourcePath(string relativePath)
    {
        string normalized = relativePath.Replace('\\', '/');
        if (normalized.StartsWith("Defs/", StringComparison.OrdinalIgnoreCase))
            return true;
        string[] parts = normalized.Split('/');
        return parts.Length >= 3 && parts[0].StartsWith("1.", StringComparison.Ordinal) &&
            parts[0].Length > 2 && parts[0][2..].All(char.IsDigit) &&
            parts[1].Equals("Defs", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsPatchSourcePath(string relativePath)
    {
        string normalized = relativePath.Replace('\\', '/');
        if (normalized.StartsWith("Patches/", StringComparison.OrdinalIgnoreCase))
            return true;
        string[] parts = normalized.Split('/');
        return parts.Length >= 3 && parts[0].StartsWith("1.", StringComparison.Ordinal) &&
            parts[0].Length > 2 && parts[0][2..].All(char.IsDigit) &&
            parts[1].Equals("Patches", StringComparison.OrdinalIgnoreCase);
    }

    // These are the only Def kinds the direct runtime helper currently proves.
    // Never interpret a GenStepDef as a ThingDef or infer that the map step ran.
    internal sealed record TypedDefExpectation(string DefType, string DefName);
    internal sealed record BiomeGenStepExpectation(string BiomeDefName, string GenStepDefName);

    internal static IReadOnlyList<TypedDefExpectation> ExtractTypedDefExpectations(
        XmlDocument document, string relativePath)
    {
        if (!IsDefSourcePath(relativePath))
            return [];
        if (document.DocumentElement?.LocalName != "Defs")
            throw new InvalidDataException("Def XML must have a Defs root: " + relativePath);
        var result = new List<TypedDefExpectation>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (XmlNode element in document.DocumentElement.ChildNodes)
        {
            if (element.NodeType != XmlNodeType.Element ||
                element.LocalName is not ("ThingDef" or "GenStepDef"))
                continue;
            string name = element.SelectSingleNode("./defName")?.InnerText.Trim() ?? string.Empty;
            if (!IsSafeDefIdentifier(name))
                throw new InvalidDataException(
                    element.LocalName + " has an invalid or missing defName in " + relativePath);
            string key = element.LocalName + "|" + name;
            if (!seen.Add(key))
                throw new InvalidDataException("Duplicate typed Def in " + relativePath + ": " + key);
            result.Add(new TypedDefExpectation(element.LocalName, name));
        }
        return result;
    }

    internal static IReadOnlyList<BiomeGenStepExpectation> ExtractBiomeGenStepExpectations(
        XmlDocument document, string relativePath)
    {
        if (!IsPatchSourcePath(relativePath))
            return [];
        if (document.DocumentElement?.LocalName != "Patch")
            throw new InvalidDataException("Patch XML must have a Patch root: " + relativePath);
        var result = new List<BiomeGenStepExpectation>();
        XmlNodeList? operations = document.SelectNodes("/Patch/Operation");
        if (operations is null)
            return result;
        foreach (XmlNode operation in operations)
        {
            // This verifier supports the exact two-branch conditional add
            // used for extraGenSteps. Other patch shapes cannot be silently
            // mistaken for evidence that a biome contains a generation step.
            if (!operation.OuterXml.Contains("extraGenSteps", StringComparison.Ordinal))
                continue;
            const string prefix = "Defs/BiomeDef[defName=\"";
            const string listSuffix = "\"]/extraGenSteps";
            string selector = operation.SelectSingleNode("./xpath")?.InnerText.Trim() ?? string.Empty;
            if (operation.Attributes?["Class"]?.Value != "PatchOperationConditional" ||
                !selector.StartsWith(prefix, StringComparison.Ordinal) ||
                !selector.EndsWith(listSuffix, StringComparison.Ordinal))
                throw new InvalidDataException("Unsupported biome extraGenSteps patch: " + relativePath);
            string biome = selector[prefix.Length..^listSuffix.Length];
            if (!IsSafeDefIdentifier(biome))
                throw new InvalidDataException("Invalid biome selector: " + relativePath);
            string rootSelector = "Defs/BiomeDef[defName=\"" + biome + "\"]";
            XmlNode? match = operation.SelectSingleNode("./match");
            XmlNode? nomatch = operation.SelectSingleNode("./nomatch");
            if (match?.Attributes?["Class"]?.Value != "PatchOperationAdd" ||
                nomatch?.Attributes?["Class"]?.Value != "PatchOperationAdd" ||
                match.SelectSingleNode("./xpath")?.InnerText.Trim() != selector ||
                nomatch.SelectSingleNode("./xpath")?.InnerText.Trim() != rootSelector)
                throw new InvalidDataException("Incomplete conditional biome patch: " + relativePath);

            string[] existing = match.SelectNodes("./value/li")?.Cast<XmlNode>()
                .Select(node => node.InnerText.Trim()).ToArray() ?? [];
            string[] missing = nomatch.SelectNodes("./value/extraGenSteps/li")?.Cast<XmlNode>()
                .Select(node => node.InnerText.Trim()).ToArray() ?? [];
            if (existing.Length == 0 || existing.Any(name => !IsSafeDefIdentifier(name)) ||
                !existing.OrderBy(name => name, StringComparer.Ordinal)
                    .SequenceEqual(missing.OrderBy(name => name, StringComparer.Ordinal)))
                throw new InvalidDataException("Biome patch branches disagree: " + relativePath);
            foreach (string step in existing)
                result.Add(new BiomeGenStepExpectation(biome, step));
        }
        return result;
    }

    private static bool IsSafeDefIdentifier(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.');

    private static object BuildRimContext(State state)
    {
        long started = Stopwatch.GetTimestamp();
        string store = Path.Combine(Path.GetTempPath(), "rimtest-dev-check-rimctx-" + Guid.NewGuid().ToString("N"), "index.sqlite");
        state.StorePath = store;
        try
        {
            WorkspaceConfiguration configuration = WorkspaceConfiguration.Resolve(state.Workspace, store);
            IndexBuildResult index = new WorkspaceIndexer().Build(configuration, force: true);
            ImpactGraph graph = new ImpactGraphService().Build(new ImpactGraphBuildRequest(
                RootPath: state.Workspace,
                StorePath: store,
                Project: state.Mod.PackageId));
            ActualImpact actual = new ImpactGraphService().AnalyzeDiff(
                graph,
                state.ChangedFiles,
                state.Workspace);
            return new
            {
                status = "PASS",
                index = new { files = index.Counts.FileCount, entities = index.Counts.EntityCount, relations = index.Counts.RelationCount, durationMs = index.DurationMilliseconds, diagnostics = index.Diagnostics ?? [] },
                affected = new { changedNodes = actual.ChangedNodeIds, dependentNodes = actual.DirectDependents, impactClasses = actual.ImpactClasses, concerns = actual.ValidationConcerns }
            };
        }
        finally
        {
            state.StageTimes["rimContext"] = ElapsedMilliseconds(started);
        }
    }

    private static async Task<string> BuildHelperAsync(RimWorldGameLayout game, State state, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            string source = Path.Combine(AppContext.BaseDirectory, "DevCheckRuntime");
            string project = Path.Combine(source, "DirectValidationRuntimeSignal.csproj");
            string about = Path.Combine(source, "About", "About.xml");
            if (!File.Exists(project) || !File.Exists(about))
                throw new FileNotFoundException("The packaged runtime signal helper is missing.");
            string staging = Path.Combine(Path.GetTempPath(), "rimtest-dev-check-helper-" + Guid.NewGuid().ToString("N"));
            state.HelperStagingPath = staging;
            Directory.CreateDirectory(Path.Combine(staging, "About"));
            File.Copy(about, Path.Combine(staging, "About", "About.xml"));
            // The CLI's repo pins .NET 8 in global.json. Waylith's net10
            // helper must select its own SDK without changing that repo policy.
            // The generated selector exists only in this tracked temp directory.
            string helperWorkingDirectory = PrepareHelperBuildDirectory(game, source, staging);
            ProcessResult result = await RunProcessAsync(
                    "dotnet",
                    ["build", project, "--configuration", "Release", "--nologo", "-p:RimWorldRoot=" + game.Root, "-p:DevCheckWaylith=" + (game.IsWaylith ? "true" : "false"), "-p:DevCheckRuntimePackageRoot=" + staging + Path.DirectorySeparatorChar],
                    helperWorkingDirectory,
                    BuildTimeout,
                    state,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.ExitCode != 0 || result.TimedOut)
                throw new InvalidOperationException("Runtime signal helper build failed: " + Bound(result.Stdout + Environment.NewLine + result.Stderr));
            string assembly = Path.Combine(staging, game.ContentVersion, "Assemblies", "DirectValidationRuntimeSignal.dll");
            if (!File.Exists(assembly))
                throw new InvalidOperationException("Runtime signal helper build produced no assembly.");
            return staging;
        }
        finally
        {
            state.StageTimes["helperBuild"] = ElapsedMilliseconds(started);
        }
    }

    internal static string PrepareHelperBuildDirectory(
        RimWorldGameLayout game, string source, string staging)
    {
        if (!game.IsWaylith)
            return source;
        if (!Directory.Exists(staging))
            throw new DirectoryNotFoundException("Isolated helper build directory is missing.");
        // latestFeature stays on the .NET 10 release line, allowing 10.0.403
        // or a later stable 10.0 SDK without pinning to the unrelated .NET 8 CLI.
        const string selector =
            "{\"sdk\":{\"version\":\"10.0.100\",\"rollForward\":\"latestFeature\",\"allowPrerelease\":false}}";
        File.WriteAllText(
            Path.Combine(staging, "global.json"), selector, new UTF8Encoding(false));
        return staging;
    }

    private static RimWorldGameLayout ResolveRimWorldGameLayout()
    {
        string? configured = Environment.GetEnvironmentVariable("RIMWORLD_ROOT");
        string root = string.IsNullOrWhiteSpace(configured)
            ? @"C:\Games\Steam\steamapps\common\RimWorld"
            : configured;
        return DetectRimWorldGameLayout(root);
    }

    // Fail closed for mixed/partial engine installations rather than guessing
    // that any existing RimWorld directory is safe for isolated launch.
    internal static RimWorldGameLayout DetectRimWorldGameLayout(string root)
    {
        string full = Path.GetFullPath(root);
        if (!Directory.Exists(full) || !Directory.Exists(Path.Combine(full, "Data")) ||
            !Directory.Exists(Path.Combine(full, "Mods")))
            throw new DirectoryNotFoundException("RimWorld installation, Data or Mods directory is missing: " + full);

        string unityExecutable = Path.Combine(full, "RimWorldWin64.exe");
        string unityManaged = Path.Combine(full, "RimWorldWin64_Data", "Managed");
        string waylithExecutable = Path.Combine(full, "RimWorld.exe");
        bool unityExe = File.Exists(unityExecutable);
        bool unityAssembly = File.Exists(Path.Combine(unityManaged, "Assembly-CSharp.dll"));
        bool waylithExe = File.Exists(waylithExecutable);
        bool waylithAssembly = File.Exists(Path.Combine(full, "Assembly-CSharp.dll"));
        bool waylithEngine = File.Exists(Path.Combine(full, "Waylith.dll"));
        bool unityStubs = File.Exists(Path.Combine(full, "UnityStubs.dll"));
        bool anyUnity = unityExe || unityAssembly;
        bool anyWaylith = waylithExe || waylithAssembly || waylithEngine || unityStubs;

        if (anyUnity && anyWaylith)
            throw new InvalidDataException("Ambiguous RimWorld Unity/Waylith installation: " + full);
        if (unityExe && unityAssembly)
            return new RimWorldGameLayout(full, unityExecutable, "1.6");
        if (waylithExe && waylithAssembly && waylithEngine && unityStubs)
            return new RimWorldGameLayout(full, waylithExecutable, "1.7");
        throw new InvalidDataException(
            "Incomplete RimWorld game layout. Expected 1.6 RimWorldWin64.exe and Managed/Assembly-CSharp.dll, " +
            "or 1.7 RimWorld.exe and root Assembly-CSharp.dll, Waylith.dll and UnityStubs.dll: " + full);
    }

    private static string? ResolveTargetAssembly(string workspace, string project)
    {
        string assemblyName = Path.GetFileNameWithoutExtension(project);
        try
        {
            XDocument document = XDocument.Load(project);
            string configured = ElementText(document, "AssemblyName");
            if (!string.IsNullOrWhiteSpace(configured))
                assemblyName = configured;
        }
        catch (XmlException)
        {
        }
        string projectDirectory = Path.GetDirectoryName(project)!;
        string[] candidates =
        [
            Path.Combine(workspace, "1.6", "Assemblies", assemblyName + ".dll"),
            Path.Combine(workspace, "1.7", "Assemblies", assemblyName + ".dll"),
            Path.Combine(projectDirectory, "bin", "Release", "net10.0", assemblyName + ".dll"),
            Path.Combine(projectDirectory, "bin", "Release", "net472", assemblyName + ".dll"),
            Path.Combine(projectDirectory, "bin", "Release", "net48", assemblyName + ".dll"),
            Path.Combine(projectDirectory, "bin", "Release", "netstandard2.0", assemblyName + ".dll"),
            Path.Combine(projectDirectory, "bin", "Release", "net8.0", assemblyName + ".dll")
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    internal static string TargetAssemblyContentVersion(RimWorldGameLayout game, string workspace, string assembly)
    {
        string relative = Path.GetRelativePath(workspace, assembly).Replace('\\', '/');
        if (relative.StartsWith("1.6/Assemblies/", StringComparison.OrdinalIgnoreCase))
            return "1.6";
        if (relative.StartsWith("1.7/Assemblies/", StringComparison.OrdinalIgnoreCase))
            return "1.7";
        return game.ContentVersion;
    }

    // Build launch parameters without starting a game: tests can verify that
    // every version uses ephemeral savedata and an explicit, bounded mod set.
    internal static ProcessStartInfo CreateRuntimeStartInfo(
        RimWorldGameLayout game,
        string savedata,
        string logPath,
        IReadOnlyList<string> activePackages)
    {
        var start = new ProcessStartInfo(game.ExecutablePath)
        {
            WorkingDirectory = game.Root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-savedatafolder=" + savedata);
        if (game.IsWaylith)
        {
            // Waylith's -mod= argument includes base game and installed DLCs
            // automatically. Do not use the player's global mod selection.
            string[] selected = activePackages
                .Where(id => !id.Equals("ludeon.rimworld", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (selected.Length < 2 || !selected.Contains(HelperPackageId, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("Waylith isolated launch requires the helper and a target mod.");
            start.ArgumentList.Add("-mod=" + string.Join(";", selected));
            start.ArgumentList.Add("-Headless");
        }
        else
        {
            start.ArgumentList.Add("-logfile");
            start.ArgumentList.Add(logPath);
        }
        return start;
    }

    internal static bool IsWaylithTargetSelected(ProcessStartInfo start, string packageId) =>
        start.ArgumentList.Where(arg => arg.StartsWith("-mod=", StringComparison.OrdinalIgnoreCase))
            .SelectMany(arg => arg[5..].Split(';', StringSplitOptions.RemoveEmptyEntries))
            .Any(id => id.Equals(packageId, StringComparison.OrdinalIgnoreCase));

    private const long MaximumWaylithLogBytes = 4L * 1024 * 1024;
    private const int MaximumWaylithHeaderCharacters = 8_192;

    internal sealed record WaylithLogSnapshot(
        string Path, bool Existed, long Length, DateTime LastWriteUtc);

    internal static string ResolveWaylithLogPath()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Waylith LocalLow log custody requires Windows.");
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
            throw new DirectoryNotFoundException("Windows LocalAppData is unavailable.");
        string appData = Path.GetDirectoryName(Path.GetFullPath(local)) ??
            throw new DirectoryNotFoundException("Cannot determine the Windows AppData root.");
        return Path.Combine(appData, "LocalLow", "Ludeon Studios",
            "RimWorld by Ludeon Studios", "Logs", "Player.log");
    }

    internal static WaylithLogSnapshot CaptureWaylithLogSnapshot(string path)
    {
        var info = new FileInfo(path);
        info.Refresh();
        return info.Exists
            ? new WaylithLogSnapshot(path, true, info.Length, info.LastWriteTimeUtc)
            : new WaylithLogSnapshot(path, false, 0L, DateTime.MinValue);
    }

    // Inspect the run's global Waylith log without modifying or rotating it.
    // A matching startup signal alone does not prove that the log belongs to
    // this child process: the game ignores -savedatafolder for Player.log.
    internal static bool TryReadVerifiedWaylithLog(
        WaylithLogSnapshot before,
        RimWorldGameLayout game,
        ProcessStartInfo start,
        DateTimeOffset childStartedUtc,
        DateTimeOffset childExitedUtc,
        out string log,
        out string reason)
    {
        log = string.Empty;
        reason = "WAYLITH_LOG_MISSING";
        try
        {
            var info = new FileInfo(before.Path);
            info.Refresh();
            if (!info.Exists)
                return false;
            if (info.Length <= 0 || info.Length > MaximumWaylithLogBytes)
            {
                reason = "WAYLITH_LOG_SIZE_UNSUPPORTED";
                return false;
            }
            if (before.Existed &&
                info.Length == before.Length &&
                info.LastWriteTimeUtc <= before.LastWriteUtc)
            {
                reason = "WAYLITH_LOG_UNCHANGED";
                return false;
            }
            if (info.LastWriteTimeUtc < childStartedUtc.UtcDateTime.AddSeconds(-2))
            {
                reason = "WAYLITH_LOG_STALE";
                return false;
            }

            string content;
            long readLength;
            DateTime readLastWriteUtc = info.LastWriteTimeUtc;
            using (var stream = new FileStream(before.Path, FileMode.Open,
                       FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8,
                       detectEncodingFromByteOrderMarks: true))
            {
                if (stream.Length > MaximumWaylithLogBytes)
                {
                    reason = "WAYLITH_LOG_SIZE_UNSUPPORTED";
                    return false;
                }
                readLength = stream.Length;
                content = reader.ReadToEnd();
            }
            info.Refresh();
            if (!info.Exists || info.Length > MaximumWaylithLogBytes ||
                info.Length != readLength || info.LastWriteTimeUtc != readLastWriteUtc)
            {
                // A changing log is not attributable evidence. Never modify
                // the normal user log, even if another game starts during us.
                reason = "WAYLITH_LOG_UNSTABLE";
                return false;
            }
            if (!VerifyWaylithLogHeader(
                    content, before.Path, game, start, childStartedUtc,
                    childExitedUtc, out reason))
                return false;
            log = content;
            reason = "WAYLITH_LOG_VERIFIED";
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            reason = "WAYLITH_LOG_UNREADABLE";
            return false;
        }
    }

    internal static bool VerifyWaylithLogHeader(
        string content,
        string logPath,
        RimWorldGameLayout game,
        ProcessStartInfo start,
        DateTimeOffset childStartedUtc,
        DateTimeOffset childExitedUtc,
        out string reason)
    {
        reason = "WAYLITH_LOG_PROVENANCE_UNVERIFIED";
        if (content.Length == 0)
            return false;
        string header = content[..Math.Min(content.Length, MaximumWaylithHeaderCharacters)];
        string[] lines = header.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        string? Value(string prefix) => lines.Select(line => line.Trim())
            .Where(line => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(line => line[prefix.Length..].Trim())
            .FirstOrDefault();

        string? started = Value("Started:");
        string? version = Value("Version:");
        string? loggedPath = Value("Log file:");
        string? command = Value("Command:");
        if (!DateTimeOffset.TryParse(started,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AllowWhiteSpaces,
                out DateTimeOffset logStarted))
            return false;
        if (logStarted < childStartedUtc.AddSeconds(-2) ||
            logStarted > childExitedUtc.AddSeconds(2) ||
            string.IsNullOrWhiteSpace(version) ||
            !version.StartsWith("1.7.", StringComparison.Ordinal) ||
            !string.Equals(loggedPath, logPath, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(command) ||
            !command.Contains(Path.Combine(game.Root, "RimWorld.dll"),
                StringComparison.OrdinalIgnoreCase))
            return false;

        string? savedataArg = start.ArgumentList.SingleOrDefault(
            arg => arg.StartsWith("-savedatafolder=", StringComparison.OrdinalIgnoreCase));
        string? modArg = start.ArgumentList.SingleOrDefault(
            arg => arg.StartsWith("-mod=", StringComparison.OrdinalIgnoreCase));
        if (savedataArg is null || modArg is null ||
            !HasExactLogArgument(header, savedataArg) ||
            !HasExactLogArgument(header, modArg) ||
            !HasExactLogArgument(header, "-Headless"))
            return false;
        reason = "WAYLITH_LOG_VERIFIED";
        return true;
    }

    private static bool HasExactLogArgument(string text, string value)
    {
        for (int index = text.IndexOf(value, StringComparison.OrdinalIgnoreCase);
             index >= 0;
             index = text.IndexOf(value, index + 1, StringComparison.OrdinalIgnoreCase))
        {
            int end = index + value.Length;
            bool before = index == 0 || char.IsWhiteSpace(text[index - 1]) ||
                text[index - 1] == '"';
            bool after = end == text.Length || char.IsWhiteSpace(text[end]) ||
                text[end] == '"';
            if (before && after)
                return true;
        }
        return false;
    }

    private static async Task<RuntimeResult> RunRuntimeAsync(
        RimWorldGameLayout game,
        State state,
        CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        string rimWorldRoot = game.Root;
        string modsRoot = Path.Combine(rimWorldRoot, "Mods");
        if (game.IsWaylith)
        {
            // The Waylith log is global even when save-data is isolated.
            // Refuse to race a preexisting game session for that log.
            Process[] otherGames = Process.GetProcessesByName("RimWorld");
            try
            {
                if (otherGames.Length > 0)
                    throw new InvalidOperationException(
                        "WAYLITH_LOG_CONCURRENT_GAME: Close RimWorld before isolated dev-check.");
            }
            finally
            {
                foreach (Process other in otherGames)
                    other.Dispose();
            }
        }
        if (IsWithin(modsRoot, state.Workspace))
            throw new InvalidDataException("The target workspace cannot be inside the active RimWorld Mods directory.");
        string deployment = Path.Combine(modsRoot, ".rimtest-dev-check-" + Guid.NewGuid().ToString("N"));
        string helperDeployment = Path.Combine(modsRoot, ".rimtest-dev-check-helper-" + Guid.NewGuid().ToString("N"));
        string savedata = Path.Combine(Path.GetTempPath(), "rimtest-dev-check-savedata-" + Guid.NewGuid().ToString("N"));
        state.DeploymentPath = deployment;
        state.HelperDeploymentPath = helperDeployment;
        state.SavedataPath = savedata;
        Process? process = null;
        try
        {
            CopyRuntimePayload(state.Workspace, deployment);
            foreach (string source in state.TargetAssemblySources)
            {
                // Keep already-versioned 1.6/1.7 content at its declared
                // version. Only an unversioned build output adopts the
                // installed game's version: never duplicate both versions.
                string contentVersion = TargetAssemblyContentVersion(game, state.Workspace, source);
                string targetAssemblyDirectory = Path.Combine(deployment, contentVersion, "Assemblies");
                string target = Path.Combine(targetAssemblyDirectory, Path.GetFileName(source));
                if (!File.Exists(target))
                {
                    Directory.CreateDirectory(targetAssemblyDirectory);
                    File.Copy(source, target);
                }
            }
            IReadOnlyList<RuntimeModPackage> dependencies = ResolveRequiredRuntimeDependencies(
                Path.Combine(state.Workspace, "About", "About.xml"),
                DiscoverRuntimeModRoots(rimWorldRoot));
            foreach (RuntimeModPackage dependency in dependencies)
            {
                string dependencyDeployment = Path.Combine(
                    modsRoot,
                    ".rimtest-dev-check-dependency-" + Guid.NewGuid().ToString("N"));
                CopyDirectory(dependency.RootPath, dependencyDeployment);
                state.DependencyDeploymentPaths.Add(dependencyDeployment);
            }
            CopyDirectory(state.HelperPackagePath!, helperDeployment);
            string configDirectory = Path.Combine(savedata, "Config");
            Directory.CreateDirectory(configDirectory);
            string configPath = Path.Combine(configDirectory, "ModsConfig.xml");
            IReadOnlyList<string> activePackages = BuildActiveModPackageIds(state.Mod.PackageId, dependencies);
            WriteModsConfig(configPath, activePackages);
            string signalPath = Path.Combine(savedata, "direct-validation-signal.json");
            string behaviorPath = Path.Combine(savedata, "direct-validation-behavior.json");
            string logPath = game.IsWaylith
                ? ResolveWaylithLogPath()
                : Path.Combine(savedata, "rimworld-direct-validation.log");
            WaylithLogSnapshot? priorGlobalLog = game.IsWaylith
                ? CaptureWaylithLogSnapshot(logPath)
                : null;
            ProcessStartInfo start = CreateRuntimeStartInfo(game, savedata, logPath, activePackages);
            start.Environment["DIRECT_VALIDATION_TARGET_PACKAGE"] = state.Mod.PackageId;
            start.Environment["DIRECT_VALIDATION_TARGET_DEFS"] = string.Join(';', state.ChangedDefs);
            start.Environment["DIRECT_VALIDATION_TARGET_TYPED_DEFS"] = string.Join(
                ';', state.ChangedTypedDefs.Select(def => def.DefType + "|" + def.DefName));
            start.Environment["DIRECT_VALIDATION_EXPECTED_BIOME_GENSTEPS"] = string.Join(
                ';', state.ExpectedBiomeGenSteps.Select(step => step.BiomeDefName + "|" + step.GenStepDefName));
            start.Environment["DIRECT_VALIDATION_TARGET_ASSEMBLIES"] = string.Join(
                ';',
                state.TargetAssemblySources.Select(source => Path.GetFileNameWithoutExtension(source)));
            SetRuntimeSignalEnvironment(start, signalPath);
            process = new Process { StartInfo = start };
            state.LaunchedProcess = process;
            state.RuntimeProcessExitConfirmed = false;
            DateTimeOffset childStartedUtc = DateTimeOffset.UtcNow;
            if (!process.Start())
                throw new InvalidOperationException("RimWorld process did not start.");
            state.Processes.Add(Path.GetFileName(game.ExecutablePath) ?? game.ExecutablePath);
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            DateTime deadline = DateTime.UtcNow + RuntimeTimeoutForGame(game);
            JsonElement? signal = null;
            while (DateTime.UtcNow < deadline && !process.HasExited)
            {
                if (File.Exists(signalPath))
                {
                    try
                    {
                        using JsonDocument document = JsonDocument.Parse(ReadSharedText(signalPath));
                        JsonElement candidate = document.RootElement.Clone();
                        if (candidate.TryGetProperty("protocol", out JsonElement protocol) && protocol.GetString() == "direct-validation-signal/v1")
                        {
                            signal = candidate;
                            if (candidate.TryGetProperty("completion", out JsonElement completion) && completion.GetString() == "content-loaded")
                                break;
                        }
                    }
                    catch (JsonException)
                    {
                    }
                }
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
            ProcessLifecycleResult completed = await CompleteProcessAsync(
                    process,
                    stdoutTask,
                    stderrTask,
                    terminateIfRunning: true,
                    cancellationToken)
                .ConfigureAwait(false);
            DateTimeOffset childExitedUtc = DateTimeOffset.UtcNow;
            state.RuntimeProcessExitConfirmed = true;
            state.LaunchedProcess = null;
            process.Dispose();
            process = null;
            string log;
            bool logVerified;
            string logFailure = "WAYLITH_LOG_NOT_APPLICABLE";
            if (game.IsWaylith)
            {
                logVerified = TryReadVerifiedWaylithLog(
                    priorGlobalLog!, game, start, childStartedUtc, childExitedUtc,
                    out log, out logFailure);
            }
            else
            {
                logVerified = File.Exists(logPath);
                log = logVerified ? ReadRuntimeLog(logPath) : string.Empty;
            }
            state.RelevantErrors.AddRange(FindTargetErrors(log, state));
            bool targetConfigured = game.IsWaylith
                ? IsWaylithTargetSelected(start, state.Mod.PackageId)
                : IsTargetConfigured(configPath, state.Mod.PackageId);
            RuntimeResult result = ParseRuntime(
                signal,
                state,
                behaviorPath,
                targetConfigured,
                completed.ControlledTermination,
                completed.ExitCode);
            // On Waylith the log is global. Require exact fresh invocation
            // custody before trusting the result; do not treat existence of
            // a preexisting Player.log as proof of an isolated test.
            if (game.IsWaylith && !logVerified)
                result = result with { Status = "RUNTIME_LOAD_FAIL", Reason = logFailure };
            if (game.IsWaylith)
                result = result with
                {
                    Evidence = [.. result.Evidence,
                        "waylithLogVerified=" + logVerified,
                        "waylithLogStatus=" + logFailure]
                };
            return result with
            {
                RuntimeSeconds = Math.Round(ElapsedMilliseconds(started) / 1000d, 3),
                LogProduced = logVerified
            };
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                    if (process.WaitForExit(5_000))
                        state.RuntimeProcessExitConfirmed = true;
                    else
                        state.CleanupErrors.Add("RimWorld process did not exit within the bounded termination window.");
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
                {
                    state.CleanupErrors.Add("RimWorld process: " + Bound(exception.Message));
                }
                finally
                {
                    state.LaunchedProcess = null;
                    process.Dispose();
                }
            }
            state.StageTimes["runtime"] = ElapsedMilliseconds(started);
        }
    }

    internal static async Task<ProcessLifecycleResult> CompleteProcessAsync(
        Process process,
        Task<string> stdoutTask,
        Task<string> stderrTask,
        bool terminateIfRunning,
        CancellationToken cancellationToken)
    {
        bool controlledTermination = false;
        if (!process.HasExited && terminateIfRunning)
        {
            controlledTermination = true;
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        if (!process.HasExited)
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return new ProcessLifecycleResult(process.ExitCode, controlledTermination);
    }
    internal static void SetRuntimeSignalEnvironment(ProcessStartInfo start, string signalPath)
    {
        start.Environment["DIRECT_VALIDATION_SIGNAL_RESULT"] = signalPath;
    }

    // Never accept an untyped or missing signal for a requested GenStepDef.
    // One positive response must exist for each exact type/name pair and each
    // biome-generation-step membership assertion.
    internal static bool VerifyRuntimeDefinitionSignal(
        JsonElement signal,
        IReadOnlyList<TypedDefExpectation> expectedDefs,
        IReadOnlyList<BiomeGenStepExpectation> expectedBiomeSteps)
    {
        if (signal.ValueKind != JsonValueKind.Object)
            return false;
        if (expectedDefs.Count > 0)
        {
            if (!signal.TryGetProperty("requestedDefs", out JsonElement defs) ||
                defs.ValueKind != JsonValueKind.Array)
                return false;
            foreach (TypedDefExpectation expected in expectedDefs)
            {
                JsonElement[] matches = defs.EnumerateArray().Where(item =>
                    item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty("defName", out JsonElement name) &&
                    name.ValueKind == JsonValueKind.String &&
                    name.GetString() == expected.DefName &&
                    item.TryGetProperty("defType", out JsonElement type) &&
                    type.ValueKind == JsonValueKind.String &&
                    type.GetString() == expected.DefType).ToArray();
                if (matches.Length != 1 ||
                    !matches[0].TryGetProperty("exists", out JsonElement exists) ||
                    exists.ValueKind != JsonValueKind.True)
                    return false;
            }
        }
        if (expectedBiomeSteps.Count > 0)
        {
            if (!signal.TryGetProperty("biomeGenSteps", out JsonElement biomeSteps) ||
                biomeSteps.ValueKind != JsonValueKind.Array)
                return false;
            foreach (BiomeGenStepExpectation expected in expectedBiomeSteps)
            {
                JsonElement[] matches = biomeSteps.EnumerateArray().Where(item =>
                    item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty("biomeDefName", out JsonElement biomeName) &&
                    biomeName.ValueKind == JsonValueKind.String &&
                    biomeName.GetString() == expected.BiomeDefName &&
                    item.TryGetProperty("genStepDefName", out JsonElement genStepName) &&
                    genStepName.ValueKind == JsonValueKind.String &&
                    genStepName.GetString() == expected.GenStepDefName).ToArray();
                if (matches.Length != 1 ||
                    !matches[0].TryGetProperty("exists", out JsonElement exists) ||
                    exists.ValueKind != JsonValueKind.True)
                    return false;
            }
        }
        return true;
    }

    private static RuntimeResult ParseRuntime(JsonElement? signal, State state, string behaviorPath, bool targetConfigured, bool controlledTermination, int? exitCode)
    {
        bool contentLoaded = false;
        bool targetLoaded = false;
        bool defsValid = state.ChangedTypedDefs.Count == 0 &&
            state.ExpectedBiomeGenSteps.Count == 0;
        bool biomeStepsValid = state.ExpectedBiomeGenSteps.Count == 0;
        bool configured = targetConfigured;
        var assemblyResults = state.TargetAssemblySources
            .Select(source => new AssemblyLoadResult(Path.GetFileNameWithoutExtension(source), false))
            .ToList();
        bool? assemblyLoaded = assemblyResults.Count == 0 ? null : false;
        string? reason = null;
        if (signal.HasValue)
        {
            JsonElement value = signal.Value;
            contentLoaded = value.TryGetProperty("completion", out JsonElement completion) && completion.GetString() == "content-loaded";
            targetLoaded = value.TryGetProperty("targetModLoaded", out JsonElement target) && target.GetBoolean();
            defsValid = VerifyRuntimeDefinitionSignal(
                value, state.ChangedTypedDefs, state.ExpectedBiomeGenSteps);
            if (state.ExpectedBiomeGenSteps.Count > 0)
                biomeStepsValid = VerifyRuntimeDefinitionSignal(
                    value, [], state.ExpectedBiomeGenSteps);
            if (value.TryGetProperty("targetAssembliesLoaded", out JsonElement loadedAssemblies))
            {
                foreach (JsonElement loaded in loadedAssemblies.EnumerateArray())
                {
                    string? name = loaded.TryGetProperty("name", out JsonElement nameValue)
                        ? nameValue.GetString()
                        : null;
                    int index = assemblyResults.FindIndex(
                        result => string.Equals(result.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (index >= 0 &&
                        loaded.TryGetProperty("loaded", out JsonElement loadedValue))
                    {
                        assemblyResults[index] = assemblyResults[index] with { Loaded = loadedValue.GetBoolean() };
                    }
                }
            }
            else if (assemblyResults.Count == 1 &&
                     value.TryGetProperty("targetAssemblyLoaded", out JsonElement loaded))
            {
                assemblyResults[0] = assemblyResults[0] with { Loaded = loaded.GetBoolean() };
            }
            if (assemblyResults.Count > 0)
                assemblyLoaded = assemblyResults.All(result => result.Loaded);
        }
        object? behavior = null;
        if (File.Exists(behaviorPath))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(ReadSharedText(behaviorPath));
                JsonElement candidate = document.RootElement.Clone();
                if (candidate.TryGetProperty("protocol", out JsonElement protocol) && protocol.GetString() == BehaviorProtocol &&
                    candidate.TryGetProperty("event", out JsonElement eventName) && eventName.GetString() == "startup-executed")
                    behavior = candidate;
            }
            catch (JsonException)
            {
            }
        }
        string[] evidence =
        [
            "signalObserved=" + signal.HasValue,
            "targetModConfigured=" + configured,
            "targetModLoaded=" + targetLoaded,
            "defsValid=" + defsValid,
            "typedDefsExpected=" + state.ChangedTypedDefs.Count,
            "biomeGenStepsExpected=" + state.ExpectedBiomeGenSteps.Count,
            "biomeGenStepsValid=" + biomeStepsValid,
            "targetAssembliesLoaded=" + (assemblyLoaded?.ToString() ?? "not-applicable"),
            "startupBehaviorObserved=" + (behavior is not null)
        ];
        string status;
        if (!contentLoaded)
        {
            status = "RUNTIME_LOAD_FAIL";
            reason = "CONTENT_LOAD_SIGNAL_NOT_OBSERVED";
        }
        else if (!configured || !targetLoaded || !defsValid || assemblyLoaded == false)
        {
            status = "RUNTIME_LOAD_FAIL";
            reason = "TARGET_LOAD_NOT_PROVEN";
        }
        else if (state.RelevantErrors.Count > 0)
        {
            status = "RUNTIME_LOAD_FAIL";
            reason = "TARGET_CONTENT_ERRORS";
        }
        else if (exitCode is not null && exitCode != 0 && !controlledTermination)
        {
            status = "RUNTIME_LOAD_FAIL";
            reason = "UNEXPECTED_RIMWORLD_EXIT";
        }
        else
        {
            status = "RUNTIME_LOAD_PASS";
            reason = "EXPLICIT_RUNTIME_PROOF";
        }
        return new RuntimeResult(status, reason, contentLoaded, targetLoaded, configured, defsValid, assemblyLoaded, assemblyResults, behavior, evidence, state.RelevantErrors.ToArray(), null, false);
    }

    private static IEnumerable<string> FindTargetErrors(string log, State state)
    {
        if (string.IsNullOrWhiteSpace(log))
            yield break;
        string[] tokens = [state.Mod.PackageId, state.Mod.Name, .. state.ChangedDefs, .. state.TargetErrorTokens, .. state.ChangedFiles.Select(path => Path.GetFileName(path) ?? string.Empty)];
        foreach (string line in log.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries))
        {
            bool error = line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("exception", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Could not resolve cross-reference", StringComparison.OrdinalIgnoreCase);
            if (error && tokens.Any(token => !string.IsNullOrWhiteSpace(token) && line.Contains(token, StringComparison.OrdinalIgnoreCase)))
                yield return Bound(line.Trim());
        }
    }

    internal static IReadOnlyList<string> DiscoverRuntimeModRoots(string rimWorldRoot)
    {
        var roots = new List<string>
        {
            Path.Combine(rimWorldRoot, "Data"),
            Path.Combine(rimWorldRoot, "Mods")
        };
        string? configuredWorkshopRoot = Environment.GetEnvironmentVariable("RIMWORLD_WORKSHOP_ROOT");
        if (string.IsNullOrWhiteSpace(configuredWorkshopRoot))
            configuredWorkshopRoot = Environment.GetEnvironmentVariable("RIMWORLD_WORKSHOP_PATH");
        if (string.IsNullOrWhiteSpace(configuredWorkshopRoot))
        {
            DirectoryInfo? steamApps = Directory.GetParent(rimWorldRoot)?.Parent;
            if (steamApps is not null)
                configuredWorkshopRoot = Path.Combine(steamApps.FullName, "workshop", "content", "294100");
        }
        if (!string.IsNullOrWhiteSpace(configuredWorkshopRoot))
            roots.Add(configuredWorkshopRoot);
        return roots
            .Where(Directory.Exists)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    internal static IReadOnlyList<RuntimeModPackage> ResolveRequiredRuntimeDependencies(
        string targetAboutPath,
        IReadOnlyList<string> runtimeRoots)
    {
        RuntimeModPackage target = ReadRuntimeModPackage(
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(targetAboutPath)!, "..")));
        var installed = new List<RuntimeModPackage>();
        foreach (string root in runtimeRoots)
        {
            try
            {
                foreach (string packageRoot in EnumerateRuntimeModPackageRoots(root))
                {
                    try
                    {
                        installed.Add(ReadRuntimeModPackage(packageRoot));
                    }
                    catch (Exception exception) when (exception is InvalidDataException or XmlException)
                    {
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolved = new List<RuntimeModPackage>();
        foreach (string dependency in target.RequiredDependencies)
            Visit(dependency);
        return resolved;

        void Visit(string packageId)
        {
            if (!visited.Add(packageId))
                return;
            RuntimeModPackage[] matches = installed
                .Where(package => string.Equals(package.PackageId, packageId, StringComparison.OrdinalIgnoreCase))
                .GroupBy(package => package.RootPath, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
            if (matches.Length == 0)
                throw new InvalidDataException("REQUIRED_MOD_DEPENDENCY_MISSING: " + packageId);
            if (matches.Length > 1)
                throw new InvalidDataException("REQUIRED_MOD_DEPENDENCY_AMBIGUOUS: " + packageId);
            if (!visiting.Add(packageId))
                return;
            foreach (string nested in matches[0].RequiredDependencies)
                Visit(nested);
            visiting.Remove(packageId);
            resolved.Add(matches[0]);
        }
    }

    internal static IReadOnlyList<string> BuildActiveModPackageIds(
        string targetPackageId,
        IReadOnlyList<RuntimeModPackage> dependencies)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string packageId in new[] { "ludeon.rimworld" }
                     .Concat(dependencies.Select(package => package.PackageId))
                     .Concat([HelperPackageId, targetPackageId]))
        {
            if (seen.Add(packageId))
                result.Add(packageId);
        }
        return result;
    }

    private static IEnumerable<string> EnumerateRuntimeModPackageRoots(string runtimeRoot)
    {
        foreach (string packageRoot in Directory.EnumerateDirectories(runtimeRoot))
        {
            if (File.Exists(Path.Combine(packageRoot, "About", "About.xml")))
                yield return packageRoot;
        }
    }

    private static RuntimeModPackage ReadRuntimeModPackage(string packageRoot)
    {
        string aboutPath = Path.Combine(packageRoot, "About", "About.xml");
        if (!File.Exists(aboutPath))
            throw new InvalidDataException("Runtime mod package metadata is missing: " + aboutPath);
        XDocument document = XDocument.Load(aboutPath, LoadOptions.SetLineInfo);
        string packageId = RootElementText(document, "packageId");
        if (string.IsNullOrWhiteSpace(packageId) ||
            !packageId.All(static c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-'))
            throw new InvalidDataException("Runtime mod packageId is invalid: " + aboutPath);
        IReadOnlyList<string> dependencies = document.Root?
            .Elements()
            .FirstOrDefault(element => element.Name.LocalName.Equals("modDependencies", StringComparison.OrdinalIgnoreCase))?
            .Elements()
            .Where(element => element.Name.LocalName.Equals("li", StringComparison.OrdinalIgnoreCase))
            .Select(element => element.Elements()
                .FirstOrDefault(child => child.Name.LocalName.Equals("packageId", StringComparison.OrdinalIgnoreCase))
                ?.Value.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value, StringComparer.Ordinal)
            .ToArray() ?? [];
        return new RuntimeModPackage(packageId, Path.GetFullPath(packageRoot), dependencies);
    }

    private static void WriteModsConfig(string path, IReadOnlyList<string> packageIds)
    {
        var document = new XmlDocument();
        XmlElement root = document.CreateElement("ModsConfigData");
        document.AppendChild(root);
        XmlElement active = document.CreateElement("activeMods");
        root.AppendChild(active);
        foreach (string package in packageIds)
        {
            XmlElement item = document.CreateElement("li");
            item.InnerText = package;
            active.AppendChild(item);
        }
        document.Save(path);
    }
    private static bool IsTargetConfigured(string path, string packageId)
    {
        var document = new XmlDocument();
        document.Load(path);
        return document.SelectNodes("/ModsConfigData/activeMods/li")?.Cast<XmlNode>()
            .Any(node => node.InnerText.Trim() == packageId) == true;
    }


    private static string ReadSharedText(string path)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try { return File.ReadAllText(path); }
            catch (IOException) when (attempt < 9) { Thread.Sleep(20); }
        }
        return File.ReadAllText(path);
    }

    private static string ReadBoundedText(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        char[] buffer = new char[MaximumDiagnosticLength];
        int count = reader.ReadBlock(buffer, 0, buffer.Length);
        return new string(buffer, 0, count);
    }
    private static string ReadRuntimeLog(string path)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                return ReadBoundedText(path);
            }
            catch (IOException) when (attempt < 9)
            {
                Thread.Sleep(50);
            }
        }
        return ReadBoundedText(path);
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        State state,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        if (environment is not null)
        {
            foreach ((string key, string value) in environment)
                start.Environment[key] = value;
        }
        using var process = new Process { StartInfo = start };
        long started = Stopwatch.GetTimestamp();
        if (!process.Start())
            throw new InvalidOperationException("Process did not start: " + fileName);
        state.Processes.Add(fileName);
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);
        state.StageTimes[fileName] = ElapsedMilliseconds(started);
        return new ProcessResult(process.HasExited ? process.ExitCode : -1, timedOut, stdout, stderr);
    }

    internal static void CopyRuntimePayload(string source, string destination)
    {
        string about = Path.Combine(source, "About");
        if (!Directory.Exists(about) || !File.Exists(Path.Combine(about, "About.xml")))
            throw new InvalidDataException("The workspace has no explicit About/About.xml runtime payload.");

        CopyDirectory(about, Path.Combine(destination, "About"));
        string loadFolders = Path.Combine(source, "LoadFolders.xml");
        if (File.Exists(loadFolders))
        {
            Directory.CreateDirectory(destination);
            File.Copy(loadFolders, Path.Combine(destination, "LoadFolders.xml"), overwrite: true);
        }
        foreach (string directory in Directory.EnumerateDirectories(source, "1.*", SearchOption.TopDirectoryOnly)
                     .Where(path => IsVersionedRuntimeDirectory(Path.GetFileName(path))))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    private static bool IsVersionedRuntimeDirectory(string name) =>
        name.StartsWith("1.", StringComparison.Ordinal) &&
        name.Length > 2 &&
        name[2..].All(char.IsDigit);

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            string target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void Cleanup(State state)
    {
        bool processExitConfirmed = state.RuntimeProcessExitConfirmed;
        if (state.LaunchedProcess is not null)
        {
            try
            {
                if (!state.LaunchedProcess.HasExited)
                    state.LaunchedProcess.Kill(entireProcessTree: true);
                processExitConfirmed = state.LaunchedProcess.WaitForExit(5_000);
                state.RuntimeProcessExitConfirmed = processExitConfirmed;
                if (!processExitConfirmed)
                    state.CleanupErrors.Add("RimWorld process did not exit within the bounded termination window.");
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
            {
                processExitConfirmed = false;
                state.RuntimeProcessExitConfirmed = false;
                state.CleanupErrors.Add("RimWorld process: " + Bound(exception.Message));
            }
            finally
            {
                state.LaunchedProcess.Dispose();
                state.LaunchedProcess = null;
            }
        }
        long started = Stopwatch.GetTimestamp();
        if (!processExitConfirmed)
        {
            state.CleanupErrors.Add("Temporary cleanup skipped because RimWorld process exit was not confirmed.");
        }
        else
        {
            var temporaryPaths = new List<(string? Path, string Label)>
            {
                (state.DeploymentPath, "target deployment"),
                (state.HelperDeploymentPath, "runtime helper deployment"),
                (state.SavedataPath, "isolated save-data"),
                (state.HelperStagingPath, "helper staging"),
                (state.StorePath is null ? null : Path.GetDirectoryName(state.StorePath), "RimContext store")
            };
            temporaryPaths.AddRange(state.DependencyDeploymentPaths.Select(path => ((string?)path, "required dependency deployment")));
            foreach ((string? path, string label) in temporaryPaths)
            {
                if (string.IsNullOrWhiteSpace(path))
                    continue;
                try
                {
                    DeleteTemporaryPath(path);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    state.CleanupErrors.Add(label + ": " + Bound(exception.Message));
                }
            }
        }
        state.CleanupStatus = state.CleanupErrors.Count == 0 ? "CLEANUP_PASS" : "CLEANUP_FAIL";
        state.StageTimes["cleanup"] = ElapsedMilliseconds(started);
    }

    private static void DeleteTemporaryPath(string path)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
                else if (File.Exists(path))
                    File.Delete(path);
                return;
            }
            catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException) && attempt < 9)
            {
                Thread.Sleep(50);
            }
        }
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
        else if (File.Exists(path))
            File.Delete(path);
    }

    private static bool IsWithin(string parent, string child)
    {
        string parentFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        string childFull = Path.GetFullPath(child);
        return childFull.Equals(parentFull, StringComparison.OrdinalIgnoreCase) ||
            childFull.StartsWith(parentFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
    private static string Bound(string value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Length <= MaximumDiagnosticLength ? value : value[..MaximumDiagnosticLength];

    private static long ElapsedMilliseconds(long started) =>
        (long)(Stopwatch.GetTimestamp() - started) * 1000 / Stopwatch.Frequency;

    private sealed class State
    {
        public string Workspace { get; set; } = string.Empty;
        public ModIdentity Mod { get; set; } = new("", "");
        public RimWorldGameLayout? Game { get; set; }
        public List<string> ChangedFiles { get; } = [];
        public List<string> ChangedDefs { get; } = [];
        public List<TypedDefExpectation> ChangedTypedDefs { get; } = [];
        public List<BiomeGenStepExpectation> ExpectedBiomeGenSteps { get; } = [];
        public List<string> TargetErrorTokens { get; } = [];
        public List<string> BuildErrors { get; } = [];
        public List<string> StaticErrors { get; } = [];
        public List<string> InfrastructureErrors { get; } = [];
        public List<string> CleanupErrors { get; } = [];
        public List<string> RelevantErrors { get; } = [];
        public List<string> Processes { get; } = [];
        public Dictionary<string, long> StageTimes { get; } = new(StringComparer.Ordinal);
        public IReadOnlyList<DiscoveredInput> SourceFiles { get; set; } = [];
        public Process? LaunchedProcess { get; set; }
        public bool RuntimeProcessExitConfirmed { get; set; } = true;
        public IReadOnlyList<DiscoveredInput> ProjectFiles { get; set; } = [];
        public IReadOnlyList<DiscoveredInput> XmlFiles { get; set; } = [];
        public ProjectSelectionResult? ProjectSelection { get; set; }
        public string? BuildProject { get; set; }
        public IReadOnlyList<string> TargetAssemblySources { get; set; } = [];
        public string? StorePath { get; set; }
        public string? HelperPackagePath { get; set; }
        public string? HelperStagingPath { get; set; }
        public string? DeploymentPath { get; set; }
        public string? HelperDeploymentPath { get; set; }
        public List<string> DependencyDeploymentPaths { get; } = [];
        public string? SavedataPath { get; set; }
        public string BuildStatus { get; set; } = "BUILD_NOT_RUN";
        public string StaticStatus { get; set; } = "STATIC_NOT_RUN";
        public string InfrastructureStatus { get; set; } = "INFRASTRUCTURE_PASS";
        public string CleanupStatus { get; set; } = "CLEANUP_PASS";
        public long ElapsedMs { get; set; }
        public object? RimContext { get; set; }
        public RuntimeResult Runtime { get; set; } = RuntimeResult.NotRun("NOT_RUN");
    }

    internal sealed record RimWorldGameLayout(string Root, string ExecutablePath, string ContentVersion)
    {
        internal bool IsWaylith => ContentVersion == "1.7";
    }

    internal sealed record ProjectSelectionResult(
        IReadOnlyList<string> BuildRoots,
        IReadOnlyList<string> ProductionProjects,
        IReadOnlyList<string> ExcludedProjects,
        bool IsAmbiguous,
        string? Error);
    private sealed record DiscoveredInput(string AbsolutePath, string RelativePath, string Kind);
    private sealed record ModIdentity(string Name, string PackageId);
    internal sealed record RuntimeModPackage(
        string PackageId,
        string RootPath,
        IReadOnlyList<string> RequiredDependencies);
    private sealed record ProcessResult(int ExitCode, bool TimedOut, string Stdout, string Stderr);
    internal sealed record ProcessLifecycleResult(int ExitCode, bool ControlledTermination);
    private sealed record AssemblyLoadResult(string Name, bool Loaded);
    private sealed record RuntimeResult(
        string Status,
        string? Reason,
        bool ContentLoaded,
        bool TargetLoaded,
        bool TargetConfigured,
        bool DefsValid,
        bool? TargetAssemblyLoaded,
        IReadOnlyList<AssemblyLoadResult> TargetAssemblies,
        object? StartupBehavior,
        IReadOnlyList<string> Evidence,
        IReadOnlyList<string> RelevantErrors,
        double? RuntimeSeconds,
        bool LogProduced)
    {
        public static RuntimeResult NotRun(string reason) =>
            new("RUNTIME_NOT_RUN", reason, false, false, false, false, null, [], null, [], [], null, false);
    }
}

/// <summary>
/// Pure bounded reader for mod-specific JSON evidence in a *previously verified*
/// RimWorld log. This is NOT a game launcher or a log-provenance verifier.
/// It never reads files, opens processes, or interprets a mod's game rules.
/// </summary>
internal sealed class MapQaEventReader
{
    internal const int MaximumLineCharacters = 8_192;
    internal const int MaximumEvents = 128;
    private const int MaximumRejectionCodes = 16;

    private readonly string marker;
    private readonly string expectedNonce;
    private readonly List<MapQaEvent> events = [];
    private readonly List<string> rejectionCodes = [];

    internal MapQaEventReader(string marker, string expectedNonce)
    {
        if (!IsValidMarker(marker))
            throw new ArgumentException("QA marker must be a bounded ASCII [Name] token.", nameof(marker));
        if (!IsValidNonce(expectedNonce))
            throw new ArgumentException("QA run nonce must be 8-40 safe ASCII characters.", nameof(expectedNonce));
        this.marker = marker;
        this.expectedNonce = expectedNonce;
    }

    internal IReadOnlyList<MapQaEvent> Events => events;
    internal IReadOnlyList<string> RejectionCodes => rejectionCodes;
    internal int RejectedCount { get; private set; }
    internal bool HasRejectedEvents => RejectedCount > 0;

    internal MapQaEventDisposition Observe(string? line)
    {
        if (line is null)
            return MapQaEventDisposition.Ignored;

        int start = line.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            return MapQaEventDisposition.Ignored;
        if (line.Length > MaximumLineCharacters)
            return Reject("QA_LINE_TOO_LONG");

        string payload = line[(start + marker.Length)..].Trim();
        if (payload.Length < 2 || payload[0] != '{')
            return Reject("QA_JSON_OBJECT_REQUIRED");
        if (events.Count >= MaximumEvents)
            return Reject("QA_EVENT_LIMIT");

        try
        {
            using JsonDocument document = JsonDocument.Parse(payload, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 12
            });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Reject("QA_JSON_OBJECT_REQUIRED");

            int schemaFields = 0;
            int nonceFields = 0;
            string? schema = null;
            string? nonce = null;
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (property.NameEquals("schemaVersion"))
                {
                    schemaFields++;
                    if (property.Value.ValueKind != JsonValueKind.String)
                        return Reject("QA_SCHEMA_INVALID");
                    schema = property.Value.GetString();
                }
                else if (property.NameEquals("nonce"))
                {
                    nonceFields++;
                    if (property.Value.ValueKind != JsonValueKind.String)
                        return Reject("QA_NONCE_INVALID");
                    nonce = property.Value.GetString();
                }
            }

            if (schemaFields != 1 || nonceFields != 1)
                return Reject("QA_IDENTITY_AMBIGUOUS");
            if (!IsValidSchema(schema))
                return Reject("QA_SCHEMA_INVALID");
            if (!IsValidNonce(nonce))
                return Reject("QA_NONCE_INVALID");
            if (!string.Equals(nonce, expectedNonce, StringComparison.Ordinal))
                return Reject("QA_FOREIGN_NONCE");

            // Preserve the original bounded JSON for the owning mod's
            // separately reviewed assertion schema; no game claim is inferred.
            events.Add(new MapQaEvent(schema!, nonce!, payload));
            return MapQaEventDisposition.Accepted;
        }
        catch (JsonException)
        {
            return Reject("QA_JSON_INVALID");
        }
    }

    private MapQaEventDisposition Reject(string reason)
    {
        if (RejectedCount < int.MaxValue)
            RejectedCount++;
        if (rejectionCodes.Count < MaximumRejectionCodes)
            rejectionCodes.Add(reason);
        return MapQaEventDisposition.Rejected;
    }

    private static bool IsValidMarker(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length is < 3 or > 64 ||
            value[0] != '[' || value[^1] != ']')
            return false;
        for (int i = 1; i < value.Length - 1; i++)
        {
            if (!IsAsciiTokenCharacter(value[i]))
                return false;
        }
        return true;
    }

    private static bool IsValidNonce(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length is < 8 or > 40)
            return false;
        foreach (char character in value)
        {
            if (!IsAsciiTokenCharacter(character))
                return false;
        }
        return true;
    }

    private static bool IsValidSchema(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length is < 4 or > 96)
            return false;
        int separatorCount = 0;
        for (int i = 0; i < value.Length; i++)
        {
            char character = value[i];
            if (character == '/')
            {
                if (i == 0 || i == value.Length - 1)
                    return false;
                separatorCount++;
            }
            else if (!IsAsciiTokenCharacter(character) && character != '.')
            {
                return false;
            }
        }
        return separatorCount == 1;
    }

    private static bool IsAsciiTokenCharacter(char character) =>
        (character >= 'A' && character <= 'Z') ||
        (character >= 'a' && character <= 'z') ||
        (character >= '0' && character <= '9') ||
        character is '_' or '-';
}

internal enum MapQaEventDisposition
{
    Ignored,
    Accepted,
    Rejected
}

internal sealed record MapQaEvent(string SchemaVersion, string Nonce, string PayloadJson);
