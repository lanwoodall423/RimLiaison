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
    private const int MaximumDefinitionExpectations = 128;
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RuntimeTimeout = TimeSpan.FromSeconds(120);
    internal static TimeSpan WaylithContentLoadTimeout => RuntimeTimeout;

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
                string rimWorldRoot = ResolveRimWorldRoot();
                state.HelperPackagePath = await BuildHelperAsync(
                        rimWorldRoot,
                        state,
                        cancellationToken)
                    .ConfigureAwait(false);
                state.Runtime = await RunRuntimeAsync(
                        rimWorldRoot,
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
            if (extension.Equals(".xml", StringComparison.OrdinalIgnoreCase) &&
                relative.StartsWith("1.", StringComparison.OrdinalIgnoreCase) &&
                !relative.StartsWith("1.7/", StringComparison.OrdinalIgnoreCase))
                continue;
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
            return ResolveRimWorldRoot();
        }
        catch (Exception exception) when (exception is DirectoryNotFoundException or FileNotFoundException)
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
                if (!input.RelativePath.StartsWith("Defs/", StringComparison.OrdinalIgnoreCase) &&
                    !input.RelativePath.StartsWith("1.7/Defs/", StringComparison.OrdinalIgnoreCase))
                    continue;
                XmlNodeList? thingNodes = document.SelectNodes("//ThingDef");
                if (thingNodes is null)
                    continue;
                foreach (XmlNode thing in thingNodes)
                {
                    AddDefinitionExpectation(state, "ThingDef", thing, input.RelativePath);
                    XmlNodeList? projectileNodes = thing.SelectNodes(".//defaultProjectile");
                    if (projectileNodes is not null)
                        foreach (XmlNode node in projectileNodes)
                        {
                            string value = node.InnerText.Trim();
                            if (!string.IsNullOrWhiteSpace(value))
                                state.TargetErrorTokens.Add(value);
                        }
                    XmlNodeList? textureNodes = thing.SelectNodes(".//texPath");
                    if (textureNodes is not null)
                        foreach (XmlNode node in textureNodes)
                        {
                            string value = node.InnerText.Trim();
                            if (!string.IsNullOrWhiteSpace(value))
                                state.TargetErrorTokens.Add(value);
                        }
                }
                foreach (string type in new[] { "GenStepDef", "TileMutatorDef" })
                {
                    XmlNodeList? typedNodes = document.SelectNodes("//" + type);
                    if (typedNodes is null) continue;
                    foreach (XmlNode typed in typedNodes)
                    {
                        AddDefinitionExpectation(state, type, typed, input.RelativePath);
                    }
                }
            }
        }
        finally
        {
            state.StageTimes["xml"] = ElapsedMilliseconds(started);
        }
    }

    private static void AddDefinitionExpectation(
        State state, string type, XmlNode node, string relativePath)
    {
        string name = node.SelectSingleNode("./defName")?.InnerText.Trim() ?? string.Empty;
        string mayRequire = node.Attributes?["MayRequire"]?.Value.Trim() ?? string.Empty;
        static bool SafeToken(string token) =>
            token.Length is > 0 and <= 128 &&
            token.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-');
        if (!SafeToken(name) || (mayRequire.Length > 0 && !SafeToken(mayRequire)))
            throw new InvalidDataException("Invalid " + type + " identity or MayRequire in " + relativePath + ".");
        string expectation = type + "|" + name +
            (mayRequire.Length > 0 ? "|" + mayRequire : string.Empty);
        if (state.ChangedDefs.Any(existing =>
                existing.Split('|')[0] == type && existing.Split('|')[1] == name))
            throw new InvalidDataException("Duplicate active " + type + " definition in " + relativePath + ".");
        if (state.ChangedDefs.Count >= MaximumDefinitionExpectations)
            throw new InvalidDataException("Active definition expectation limit exceeded.");
        state.ChangedDefs.Add(expectation);
    }

    // Reuses the actual production discovery and parsing path for inert test fixtures.
    internal static IReadOnlyList<string> InspectActiveDefinitionExpectations(string workspace)
    {
        var state = new State
        {
            XmlFiles = DiscoverInputs(workspace)
                .Where(input => input.Kind == DiscoveredFileKinds.Xml).ToArray()
        };
        ValidateXml(state);
        return state.ChangedDefs.ToArray();
    }

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

    private static async Task<string> BuildHelperAsync(string rimWorldRoot, State state, CancellationToken cancellationToken)
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
            // The CLI's .NET 8 global.json must not select an SDK for the
            // Waylith net10 runtime helper. Only this owned temp dir is written.
            string helperWorkingDirectory = PrepareWaylithHelperBuildDirectory(staging);
            ProcessResult result = await RunProcessAsync(
                    "dotnet",
                    ["build", project, "--configuration", "Release", "--nologo",
                     "-p:RimWorldRoot=" + rimWorldRoot,
                     "-p:DevCheckRuntimePackageRoot=" + staging + Path.DirectorySeparatorChar],
                    helperWorkingDirectory,
                    BuildTimeout,
                    state,
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.ExitCode != 0 || result.TimedOut)
                throw new InvalidOperationException("Runtime signal helper build failed: " +
                    Bound(result.Stdout + Environment.NewLine + result.Stderr));
            string assembly = Path.Combine(staging, "1.7", "Assemblies", "DirectValidationRuntimeSignal.dll");
            if (!File.Exists(assembly))
                throw new InvalidOperationException("Runtime signal helper build produced no 1.7 assembly.");
            return staging;
        }
        finally
        {
            state.StageTimes["helperBuild"] = ElapsedMilliseconds(started);
        }
    }

    internal static string PrepareWaylithHelperBuildDirectory(string staging)
    {
        if (!Directory.Exists(staging))
            throw new DirectoryNotFoundException("Owned helper build staging directory is missing.");
        const string selector =
            "{\"sdk\":{\"version\":\"10.0.100\",\"rollForward\":\"latestFeature\",\"allowPrerelease\":false}}";
        File.WriteAllText(Path.Combine(staging, "global.json"), selector, new UTF8Encoding(false));
        return staging;
    }

    private static string ResolveRimWorldRoot()
    {
        string? configured = Environment.GetEnvironmentVariable("RIMWORLD_ROOT");
        string root = string.IsNullOrWhiteSpace(configured)
            ? @"C:\Games\Steam\steamapps\common\RimWorld"
            : configured;
        return ValidateWaylithGameRoot(root);
    }

    // The only supported runtime is 1.7 Waylith. Partial/mixed 1.6 roots
    // must fail before the game is launched or temporary Mods are staged.
    internal static string ValidateWaylithGameRoot(string root)
    {
        string full = Path.GetFullPath(root);
        if (!Directory.Exists(full) ||
            !Directory.Exists(Path.Combine(full, "Data")) ||
            !Directory.Exists(Path.Combine(full, "Mods")))
            throw new DirectoryNotFoundException("Waylith 1.7 game root, Data or Mods is missing: " + full);

        if (File.Exists(Path.Combine(full, "RimWorldWin64.exe")) ||
            File.Exists(Path.Combine(full, "RimWorldWin64_Data", "Managed", "Assembly-CSharp.dll")))
            throw new InvalidDataException("Mixed or legacy RimWorld 1.6 engine root is unsupported.");

        foreach (string required in new[]
                 {
                     "RimWorld.exe", "Assembly-CSharp.dll",
                     "Waylith.dll", "UnityStubs.dll"
                 })
            if (!File.Exists(Path.Combine(full, required)))
                throw new FileNotFoundException("Incomplete Waylith 1.7 root, missing " + required);
        return full;
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
            Path.Combine(projectDirectory, "bin", "Release", "net10.0", assemblyName + ".dll"),
            Path.Combine(workspace, "1.7", "Assemblies", assemblyName + ".dll")
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    // Pure launch configuration: only the 1.7 Waylith apphost and an explicit,
    // isolated list of target/helper/dependencies may be started later by
    // the separately authorized runtime lease.
    internal static ProcessStartInfo CreateWaylithRuntimeStartInfo(
        string rimWorldRoot,
        string savedata,
        IReadOnlyList<string> activePackages)
    {
        string[] selected = activePackages
            .Where(id => !id.Equals("ludeon.rimworld", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (selected.Length < 2 ||
            !selected.Contains(HelperPackageId, StringComparer.OrdinalIgnoreCase) ||
            selected.All(id => id.Equals(HelperPackageId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Waylith isolated launch requires the runtime helper and a target mod.");
        var start = new ProcessStartInfo(Path.Combine(rimWorldRoot, "RimWorld.exe"))
        {
            WorkingDirectory = rimWorldRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-savedatafolder=" + savedata);
        start.ArgumentList.Add("-mod=" + string.Join(";", selected));
        start.ArgumentList.Add("-Headless");
        return start;
    }

    internal static bool IsWaylithTargetSelected(ProcessStartInfo start, string packageId) =>
        start.ArgumentList.Where(arg => arg.StartsWith("-mod=", StringComparison.OrdinalIgnoreCase))
            .SelectMany(arg => arg[5..].Split(';', StringSplitOptions.RemoveEmptyEntries))
            .Any(id => id.Equals(packageId, StringComparison.OrdinalIgnoreCase));

    private static void EnsureNoConcurrentWaylithGame()
    {
        // The game's Player.log is shared even with an isolated savedata
        // folder; refuse to race another game before staging any mod files.
        Process[] otherGames = Process.GetProcessesByName("RimWorld");
        try
        {
            if (otherGames.Length > 0)
                throw new InvalidOperationException(
                    "WAYLITH_LOG_CONCURRENT_GAME: close RimWorld before isolated dev-check.");
        }
        finally
        {
            foreach (Process game in otherGames)
                game.Dispose();
        }
    }

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
        string rimWorldRoot,
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
                    content, before.Path, rimWorldRoot, start, childStartedUtc,
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
        string rimWorldRoot,
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
            !command.Contains(Path.Combine(rimWorldRoot, "RimWorld.dll"),
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
        string rimWorldRoot,
        State state,
        CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        string modsRoot = Path.Combine(rimWorldRoot, "Mods");
        EnsureNoConcurrentWaylithGame();
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
            string targetAssemblyDirectory = Path.Combine(deployment, "1.7", "Assemblies");
            foreach (string source in state.TargetAssemblySources)
            {
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
            state.ActivePackages = activePackages;
            WriteModsConfig(configPath, activePackages);
            string signalPath = Path.Combine(savedata, "direct-validation-signal.json");
            string behaviorPath = Path.Combine(savedata, "direct-validation-behavior.json");
            string logPath = ResolveWaylithLogPath();
            WaylithLogSnapshot priorGlobalLog = CaptureWaylithLogSnapshot(logPath);
            ProcessStartInfo start = CreateWaylithRuntimeStartInfo(rimWorldRoot, savedata, activePackages);
            start.Environment["DIRECT_VALIDATION_TARGET_PACKAGE"] = state.Mod.PackageId;
            start.Environment["DIRECT_VALIDATION_TARGET_DEFS"] = string.Join(';', state.ChangedDefs);
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
            state.Processes.Add("RimWorld.exe");
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            DateTime deadline = DateTime.UtcNow + RuntimeTimeout;
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
            bool logVerified = TryReadVerifiedWaylithLog(priorGlobalLog, rimWorldRoot,
                start, childStartedUtc, childExitedUtc, out string log, out string logFailure);
            state.RelevantErrors.AddRange(FindTargetErrors(log, state));
            bool targetConfigured = IsWaylithTargetSelected(start, state.Mod.PackageId);
            RuntimeResult result = ParseRuntime(
                signal,
                state,
                behaviorPath,
                targetConfigured,
                completed.ControlledTermination,
                completed.ExitCode);
            if (!logVerified)
                result = result with { Status = "RUNTIME_LOAD_FAIL", Reason = logFailure };
            return result with
            {
                RuntimeSeconds = Math.Round(ElapsedMilliseconds(started) / 1000d, 3),
                LogProduced = logVerified,
                Evidence = [.. result.Evidence,
                    "waylithLogVerified=" + logVerified,
                    "waylithLogStatus=" + logFailure]
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

    // Check the full set of exact typed expectations, not merely a count or
    // matching name. Optional DLC defs must explicitly report inapplicability.
    internal static bool ValidateTypedDefinitionEvidence(
        IReadOnlyList<string> expectations, JsonElement? signal,
        IReadOnlyList<string> activePackages)
    {
        if (expectations.Count == 0)
            return true; // A legitimate code-only mod has no Defs to attest.
        if (!signal.HasValue || signal.Value.ValueKind != JsonValueKind.Object ||
            !signal.Value.TryGetProperty("requestedDefs", out JsonElement reported) ||
            reported.ValueKind != JsonValueKind.Array ||
            reported.GetArrayLength() != expectations.Count)
            return false;

        var pending = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string expectation in expectations)
        {
            string[] parts = expectation.Split('|');
            if ((parts.Length != 2 && parts.Length != 3) ||
                parts[1].Length == 0 ||
                (parts[0] != "ThingDef" && parts[0] != "GenStepDef" && parts[0] != "TileMutatorDef") ||
                !pending.TryAdd(parts[0] + "|" + parts[1], parts.Length == 3 ? parts[2] : ""))
                return false;
        }

        foreach (JsonElement item in reported.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("defType", out JsonElement type) ||
                type.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("defName", out JsonElement name) ||
                name.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("applicable", out JsonElement applicable) ||
                (applicable.ValueKind != JsonValueKind.True && applicable.ValueKind != JsonValueKind.False) ||
                !item.TryGetProperty("exists", out JsonElement exists) ||
                (exists.ValueKind != JsonValueKind.True && exists.ValueKind != JsonValueKind.False))
                return false;
            if (!pending.Remove(type.GetString() + "|" + name.GetString(), out string? requiredPackage))
                return false;
            bool expectedApplicable = string.IsNullOrEmpty(requiredPackage) ||
                activePackages.Contains(requiredPackage, StringComparer.OrdinalIgnoreCase);
            if ((applicable.ValueKind == JsonValueKind.True) != expectedApplicable ||
                (exists.ValueKind == JsonValueKind.True) != expectedApplicable)
                return false;
        }
        return pending.Count == 0;
    }

    private static RuntimeResult ParseRuntime(JsonElement? signal, State state, string behaviorPath, bool targetConfigured, bool controlledTermination, int? exitCode)
    {
        bool contentLoaded = false;
        bool targetLoaded = false;
        bool defsValid = ValidateTypedDefinitionEvidence(
            state.ChangedDefs, signal, state.ActivePackages);
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
        string[] tokens = [state.Mod.PackageId, state.Mod.Name, .. state.ChangedDefs.Select(item => item.Split('|')[1]), .. state.TargetErrorTokens, .. state.ChangedFiles.Select(Path.GetFileName)];
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
        // Historical 1.6 and other runtime directories are source inputs,
        // not deployable content in this 1.7-only game test.
        string waylith = Path.Combine(source, "1.7");
        if (Directory.Exists(waylith))
            CopyDirectory(waylith, Path.Combine(destination, "1.7"));
    }

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
        public List<string> ChangedFiles { get; } = [];
        public List<string> ChangedDefs { get; } = [];
        public IReadOnlyList<string> ActivePackages { get; set; } = [];
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
